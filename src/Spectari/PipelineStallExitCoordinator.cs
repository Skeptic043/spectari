namespace Spectari;

/// <summary>Starts the deadline and breaks blocked ffmpeg work before a
/// stalled pipeline can enter orderly cleanup.</summary>
internal sealed class PipelineStallExitCoordinator
{
    private readonly Action<string> _recordStall;
    private readonly Action<string, Func<string>> _armDeadline;
    private readonly Action _abortFfmpeg;
    private readonly Action _cancelSession;
    private int _started;

    internal PipelineStallExitCoordinator(
        Action<string> recordStall,
        Action<string, Func<string>> armDeadline,
        Action abortFfmpeg,
        Action cancelSession)
    {
        _recordStall = recordStall;
        _armDeadline = armDeadline;
        _abortFfmpeg = abortFfmpeg;
        _cancelSession = cancelSession;
    }

    internal bool Begin(string stopReason, Func<string> activeStages)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return false;

        _recordStall(stopReason);
        _armDeadline(stopReason, activeStages);
        try { _abortFfmpeg(); }
        finally { _cancelSession(); }
        return true;
    }
}
