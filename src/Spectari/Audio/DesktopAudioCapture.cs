using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spectari.Audio;

internal enum DefaultAudioDeviceChangeAction
{
    None,
    Bind,
    Unbind,
}

/// <summary>Decides when a live desktop capture must follow a new default endpoint.</summary>
internal sealed class DefaultAudioDeviceChangePolicy
{
    internal string? BoundDeviceId { get; private set; }

    internal DefaultAudioDeviceChangeAction Evaluate(string? defaultDeviceId)
    {
        if (string.IsNullOrEmpty(defaultDeviceId))
            return BoundDeviceId is null
                ? DefaultAudioDeviceChangeAction.None
                : DefaultAudioDeviceChangeAction.Unbind;

        return string.Equals(BoundDeviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase)
            ? DefaultAudioDeviceChangeAction.None
            : DefaultAudioDeviceChangeAction.Bind;
    }

    internal void MarkBound(string deviceId) => BoundDeviceId = deviceId;
    internal void MarkUnbound() => BoundDeviceId = null;
}

/// <summary>
/// Captures the current default Windows render endpoint through WASAPI loopback.
/// Output is 48 kHz stereo float32, matching the process capture and ffmpeg input.
/// The default endpoint identity is checked while streaming, and the WASAPI client
/// is rebound in place when it changes. Silence advances the raw-audio timeline
/// while no endpoint is available or a replacement is being opened.
/// </summary>
internal sealed class DesktopAudioCapture : IDisposable
{
    private const int SampleRate = ProcessAudioCapture.SampleRate;
    private const int Channels = ProcessAudioCapture.Channels;
    private const int BytesPerFrame = Channels * 4;
    private const long MaxCatchupBytes = SampleRate * BytesPerFrame * 2;
    private static readonly byte[] SilenceBlock = new byte[SampleRate * BytesPerFrame / 100];

    private readonly Action<byte[], int> _onSamples;
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue = new(256);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _captureThread;
    private readonly Thread _writerThread;
    private readonly long _captureStartTicks;
    private readonly long _leadInFrames;
    private long _deliveredFrames;
    private long _owedSilenceBytes;
    private int _disposed;

    internal DesktopAudioCapture(long videoEpochTicks, Action<byte[], int> onSamples)
    {
        _onSamples = onSamples;
        _captureStartTicks = Stopwatch.GetTimestamp();
        _leadInFrames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, _captureStartTicks);

        int leadInBytes = checked((int)(_leadInFrames * BytesPerFrame));
        if (leadInBytes > 0)
        {
            _queue.Add((new byte[leadInBytes], leadInBytes));
            _deliveredFrames = _leadInFrames;
        }

