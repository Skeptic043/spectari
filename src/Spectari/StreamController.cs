namespace Spectari;

internal enum StreamControllerState
{
    Idle,
    Starting,
    Live,
    Stopping,
    Switching,
    RetryingCpu,
    Failed,
}

internal sealed record StreamControllerStateChange(
    StreamControllerState Previous,
    StreamControllerState Current,
    string Trigger,
    SessionConfig? Config = null,
    string? Reason = null,
    bool WentLive = false,
    bool RenderCompletion = false);

internal sealed class StreamControllerStateTracker
{
    private readonly Action<string> _log;
    private StreamControllerState _state = StreamControllerState.Idle;
    private bool _cpuRecoveryUsed;

    internal StreamControllerState State => _state;
    internal bool CpuRecoveryUsed => _cpuRecoveryUsed;
    internal event Action<StreamControllerStateChange>? Changed;

    internal StreamControllerStateTracker(Action<string> log) => _log = log;

    internal bool CanTransitionTo(StreamControllerState next) => (_state, next) switch
    {
        (StreamControllerState.Idle, StreamControllerState.Starting or StreamControllerState.Failed) => true,
        (StreamControllerState.Starting, StreamControllerState.Live or StreamControllerState.Stopping
            or StreamControllerState.Switching or StreamControllerState.RetryingCpu
            or StreamControllerState.Idle or StreamControllerState.Failed) => true,
        (StreamControllerState.Live, StreamControllerState.Stopping or StreamControllerState.Switching
            or StreamControllerState.RetryingCpu or StreamControllerState.Idle
            or StreamControllerState.Failed) => true,
        (StreamControllerState.Stopping, StreamControllerState.Idle) => true,
        (StreamControllerState.Switching, StreamControllerState.Starting
            or StreamControllerState.Idle or StreamControllerState.Failed) => true,
        (StreamControllerState.RetryingCpu, StreamControllerState.Starting
            or StreamControllerState.Idle or StreamControllerState.Failed) => true,
        (StreamControllerState.Failed, StreamControllerState.Starting) => true,
        _ => false,
    };

    internal void ResetCpuRecovery() => _cpuRecoveryUsed = false;

    internal bool TryUseCpuRecovery()
    {
        if (_cpuRecoveryUsed) return false;
        _cpuRecoveryUsed = true;
        return true;
    }

    internal void Transition(
        StreamControllerState next,
        string trigger,
        SessionConfig? config = null,
        string? reason = null,
        bool wentLive = false,
        bool renderCompletion = false)
    {
        if (!CanTransitionTo(next))
            throw new InvalidOperationException($"Invalid stream state transition: {_state} to {next} ({trigger}).");

        StreamControllerState previous = _state;
        _state = next;
        _log($"[stream-controller] state {previous} -> {next}; trigger: {trigger}");
        Changed?.Invoke(new StreamControllerStateChange(
            previous, next, trigger, config, reason, wentLive, renderCompletion));
    }
}

internal sealed record CpuRecoveryPlan(
    SessionConfig FallbackConfig,
    bool DetectedStall,
    bool InvalidateAutoProbe,
    int OutputWidth,
    int OutputHeight)
{
    internal string GuiMessage => DetectedStall
        ? "Video pipeline stalled; restarting the session once with libx264…"
        : "GPU encoder exited; restarting with the CPU encoder (libx264)…";

    internal string ConsoleMessage => DetectedStall
        ? "[pipeline] Video pipeline stalled; restarting the session once with libx264…"
        : "[encoder] GPU encoder exited; restarting with the CPU encoder (libx264)…";

    internal string GuiCapacityWarning =>
        $"Warning: libx264 (CPU) may not keep up at {OutputWidth}x{OutputHeight}@{FallbackConfig.Fps}; lower the Preset if playback is choppy.";

    internal string ConsoleCapacityWarning =>
        $"[encoder] warning: libx264 (CPU) may not keep up at {OutputWidth}x{OutputHeight}@{FallbackConfig.Fps}; lower the Preset if playback is choppy.";
}

internal sealed record StreamControllerHooks(
    Func<Task<IDisposable?>> AcquireStartFenceAsync,
    Action StopIdleServer,
    Action<Action> Dispatch,
    Func<string, IDisposable?> TrackOperation,
    Action<string> Log);

