using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StreamHost.Capture;

internal enum IdlePreviewPollState
{
    NoChange,
    Frame,
    Minimized,
    Unavailable,
}

internal enum IdlePreviewStartState
{
    Ready,
    Canceled,
    TimedOut,
    Failed,
}

internal readonly record struct IdlePreviewPollResult(IdlePreviewPollState State, Bitmap? Image = null);

/// <summary>
/// Owns preview-only WGC lifecycle work so capture creation and teardown never
/// run on the UI thread.
/// </summary>
internal sealed class IdlePreviewCapture : IDisposable
{
    private static readonly TimeSpan CreationTimeout = TimeSpan.FromSeconds(5);

    private readonly object _stateGate = new();
    private Task _lifecycleTail = Task.CompletedTask;
    private Task<bool> _attachedCleanup = Task.FromResult(true);
    private CancellationTokenSource? _activeCreation;
    private PreviewResources? _resources;
    private int _generation;
    private bool _streamStartFenced;
    private bool _disposed;

    public bool IsReady
    {
        get
        {
            lock (_stateGate) return _resources is not null;
        }
    }

    public Task<IdlePreviewStartState> StartForMonitorAsync(IntPtr monitorHandle) =>
        StartAsync(
            "monitor",
            IntPtr.Zero,
            trace => ScreenCapture.ForPreviewMonitor(monitorHandle, trace));

    public Task<IdlePreviewStartState> StartForWindowAsync(IntPtr windowHandle) =>
        StartAsync(
            "window",
            windowHandle,
            trace => ScreenCapture.ForPreviewWindow(windowHandle, trace));

    private Task<IdlePreviewStartState> StartAsync(
        string targetKind,
        IntPtr windowHandle,
        Func<CaptureCreationTrace, ScreenCapture> createCapture)
    {
        CancellationTokenSource creationCts;
        Task canceled;
        Task<CreationOutcome> creation;
        CaptureCreationTrace trace;
        int generation;

        lock (_stateGate)
        {
            if (_disposed || _streamStartFenced)
                return Task.FromResult(IdlePreviewStartState.Canceled);

            _activeCreation?.Cancel();
            creationCts = new CancellationTokenSource();
            canceled = Task.Delay(Timeout.InfiniteTimeSpan, creationCts.Token);
            _activeCreation = creationCts;
            generation = ++_generation;

            PreviewResources? previousResources = _resources;
            _resources = null;
            Task previousLifecycle = _lifecycleTail;
            trace = new CaptureCreationTrace(targetKind);
            creation = Task.Run(
                () => CreateAfterPreviousAsync(
                    previousLifecycle,
                    previousResources,
                    generation,
                    creationCts,
                    windowHandle,
                    createCapture,
                    trace));
            _lifecycleTail = creation;
        }

        return AwaitCreationAsync(
            targetKind,
            generation,
            creationCts,
            creation,
            canceled,
            trace);
    }

    private async Task<CreationOutcome> CreateAfterPreviousAsync(
        Task previousLifecycle,
        PreviewResources? previousResources,
        int generation,
        CancellationTokenSource creationCts,
        IntPtr windowHandle,
        Func<CaptureCreationTrace, ScreenCapture> createCapture,
        CaptureCreationTrace trace)
    {
        try
        {
            await previousLifecycle.ConfigureAwait(false);
            previousResources?.Dispose();

            if (!CanCreate(generation, creationCts))
                return CreationOutcome.Canceled;

            ScreenCapture capture = createCapture(trace);
            PreviewResources created;
            try
            {
                created = new PreviewResources(capture, windowHandle);
            }
            catch
            {
                capture.Dispose();
                throw;
            }

            if (!TryAttach(generation, creationCts, created))
            {
                created.Dispose();
                return CreationOutcome.Canceled;
            }

            return CreationOutcome.Ready;
        }
        catch (Exception ex)
        {
            return IsGenerationCurrent(generation)
                ? new CreationOutcome(IdlePreviewStartState.Failed, ex)
                : CreationOutcome.Canceled;
        }
        finally
        {
            CompleteCreation(creationCts);
        }
    }