        long leadInMs = (_leadInFrames * 1000 + SampleRate / 2) / SampleRate;
        Console.WriteLine($"[audio] aligned to video timeline (+{leadInMs} ms lead-in silence)");

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "desktop-audio-writer",
            Priority = ThreadPriority.AboveNormal,
        };
        _writerThread.Start();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "desktop-audio-capture",
            Priority = ThreadPriority.Highest,
        };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        const int COINIT_MULTITHREADED = 0;
        int initializeHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        bool comInitialized = initializeHr >= 0;
        IMMDeviceEnumerator? enumerator = null;
        DesktopAudioBinding? binding = null;
        IntPtr mmcss = IntPtr.Zero;

        try
        {
            Marshal.ThrowExceptionForHR(initializeHr);
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            mmcss = ConfigureMmcss();
            Console.WriteLine(
                $"[audio] capture started (desktop loopback, 48 kHz stereo{(mmcss != IntPtr.Zero ? ", MMCSS Pro Audio" : ", thread priority Highest")})");

            var devicePolicy = new DefaultAudioDeviceChangePolicy();
            bool endpointFailureLogged = false;
            bool readFailureLogged = false;
            bool overflowLogged = false;
            long nextDefaultCheckTicks = 0;
            long checkIntervalTicks = Stopwatch.Frequency / 10;
            long lastPacketTicks = _captureStartTicks;
            long idleAfterTicks = Stopwatch.Frequency * 150 / 1000;
            bool idle = false;
            long idleAnchorFrames = 0;
            long idleAnchorTicks = 0;

            while (!_cts.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                if (binding is null || now >= nextDefaultCheckTicks)
                {
                    nextDefaultCheckTicks = now + checkIntervalTicks;
                    string? defaultDeviceId;
                    try
                    {
                        defaultDeviceId = GetDefaultRenderDeviceId(enumerator);
                    }
                    catch (Exception ex)
                    {
                        if (!endpointFailureLogged)
                        {
                            endpointFailureLogged = true;
                            Console.Error.WriteLine(
                                $"[audio] default output device query failed ({ex.Message}); feeding silence and retrying.");
                        }
                        ReleaseBinding(ref binding, devicePolicy);
                        EmitSilenceToWallClock(now);
                        Thread.Sleep(10);
                        continue;
                    }

                    DefaultAudioDeviceChangeAction action = devicePolicy.Evaluate(defaultDeviceId);
                    if (action != DefaultAudioDeviceChangeAction.None)
                    {
                        bool replacingDevice = binding is not null && defaultDeviceId is not null;
                        ReleaseBinding(ref binding, devicePolicy);
                        EmitSilenceToWallClock(now);
                        idle = false;

                        if (replacingDevice)
                            Console.WriteLine("[audio] default output device changed; switching desktop audio capture");

                        if (action == DefaultAudioDeviceChangeAction.Bind && defaultDeviceId is not null)
                        {
                            try
                            {
                                binding = DesktopAudioBinding.Open(enumerator, defaultDeviceId);
                                EmitSilenceToWallClock(binding.StartTicks);
                                devicePolicy.MarkBound(defaultDeviceId);
                                endpointFailureLogged = false;
                                readFailureLogged = false;
                                lastPacketTicks = binding.StartTicks;
                            }
                            catch (Exception ex)
                            {
                                if (!endpointFailureLogged)
                                {
                                    endpointFailureLogged = true;
                                    Console.Error.WriteLine(
                                        $"[audio] desktop loopback device open failed ({ex.Message}); feeding silence and retrying.");
                                }
                            }
                        }
                        else if (!endpointFailureLogged)
                        {
                            endpointFailureLogged = true;
                            Console.Error.WriteLine(
                                "[audio] no default output device is available; feeding silence until one appears.");
                        }
                    }
                }

                if (binding is null)
                {
                    EmitSilenceToWallClock(Stopwatch.GetTimestamp());
                    Thread.Sleep(10);
                    continue;
                }

                bool gotPacket = false;
                bool bindingFailed = false;
                while (true)
                {
                    int hr = binding.Capture.GetNextPacketSize(out uint packetFrames);
                    if (hr < 0)
                    {
                        if (!readFailureLogged)
                        {
                            readFailureLogged = true;
                            Console.Error.WriteLine(
                                $"[audio] desktop loopback read failed (0x{hr:X8}); rebinding the default output device.");
                        }
                        ReleaseBinding(ref binding, devicePolicy);
                        bindingFailed = true;
                        break;
                    }
                    if (packetFrames == 0) break;

                    hr = binding.Capture.GetBuffer(
                        out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (hr < 0)
                    {
                        if (!readFailureLogged)
                        {
                            readFailureLogged = true;
                            Console.Error.WriteLine(
                                $"[audio] desktop loopback read failed (0x{hr:X8}); rebinding the default output device.");
                        }
                        ReleaseBinding(ref binding, devicePolicy);
                        bindingFailed = true;
                        break;
                    }

                    try
                    {
                        gotPacket = true;
                        int bytes = checked((int)frames * BytesPerFrame);
                        var buffer = new byte[bytes];
                        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                            Marshal.Copy(data, buffer, 0, bytes);
                        Emit(buffer, bytes, ref overflowLogged);
                    }
                    finally
                    {
                        binding?.Capture.ReleaseBuffer(frames);
                    }
                }

                now = Stopwatch.GetTimestamp();
                if (bindingFailed)
                {
                    EmitSilenceToWallClock(now);
                }
                else if (gotPacket)
                {
                    lastPacketTicks = now;
                    idle = false;
                }
                else if (now - lastPacketTicks > idleAfterTicks)
                {
                    if (!idle)
                    {
                        idle = true;
                        idleAnchorFrames = _deliveredFrames + _owedSilenceBytes / BytesPerFrame;
                        idleAnchorTicks = lastPacketTicks;
                    }

                    long targetFrames = idleAnchorFrames
                        + (now - idleAnchorTicks) * SampleRate / Stopwatch.Frequency;
                    if (targetFrames > _deliveredFrames)
                        EmitSilence((targetFrames - _deliveredFrames) * BytesPerFrame);
                    if (_deliveredFrames >= targetFrames)
                        _owedSilenceBytes = 0;
                }

                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[audio] desktop capture thread stopped on error ({ex.Message}); feeding silence instead.");
                while (!_cts.IsCancellationRequested)
                {
                    EmitSilenceToWallClock(Stopwatch.GetTimestamp());
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            binding?.Dispose();
            if (enumerator is not null)
            {
                try { Marshal.FinalReleaseComObject(enumerator); } catch { }
            }
            RevertMmcss(mmcss);
            if (comInitialized) CoUninitialize();
        }
    }

    private static void ReleaseBinding(
        ref DesktopAudioBinding? binding,
        DefaultAudioDeviceChangePolicy devicePolicy)
    {
        binding?.Dispose();
        binding = null;
        devicePolicy.MarkUnbound();
    }

    private static string? GetDefaultRenderDeviceId(IMMDeviceEnumerator enumerator)
    {
        const int E_RENDER = 0;
        const int E_CONSOLE = 0;
        const int HRESULT_NOT_FOUND = unchecked((int)0x80070490);

        int hr = enumerator.GetDefaultAudioEndpoint(E_RENDER, E_CONSOLE, out IMMDevice device);
        if (hr == HRESULT_NOT_FOUND) return null;
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(device.GetId(out string deviceId));
            return deviceId;
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(device); } catch { }
        }
    }

    private void EmitSilenceToWallClock(long nowTicks)
    {
        long elapsedTicks = Math.Max(0, nowTicks - _captureStartTicks);
        long targetFrames = _leadInFrames
            + elapsedTicks * SampleRate / Stopwatch.Frequency;
        if (targetFrames > _deliveredFrames)
            EmitSilence((targetFrames - _deliveredFrames) * BytesPerFrame);
        if (_deliveredFrames >= targetFrames)
            _owedSilenceBytes = 0;
    }

    private bool TryEnqueue((byte[] Buffer, int Length) item)
    {
        try { return _queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; }
    }

    private void OweSilence(long bytes) =>
        _owedSilenceBytes = Math.Min(_owedSilenceBytes + bytes, MaxCatchupBytes);

    private void RepayOwedSilence()
    {
        while (_owedSilenceBytes > 0)
        {
            int chunk = (int)Math.Min(_owedSilenceBytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk))) break;
            _owedSilenceBytes -= chunk;
            _deliveredFrames += chunk / BytesPerFrame;
        }
    }

    private void Emit(byte[] buffer, int length, ref bool overflowLogged)
    {
        if (TryEnqueue((buffer, length)))
        {
            _deliveredFrames += length / BytesPerFrame;
        }
        else
        {
            if (_queue.TryTake(out var stale))
            {
                _deliveredFrames -= stale.Length / BytesPerFrame;
                OweSilence(stale.Length);
            }

            if (TryEnqueue((buffer, length)))
                _deliveredFrames += length / BytesPerFrame;
            else
                OweSilence(length);

            if (!overflowLogged)
            {
                overflowLogged = true;
                Console.Error.WriteLine(
                    "[audio] encoder is not draining desktop audio; dropping stale audio to stay at the live edge; backfilling silence to resync.");
            }
        }

        RepayOwedSilence();
    }

    private void EmitSilence(long bytes)
    {
        while (bytes > 0 && !_cts.IsCancellationRequested)
        {
            int chunk = (int)Math.Min(bytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk)))
            {
                OweSilence(bytes);
                return;
            }
            _deliveredFrames += chunk / BytesPerFrame;
            bytes -= chunk;
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var (buffer, length) in _queue.GetConsumingEnumerable())
            {
                _onSamples(buffer, length);
                if (_cts.IsCancellationRequested) return;
            }
        }
        catch
        {
            // The pipe consumer can disappear during stream teardown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        bool captureStopped = _captureThread.Join(2000);
        _queue.CompleteAdding();
        _writerThread.Join(2000);
        // A delayed COM call may still return to the capture loop. Keep its
        // cancellation source alive until that thread is definitely gone.
        if (captureStopped) _cts.Dispose();
    }

    private sealed class DesktopAudioBinding : IDisposable
    {
        private readonly IAudioClient _client;
        private int _disposed;

        private DesktopAudioBinding(
            IAudioClient client,
            IAudioCaptureClient capture,
            long startTicks)
        {
            _client = client;
            Capture = capture;
            StartTicks = startTicks;
        }

        internal IAudioCaptureClient Capture { get; }
        internal long StartTicks { get; }

        internal static DesktopAudioBinding Open(
            IMMDeviceEnumerator enumerator,
            string deviceId)
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out IMMDevice device));
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            bool started = false;
            try
            {
                var audioClientIid = typeof(IAudioClient).GUID;
                const uint CLSCTX_ALL = 23;
                Marshal.ThrowExceptionForHR(device.Activate(
                    ref audioClientIid, CLSCTX_ALL, IntPtr.Zero, out object activated));
                client = (IAudioClient)activated;

                var format = new WAVEFORMATEX
                {
                    wFormatTag = 3,
                    nChannels = Channels,
                    nSamplesPerSec = SampleRate,
                    nAvgBytesPerSec = SampleRate * BytesPerFrame,
                    nBlockAlign = BytesPerFrame,
                    wBitsPerSample = 32,
                };

                const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
                const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
                const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
                const long BUFFER_DURATION_100NS = 5_000_000;
                uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK
                    | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                Marshal.ThrowExceptionForHR(client.Initialize(
                    0, streamFlags, BUFFER_DURATION_100NS, 0, ref format, IntPtr.Zero));

                var captureIid = typeof(IAudioCaptureClient).GUID;
                Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out IntPtr capturePtr));
                try
                {
                    capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
                }
                finally
                {
                    Marshal.Release(capturePtr);
                }

                long startTicks = Stopwatch.GetTimestamp();
                Marshal.ThrowExceptionForHR(client.Start());
                started = true;
                return new DesktopAudioBinding(client, capture, startTicks);
            }
            catch
            {
                if (started && client is not null)
                {
                    try { client.Stop(); } catch { }
                }
                if (capture is not null)
                {
                    try { Marshal.FinalReleaseComObject(capture); } catch { }
                }
                if (client is not null)
                {
                    try { Marshal.FinalReleaseComObject(client); } catch { }
                }
                throw;
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(device); } catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _client.Stop(); } catch { }
            try { Marshal.FinalReleaseComObject(Capture); } catch { }
            try { Marshal.FinalReleaseComObject(_client); } catch { }
        }
    }

    private static IntPtr ConfigureMmcss()
    {
        try
        {
            uint taskIndex = 0;
            return AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void RevertMmcss(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(
            ref Guid iid,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