internal sealed class StreamController : IDisposable
{
    internal const string UserStartTrigger = "user start";
    internal const string UserStopTrigger = "user stop";
    internal const string SourceSwitchTrigger = "source switch";
    internal const string AccessRestartTrigger = "port access restart";

    private readonly StreamControllerHooks _hooks;
    private readonly StreamControllerStateTracker _state;
    private System.Windows.Forms.Timer? _cpuRetryTimer;
    private SessionConfig? _pendingSwitch;
    private string? _pendingSwitchTrigger;
    private int _lifecycleGeneration;
    private bool _closing;

    internal StreamControllerState State => _state.State;
    internal StreamSession? CurrentSession { get; private set; }
    internal SessionConfig? LastConfig { get; private set; }
    internal bool HasSession => CurrentSession is not null;
    internal bool IsCpuRetryPending => State == StreamControllerState.RetryingCpu;
    internal bool IsStopping => State is StreamControllerState.Stopping or StreamControllerState.Switching;
    internal bool IsIdleSurface => CurrentSession is null
        && State is StreamControllerState.Idle or StreamControllerState.Failed;

    internal event Action<StreamControllerStateChange>? StateChanged;
    internal event Action<SessionConfig>? SessionStarted;

    internal StreamController(StreamControllerHooks hooks)
    {
        _hooks = hooks;
        _state = new StreamControllerStateTracker(hooks.Log);
        _state.Changed += change => StateChanged?.Invoke(change);
    }

    internal async Task<bool> StartAsync(SessionConfig config, string trigger = UserStartTrigger)
    {
        if (_closing || CurrentSession is not null
            || State is not (StreamControllerState.Idle or StreamControllerState.Failed))
            return false;

        _state.ResetCpuRecovery();
        return await LaunchSessionAsync(config, trigger);
    }

    internal void MarkLive()
    {
        if (State == StreamControllerState.Starting)
            _state.Transition(StreamControllerState.Live, "first captured frame", LastConfig);
    }

    internal void Stop()
    {
        if (State == StreamControllerState.RetryingCpu)
        {
            CancelCpuRetryTimer();
            _state.Transition(
                StreamControllerState.Idle,
                UserStopTrigger,
                reason: null,
                wentLive: true,
                renderCompletion: true);
            return;
        }

        if (CurrentSession is null || IsStopping) return;

        using IDisposable? operation = _hooks.TrackOperation("stream stop UI phase");
        _pendingSwitch = null;
        _pendingSwitchTrigger = null;
        CancelCpuRetryTimer();
        _state.Transition(StreamControllerState.Stopping, UserStopTrigger);
        CurrentSession.RequestStop();
    }

    internal bool Switch(SessionConfig config, string trigger, Action? beforeStop = null)
    {
        if (CurrentSession is null || IsStopping) return false;

        _state.ResetCpuRecovery();
        _pendingSwitch = config;
        _pendingSwitchTrigger = trigger;
        _state.Transition(StreamControllerState.Switching, trigger, config);
        beforeStop?.Invoke();
        CurrentSession.RequestStop();
        return true;
    }

    internal bool StopForClose()
    {
        _closing = true;
        CancelCpuRetryTimer();
        _pendingSwitch = null;
        _pendingSwitchTrigger = null;
        StreamSession? session = CurrentSession;
        CurrentSession = null;
        return session is null || session.Stop();
    }

    internal void StopForShutdown() => CurrentSession?.Stop();

    internal static CpuRecoveryPlan? PlanCpuRecovery(
        string? reason,
        SessionConfig config,
        int outputWidth,
        int outputHeight,
        bool recoveryAlreadyUsed,
        bool userRequested)
    {
        bool detectedStall = StreamSession.IsPipelineStallReason(reason);
        bool encoderExited = reason?.StartsWith("encoder exited", StringComparison.Ordinal) ?? false;
        if (userRequested || recoveryAlreadyUsed || config.Encoder == "libx264"
            || (!detectedStall && !encoderExited))
            return null;

        bool invalidateAutoProbe = encoderExited
            && (string.IsNullOrEmpty(config.Encoder) || config.Encoder == "auto");
        return new CpuRecoveryPlan(
            config with { Encoder = "libx264" },
            detectedStall,
            invalidateAutoProbe,
            outputWidth,
            outputHeight);
    }

