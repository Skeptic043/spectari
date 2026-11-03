namespace Spectari.Capture;

internal enum CandidateStartup { Pending, Ready, Failed }

/// <summary>
/// Decides when a window capture that has delivered no frames is treated as an
/// unchanging window rather than a failure. A picked window that does not change
/// is valid content; only a closed or failed capture is a failure.
/// </summary>
internal static class UnchangingWindowPolicy
{
    internal const int FirstFrameGraceMs = 1500;

    /// <summary>A reattach candidate is ready once it delivers a frame or survives the
    /// grace period without closing or failing.</summary>
    internal static CandidateStartup Candidate(long frames, bool closed, bool failed, long elapsedMs) =>
        closed || failed ? CandidateStartup.Failed
        : frames > 0 || elapsedMs >= FirstFrameGraceMs ? CandidateStartup.Ready
        : CandidateStartup.Pending;

    /// <summary>True when the attached capture should stand in a still image, or the
    /// waiting frame until a still is possible, for its missing frames.</summary>
    internal static bool NeedsStandIn(long frames, bool hasStill, bool closed, bool failed, long elapsedMs) =>
        frames == 0 && !hasStill && !closed && !failed && elapsedMs >= FirstFrameGraceMs;
}
