namespace Spectari.Capture;

internal sealed class WindowCandidateRecovery(Func<long>? milliseconds = null)
{
    private readonly Func<long> _now = milliseconds ?? (() => Environment.TickCount64);
    private readonly object _gate = new();
    private readonly Dictionary<WindowIdentity, (int Failures, long RetryAt)> _failures = [];

    internal void Clear() { lock (_gate) _failures.Clear(); }
    internal bool CanTry(WindowIdentity identity)
    {
        lock (_gate) return !_failures.TryGetValue(identity, out var failure) || _now() >= failure.RetryAt;
    }

    /// <summary>Returns a live candidate, or null after recording a cooldown.
    /// <see cref="UnchangingWindowPolicy.Candidate"/> decides readiness.</summary>
    internal T? Prepare<T>(WindowIdentity identity, Func<T> create, Func<T, bool> closed,
        bool cursor, CancellationToken ct) where T : class, ICaptureSource
    {
        if (ct.IsCancellationRequested || !CanTry(identity)) return null;
        T? capture = null;
        try
        {
            capture = create();
            capture.CursorEnabled = cursor;
            long started = _now();
            CandidateStartup startup;
            while ((startup = UnchangingWindowPolicy.Candidate(capture.FrameVersion, closed(capture),
                       capture.CaptureError is not null, _now() - started)) == CandidateStartup.Pending
                   && !ct.IsCancellationRequested)
                capture.WaitForFreshFrame(0, 100);
            if (ct.IsCancellationRequested) return null;
            if (startup == CandidateStartup.Failed)
                throw new InvalidOperationException("candidate capture closed or failed before it started");
            lock (_gate) _failures.Remove(identity);
            T ready = capture;
            capture = null;
            return ready;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                bool first;
                lock (_gate)
                {
                    _failures.TryGetValue(identity, out var prior);
                    first = prior.Failures == 0;
                    int count = Math.Min(prior.Failures + 1, 5);
                    _failures[identity] = (count, _now() + Math.Min(30000, 2000 << (count - 1)));
                }
                if (first) Console.Error.WriteLine($"[window-reattach] candidate failed; retrying after cooldown: {ex.Message}");
            }
            return null;
        }
        finally { if (capture is not null) CaptureRetirement.Start(capture); }
    }
}
