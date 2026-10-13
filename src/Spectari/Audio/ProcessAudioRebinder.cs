using System.Diagnostics;
using Spectari.Capture;

namespace Spectari.Audio;

internal sealed class ProcessAudioRebinder : IDisposable
{
    private readonly object _gate = new();
    private readonly IWindowAudioTarget _target;
    private readonly Func<uint, long, Action<byte[], int>, IDisposable> _create;
    private readonly AudioSegmentOutput _output;
    private IDisposable? _active;
    private uint _currentPid;
    private uint _requestedPid;
    private long _generation = 1;
    private bool _disposed;
    private bool _workerRunning;
    private Task _worker = Task.CompletedTask;

    internal ProcessAudioRebinder(IWindowAudioTarget target, long epoch, Action<byte[], int> write,
        Func<uint, long, Action<byte[], int>, IDisposable>? create = null)
    {
        _target = target;
        _create = create ?? ((pid, start, emit) => new ProcessAudioCapture(pid, start, emit));
        _output = new AudioSegmentOutput(epoch, write);
        _currentPid = _requestedPid = target.AudioProcessId;
        _active = _create(_currentPid, epoch, (data, length) => _output.Write(1, data, length));
        _target.ProcessAttached += OnAttached;
        // Subscribe before resampling so an attachment during startup cannot be lost.
        OnAttached(_target.AudioProcessId);
    }

    private void OnAttached(uint pid) => _ = RebindAsync(pid);

    internal Task RebindAsync(uint pid)
    {
        lock (_gate)
        {
            if (_disposed || pid == 0) return Task.CompletedTask;
            _requestedPid = pid;
            if (!_workerRunning && _currentPid != pid)
            {
                _workerRunning = true;
                _worker = Task.Run(RebindLoop);
            }
            return _worker;
        }
    }

    private void RebindLoop()
    {
        while (true)
        {
            uint pid;
            long generation;
            lock (_gate)
            {
                if (_disposed || _requestedPid == _currentPid) { _workerRunning = false; return; }
                pid = _requestedPid;
                generation = ++_generation;
            }
            IDisposable? replacement = null;
            IDisposable? retired = null;
            try
            {
                // A replacement has a local epoch. The output owner retains the
                // session byte count and aligns only the gap at publication.
                replacement = _create(pid, Stopwatch.GetTimestamp(),
                    (data, length) => _output.Write(generation, data, length));
                lock (_gate)
                {
                    if (_disposed) { _workerRunning = false; return; }
                    if (_requestedPid != pid) continue;
                    _output.Begin(generation);
                    retired = _active;
                    _active = replacement;
                    replacement = null;
                    _currentPid = pid;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[audio] follow-window rebind failed: {ex.Message}");
                lock (_gate)
                {
                    if (_disposed || _requestedPid == pid) { _workerRunning = false; return; }
                }
            }
            finally
            {
                if (replacement is not null) CaptureRetirement.Start(replacement);
                if (retired is not null) CaptureRetirement.Start(retired);
            }
        }
    }

    public void Dispose()
    {
        IDisposable? active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _target.ProcessAttached -= OnAttached;
            _output.Stop();
            active = _active;
            _active = null;
        }
        if (active is not null) CaptureRetirement.Start(active);
    }
}

internal sealed class AudioSegmentOutput(long epoch, Action<byte[], int> write, Func<long>? clock = null)
{
    private const int BytesPerFrame = ProcessAudioCapture.Channels * 4;
    private readonly object _gate = new();
    private readonly byte[] _silence = new byte[ProcessAudioCapture.SampleRate / 100 * BytesPerFrame];
    private readonly Func<long> _now = clock ?? Stopwatch.GetTimestamp;
    private long _generation = 1;
    private long _framesWritten;
    private bool _align;
    private bool _stopped;

    internal void Begin(long generation) { lock (_gate) { _generation = generation; _align = true; } }
    internal void Stop() { lock (_gate) _stopped = true; }
    internal void Write(long generation, byte[] data, int length)
    {
        lock (_gate)
        {
            if (_stopped || generation != _generation) return;
            if (_align)
            {
                long elapsedFrames = (long)(Math.Max(0, _now() - epoch)
                    * (double)ProcessAudioCapture.SampleRate / Stopwatch.Frequency);
                long gap = Math.Clamp(elapsedFrames - _framesWritten - length / BytesPerFrame,
                    0, ProcessAudioCapture.SampleRate * 5);
                while (gap > 0)
                {
                    int frames = (int)Math.Min(gap, _silence.Length / BytesPerFrame);
                    write(_silence, frames * BytesPerFrame);
                    _framesWritten += frames;
                    gap -= frames;
                }
                _align = false;
            }
            write(data, length);
            _framesWritten += length / BytesPerFrame;
        }
    }
}
