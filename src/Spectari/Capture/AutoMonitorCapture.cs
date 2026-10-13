using System.Diagnostics;

namespace Spectari.Capture;

/// <summary>
/// Self-managing monitor capture. DEFAULT backend is DXGI desktop duplication
/// (full-rate delivery - 141 fps measured where WGC capped at ~50 - sees
/// exclusive fullscreen, and composites the cursor itself since v0.12).
/// Windows.Graphics.Capture is the fallback when duplication can't start
/// (rotated display, another capture app holding the output) or dies. If WGC
/// is active and starves while a fullscreen app is focused, we hot-swap back
/// to duplication. The stream never notices a swap: dimensions are identical
/// and the frame counter stays monotonic.
/// </summary>
public sealed class AutoMonitorCapture : ICaptureSource
{
    private const int StarvationMs = 2500;   // no fresh frames for this long...
    private const int SwapCooldownMs = 15000; // ...and don't thrash between backends

    private readonly IntPtr _hMonitor;
    private readonly Lock _swapLock = new();
    // Native retirement waits for readers without holding the publication lock.
    private readonly object _readLock = new();
    private readonly Thread? _watchdog;
    private readonly Func<bool, ICaptureSource> _createCapture;
    private readonly CancellationToken _stop;
    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();

    private ICaptureSource? _active;
    private bool _usingDuplication;
    private long _versionOffset;
    private long _lastSeenVersion;
    private long _lastFreshTicks = Stopwatch.GetTimestamp();
    private long _lastSwapAttemptTicks;
    private volatile Exception? _bothFailed;
    private bool _cursorEnabled = true;

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId { get; }
    public string AdapterName { get; }
    public string AdapterLuid { get; }
    public string DriverVersion { get; }

    public AutoMonitorCapture(IntPtr hMonitor)
        : this(hMonitor, duplication => duplication ? new DuplicationCapture(hMonitor)
            : ScreenCapture.ForMonitor(hMonitor), true) { }

    internal AutoMonitorCapture(IntPtr hMonitor, Func<bool, ICaptureSource> createCapture, bool startWatchdog)
    {
        _createCapture = createCapture;
        _stop = _cts.Token;
        _hMonitor = hMonitor;
        try
        {
            _active = _createCapture(true);
            _usingDuplication = true;
        }
        catch (Exception ex)
        {
            // Duplication can't start here (rotated display, another capture app,
            // transition in progress) - fall back to standard capture.
            Console.WriteLine($"[capture] desktop duplication unavailable ({ex.Message}); using standard capture");
            _active = _createCapture(false);
        }
        Width = _active.Width;
        Height = _active.Height;
        GpuVendorId = _active.GpuVendorId;
        AdapterName = _active.AdapterName;
        AdapterLuid = _active.AdapterLuid;
        DriverVersion = _active.DriverVersion;

        if (startWatchdog)
        {
            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "capture-watchdog" };
            _watchdog.Start();
        }
    }

    public long FrameVersion { get { lock (_swapLock) return _versionOffset + (_active?.FrameVersion ?? 0); } }
    public long FramesArrived => FrameVersion;
    public Exception? CaptureError => _bothFailed;
    public bool CursorEnabled { set { lock (_swapLock) { _cursorEnabled = value; if (_active is not null) _active.CursorEnabled = value; } } }

    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
    {
        lock (_readLock)
        {
        ICaptureSource? active;
        long offset;
        lock (_swapLock) { active = _active; offset = _versionOffset; }
        bool fresh = active is not null && active.WaitForFreshFrame(sinceVersion - offset, timeoutMs);
        if (fresh) _lastFreshTicks = Stopwatch.GetTimestamp();
        return fresh;
        }
    }

    public bool TryReadFrame(byte[] buffer)
    {
        lock (_readLock)
        {
        ICaptureSource? active;
        lock (_swapLock) active = _active;
        return active?.TryReadFrame(buffer) ?? false;
        }
    }

    private void WatchdogLoop()
    {
        bool failureLogged = false;
        while (!_stop.IsCancellationRequested)
        {
            Thread.Sleep(500);
            try { Evaluate(); failureLogged = false; }
            catch (Exception ex)
            {
                if (!failureLogged) Console.Error.WriteLine($"[capture] watchdog evaluation failed: {ex.Message}");
                failureLogged = true;
            }
        }
    }

    private void Evaluate()
    {
        long now = Stopwatch.GetTimestamp();
        double sinceSwapMs = (now - _lastSwapAttemptTicks) * 1000.0 / Stopwatch.Frequency;
        if (sinceSwapMs < SwapCooldownMs && _lastSwapAttemptTicks != 0) return;

        ICaptureSource? active;
        lock (_swapLock) active = _active;
        if (active is null) return;

        // Track freshness independently of the pacing loop (covers the startup gate too).
        long version = FrameVersion;
        if (version != _lastSeenVersion)
        {
            _lastSeenVersion = version;
            _lastFreshTicks = now;
        }
        double starvedMs = (now - _lastFreshTicks) * 1000.0 / Stopwatch.Frequency;

        if (active.CaptureError is not null)
        {
            // Active backend died outright: swap to the other one.
            Swap(toDuplication: !_usingDuplication, $"active capture failed ({active.CaptureError.Message})");
            return;
        }

        if (!_usingDuplication && starvedMs > StarvationMs && ForegroundProbe.FullscreenAppOnMonitor(_hMonitor))
        {
            // The exclusive-fullscreen signature: WGC starves while a fullscreen
            // app is focused here. A static desktop never triggers this.
            Swap(toDuplication: true, "fullscreen app detected while standard capture starved");
        }
    }

    internal void Swap(bool toDuplication, string reason)
    {
        ICaptureSource? expected;
        lock (_swapLock)
        {
            if (_disposed) return;
            expected = _active;
        }
        _lastSwapAttemptTicks = Stopwatch.GetTimestamp();
        Console.WriteLine($"[capture] switching backend: {reason}");
        ICaptureSource? fresh = null;
        ICaptureSource? retired = null;
        try
        {
            fresh = _createCapture(toDuplication);
            if (fresh.Width != Width || fresh.Height != Height)
                throw new InvalidOperationException("backend resolution mismatch");
            lock (_swapLock)
            {
                if (_disposed || !ReferenceEquals(_active, expected)) return;
                fresh.CursorEnabled = _cursorEnabled;
                _versionOffset += _active?.FrameVersion ?? 0;
                retired = _active;
                _active = fresh;
                fresh = null;
                _usingDuplication = toDuplication;
                _lastFreshTicks = Stopwatch.GetTimestamp();
            }
        }
        catch (Exception ex)
        {
            lock (_swapLock)
            {
                if (_disposed) return;
                if (_active?.CaptureError is Exception activeError)
                    _bothFailed = new InvalidOperationException("both capture backends failed", new AggregateException(activeError, ex));
            }
            Console.Error.WriteLine($"[capture] switch failed: {ex.Message}");
        }
        finally
        {
            if (fresh is not null) CaptureRetirement.Start(fresh, _readLock);
            if (retired is not null) CaptureRetirement.Start(retired, _readLock);
        }
    }

    public void Dispose()
    {
        ICaptureSource? active;
        lock (_swapLock)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active;
            _active = null;
        }
        _cts.Cancel();
        if (active is not null) CaptureRetirement.Start(active, _readLock);
        if (_watchdog is not null && !_watchdog.Join(1500))
            Console.Error.WriteLine("[capture] watchdog is still finishing a native call; late capture will be retired");
        _cts.Dispose();
    }
}