    private async Task<IdlePreviewStartState> AwaitCreationAsync(
        string targetKind,
        int generation,
        CancellationTokenSource creationCts,
        Task<CreationOutcome> creation,
        Task canceled,
        CaptureCreationTrace trace)
    {
        Task timeout = Task.Delay(CreationTimeout);
        Task completed = await Task.WhenAny(creation, canceled, timeout);

        if (completed == creation)
            return ReportOutcome(targetKind, generation, await creation, trace);

        if (completed == canceled)
            return IdlePreviewStartState.Canceled;

        if (!CancelTimedOutGeneration(generation, creationCts))
        {
            if (creation.IsCompleted)
                return ReportOutcome(targetKind, generation, await creation, trace);
            return IdlePreviewStartState.Canceled;
        }

        Console.WriteLine(
            $"[preview] {targetKind} creation timed out after 5 seconds; preview disabled; " +
            $"pending step: {trace.CurrentStep}; last completed step: {trace.LastCompletedStep}");
        return IdlePreviewStartState.TimedOut;
    }

    private IdlePreviewStartState ReportOutcome(
        string targetKind,
        int generation,
        CreationOutcome outcome,
        CaptureCreationTrace trace)
    {
        if (!IsGenerationCurrent(generation))
            return IdlePreviewStartState.Canceled;

        if (outcome.State == IdlePreviewStartState.Failed && outcome.Error is not null)
        {
            Console.WriteLine(
                $"[preview] {targetKind} creation failed; preview disabled; " +
                $"pending step: {trace.CurrentStep}; last completed step: {trace.LastCompletedStep}; " +
                $"error: {SingleLine(outcome.Error.Message)}");
        }

        return outcome.State;
    }

    public IdlePreviewPollResult Poll()
    {
        lock (_stateGate)
            return _resources?.Poll() ?? default;
    }

