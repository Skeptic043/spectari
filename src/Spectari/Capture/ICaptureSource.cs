namespace Spectari.Capture;

/// <summary>
/// A source of BGRA frames for the pipeline. Implementations cover Windows.Graphics.Capture,
/// DXGI desktop duplication, and Media Foundation video capture devices.
/// </summary>
public interface ICaptureSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    int? FrameRate => null;
    uint GpuVendorId { get; }
    string AdapterName { get; }
    string AdapterLuid { get; }
    string DriverVersion { get; }

    /// <summary>Monotonic frame counter - compare across calls to detect fresh content.</summary>
    long FrameVersion { get; }
    long FramesArrived { get; }

    /// <summary>First error from the capture path, if any; set means the source is dead.</summary>
    Exception? CaptureError { get; }

    /// <summary>Waits up to timeoutMs for a frame newer than sinceVersion. Single waiter.</summary>
    bool WaitForFreshFrame(long sinceVersion, int timeoutMs);

    /// <summary>Copies the most recent frame into buffer as tightly packed BGRA.</summary>
    bool TryReadFrame(byte[] buffer);

    bool CursorEnabled { set; }
}

/// <summary>Optional stage-level progress for capture backends whose native work
/// can block. Stopwatch ticks keep reads cheap and let the session describe the
/// last completed boundary without logging source names or frame contents.</summary>
internal interface ICaptureDiagnostics
{
    CaptureProgressSnapshot GetProgressSnapshot();
}

internal readonly record struct CaptureProgressSnapshot(
    long CallbacksStarted,
    long FramesReady,
    long ReadbacksStarted,
    long ReadbacksCompleted,
    long LastCallbackTicks,
    long LastFrameReadyTicks,
    long LastReadbackStartedTicks,
    long LastReadbackCompletedTicks,
    string CallbackStage,
    string ReadbackStage);