    private async Task<bool> LaunchSessionAsync(SessionConfig config, string trigger)
    {
        using IDisposable? operation = _hooks.TrackOperation("stream start UI phase");
        _state.Transition(StreamControllerState.Starting, trigger, config);

        IDisposable? previewFence;
        try
        {
            previewFence = await _hooks.AcquireStartFenceAsync();
        }
        catch (Exception ex)
        {
            _hooks.Log($"[stream-controller] start preparation failed: {ex.Message}");
            TransitionToFailed("start preparation failed");
            return false;
        }

        if (previewFence is null) return false;
        if (_closing)
        {
            previewFence.Dispose();
            return false;
        }

        _hooks.StopIdleServer();
        StreamSession session;
        try
        {
            session = new StreamSession(config);
        }
        catch (Exception ex)
        {
            _hooks.Log($"Failed to start: {ex.Message}");
            previewFence.Dispose();
            TransitionToFailed("session creation failed");
            return false;
        }

        session.Stopped += reason => DispatchStopped(session, config, reason);
        CurrentSession = session;
        LastConfig = config;
        previewFence.Dispose();
        session.Start();
        SessionStarted?.Invoke(config);
        return true;
    }

    private void DispatchStopped(StreamSession session, SessionConfig config, string reason)
    {
        try
        {
            _hooks.Dispatch(async () => await HandleStoppedAsync(session, config, reason));
        }
        catch (Exception ex) when (_closing && ex is InvalidOperationException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _hooks.Log($"[stream-controller] dispatch stop completion failed: {ex.Message}");
        }
    }

    private async Task HandleStoppedAsync(StreamSession session, SessionConfig config, string reason)
    {
        using IDisposable? operation = _hooks.TrackOperation("stream stop completion");
        if (!ReferenceEquals(CurrentSession, session)) return;

        CurrentSession = null;
        _lifecycleGeneration++;
        bool userRequested = State == StreamControllerState.Stopping;

        if (State == StreamControllerState.Switching && _pendingSwitch is { } next)
        {
            string nextTrigger = _pendingSwitchTrigger ?? SourceSwitchTrigger;
            _pendingSwitch = null;
            _pendingSwitchTrigger = null;
            await LaunchSessionAsync(next, nextTrigger);
            return;
        }

        CpuRecoveryPlan? recovery = PlanCpuRecovery(
            reason,
            config,
            session.OutputWidth,
            session.OutputHeight,
            _state.CpuRecoveryUsed,
            userRequested);
        if (recovery is not null && _state.TryUseCpuRecovery())
        {
            _hooks.Log(recovery.GuiMessage);
            if (recovery.InvalidateAutoProbe)
                Encode.FfmpegEncoder.InvalidateProbeCache();
            if (recovery.OutputHeight >= 1440)
                _hooks.Log(recovery.GuiCapacityWarning);

            _state.Transition(
                StreamControllerState.RetryingCpu,
                recovery.DetectedStall ? "pipeline stall" : "GPU encoder exit",
                recovery.FallbackConfig);
            ArmCpuRetry(recovery.FallbackConfig);
            return;
        }

        string? displayReason = userRequested ? null : reason;
        StreamControllerState finalState = displayReason is null or "stopped"
            ? StreamControllerState.Idle
            : StreamControllerState.Failed;
        _state.Transition(
            finalState,
            userRequested ? UserStopTrigger : $"session stopped: {reason}",
            reason: displayReason,
            wentLive: session.WentLive,
            renderCompletion: true);
    }

    private void ArmCpuRetry(SessionConfig fallback)
    {
        int generation = _lifecycleGeneration;
        _cpuRetryTimer?.Dispose();
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        _cpuRetryTimer = timer;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (ReferenceEquals(_cpuRetryTimer, timer)) _cpuRetryTimer = null;
            if (_closing || generation != _lifecycleGeneration
                || State != StreamControllerState.RetryingCpu)
                return;

            await LaunchSessionAsync(fallback, "CPU recovery");
        };
        timer.Start();
    }

    private void CancelCpuRetryTimer()
    {
        _cpuRetryTimer?.Stop();
        _cpuRetryTimer?.Dispose();
        _cpuRetryTimer = null;
        _lifecycleGeneration++;
    }

    private void TransitionToFailed(string trigger)
    {
        if (State != StreamControllerState.Failed)
            _state.Transition(
                StreamControllerState.Failed,
                trigger,
                reason: null,
                wentLive: false,
                renderCompletion: true);
    }

    public void Dispose()
    {
        _closing = true;
        CancelCpuRetryTimer();
    }
}