    public void Stop()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            CancelCurrentGeneration();
            QueueAttachedCleanup("background teardown");
        }
    }

    public async Task<IdlePreviewStreamFence?> AcquireStreamStartFenceAsync()
    {
        Task<bool> cleanup;
        lock (_stateGate)
        {
            if (_disposed || _streamStartFenced)
                return null;

            _streamStartFenced = true;
            CancelCurrentGeneration();
            cleanup = QueueAttachedCleanup("stream-start teardown");
        }

        if (!await cleanup.ConfigureAwait(true))
        {
            ReleaseStreamStartFence();
            return null;
        }

        lock (_stateGate)
        {
            if (_disposed)
            {
                _streamStartFenced = false;
                return null;
            }
        }

        return new IdlePreviewStreamFence(this);
    }

    private bool CanCreate(int generation, CancellationTokenSource creationCts)
    {
        lock (_stateGate)
        {
            return !_disposed &&
                   !_streamStartFenced &&
                   generation == _generation &&
                   ReferenceEquals(_activeCreation, creationCts) &&
                   !creationCts.IsCancellationRequested;
        }
    }

    private bool IsGenerationCurrent(int generation)
    {
        lock (_stateGate)
            return !_disposed && !_streamStartFenced && generation == _generation;
    }

    private bool TryAttach(
        int generation,
        CancellationTokenSource creationCts,
        PreviewResources resources)
    {
        lock (_stateGate)
        {
            if (_disposed ||
                _streamStartFenced ||
                generation != _generation ||
                !ReferenceEquals(_activeCreation, creationCts) ||
                creationCts.IsCancellationRequested)
            {
                return false;
            }

            _resources = resources;
            return true;
        }
    }

    private bool CancelTimedOutGeneration(int generation, CancellationTokenSource creationCts)
    {
        lock (_stateGate)
        {
            if (_disposed ||
                _streamStartFenced ||
                generation != _generation ||
                !ReferenceEquals(_activeCreation, creationCts))
            {
                return false;
            }

            creationCts.Cancel();
            _generation++;
            QueueAttachedCleanup("timeout teardown");
            return true;
        }
    }

    private void CancelCurrentGeneration()
    {
        _activeCreation?.Cancel();
        _generation++;
    }

    private void CompleteCreation(CancellationTokenSource creationCts)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_activeCreation, creationCts))
                _activeCreation = null;
        }
        creationCts.Dispose();
    }

    private Task<bool> QueueAttachedCleanup(string action)
    {
        PreviewResources? resources = _resources;
        _resources = null;
        if (resources is null)
            return _attachedCleanup;

        Task<bool> cleanup = RunCleanupAfterAsync(_lifecycleTail, resources, action);
        _lifecycleTail = cleanup;
        _attachedCleanup = cleanup;
        return cleanup;
    }

    private static Task<bool> RunCleanupAfterAsync(
        Task previousLifecycle,
        PreviewResources resources,
        string action) =>
        Task.Run(async () =>
        {
            try
            {
                await previousLifecycle.ConfigureAwait(false);
                resources.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[preview] {action} failed: {SingleLine(ex.Message)}");
                return false;
            }
        });

    private void ReleaseStreamStartFence()
    {
        lock (_stateGate) _streamStartFenced = false;
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelCurrentGeneration();
            QueueAttachedCleanup("shutdown teardown");
        }
    }

    private readonly record struct CreationOutcome(IdlePreviewStartState State, Exception? Error = null)
    {
        public static CreationOutcome Ready => new(IdlePreviewStartState.Ready);
        public static CreationOutcome Canceled => new(IdlePreviewStartState.Canceled);
    }

    internal sealed class IdlePreviewStreamFence : IDisposable
    {
        private IdlePreviewCapture? _owner;

        public IdlePreviewStreamFence(IdlePreviewCapture owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseStreamStartFence();
        }
    }

    private sealed class PreviewResources : IDisposable
    {
        private const int MaxPreviewWidth = 640;
        private const int MaxPreviewHeight = 360;

        private readonly ScreenCapture _capture;
        private readonly IntPtr _windowHandle;
        private readonly byte[] _buffer;
        private readonly GCHandle _bufferPin;
        private readonly Bitmap _sourceBitmap;
        private long _lastFrameVersion = -1;
        private IdlePreviewPollState _lastState = IdlePreviewPollState.NoChange;
        private int _disposed;

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        public PreviewResources(ScreenCapture capture, IntPtr windowHandle)
        {
            _capture = capture;
            _windowHandle = windowHandle;

            int byteCount = checked(capture.Width * capture.Height * 4);
            _buffer = GC.AllocateUninitializedArray<byte>(byteCount);
            _bufferPin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                _sourceBitmap = new Bitmap(
                    capture.Width,
                    capture.Height,
                    capture.Width * 4,
                    PixelFormat.Format32bppArgb,
                    _bufferPin.AddrOfPinnedObject());
            }
            catch
            {
                _bufferPin.Free();
                throw;
            }
        }

        public IdlePreviewPollResult Poll()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ChangedState(IdlePreviewPollState.Unavailable);

            if (_windowHandle != IntPtr.Zero)
            {
                if (!IsWindow(_windowHandle))
                    return ChangedState(IdlePreviewPollState.Unavailable);
                if (IsIconic(_windowHandle))
                    return ChangedState(IdlePreviewPollState.Minimized);
            }

            if (_capture.CaptureError is { } callbackError)
                return CaptureUnavailable("frame callback", callbackError);

            long version = _capture.FrameVersion;
            if (version <= _lastFrameVersion)
                return default;

            if (!_capture.TryReadFrame(_buffer))
            {
                return _capture.CaptureError is { } readbackError
                    ? CaptureUnavailable("frame readback", readbackError)
                    : default;
            }

            _lastFrameVersion = version;
            _lastState = IdlePreviewPollState.Frame;
            return new IdlePreviewPollResult(IdlePreviewPollState.Frame, CreateScaledBitmap());
        }

        private IdlePreviewPollResult ChangedState(IdlePreviewPollState state)
        {
            if (_lastState == state) return default;
            _lastState = state;
            return new IdlePreviewPollResult(state);
        }

        private IdlePreviewPollResult CaptureUnavailable(string action, Exception error)
        {
            if (_lastState != IdlePreviewPollState.Unavailable)
                Console.WriteLine($"[preview] {action} failed: {SingleLine(error.Message)}");
            return ChangedState(IdlePreviewPollState.Unavailable);
        }

        private Bitmap CreateScaledBitmap()
        {
            double scale = Math.Min(
                1.0,
                Math.Min((double)MaxPreviewWidth / _capture.Width, (double)MaxPreviewHeight / _capture.Height));
            int width = Math.Max(1, (int)Math.Round(_capture.Width * scale));
            int height = Math.Max(1, (int)Math.Round(_capture.Height * scale));
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using Graphics graphics = Graphics.FromImage(result);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    _sourceBitmap,
                    new Rectangle(0, 0, width, height),
                    0,
                    0,
                    _capture.Width,
                    _capture.Height,
                    GraphicsUnit.Pixel);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _capture.Dispose();
            }
            finally
            {
                _sourceBitmap.Dispose();
                _bufferPin.Free();
            }
        }
    }
}
