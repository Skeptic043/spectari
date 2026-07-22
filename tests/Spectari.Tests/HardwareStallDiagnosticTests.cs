using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class HardwareStallDiagnosticTests
{
    [Fact]
    public void DeliveryFormatIncludesPendingDepthAndInputCredits()
    {
        var encoder = new HardwareEncoderProgress(7, 0, 1, 0, 0);

        string diagnostic = HardwareStallDiagnostic.FormatDelivery(
            59.94,
            600,
            2,
            encoder);

        Assert.Equal(
            "[gpu-encode] encode delivery: 59.9 fps, 600 access units, debt 2 frames, pending-depth=7, input-credits=1.",
            diagnostic);
    }

    [Fact]
    public void FormatNamesEveryResourceLocationAtTheStallInstant()
    {
        var pool = new FrameLeaseAccounting(
            Capacity: 8,
            Available: 0,
            Outstanding: 8,
            TotalRents: 1200,
            Returns: new Dictionary<FrameLeaseReturnReason, long>());
        var encoder = new HardwareEncoderProgress(
            PendingQueueDepth: 1,
            SubmittedQueueDepth: 7,
            InputCredits: 0,
            LastNeedInputEventTicks: 250,
            LastHaveOutputEventTicks: 500);
        var writer = new VideoInputWriterProgress(
            FramesEnqueued: 1200,
            WritesStarted: 1192,
            WritesCompleted: 1191,
            LastEnqueueTicks: 700,
            LastWriteStartedTicks: 400,
            LastWriteCompletedTicks: 300,
            QueueDepth: 8,
            WriteInProgress: true,
            Failed: false);

        string diagnostic = HardwareStallDiagnostic.Format(
            pool,
            encoder,
            writer,
            nowTicks: 1000,
            timestampFrequency: 1000);

        Assert.Equal(string.Join(Environment.NewLine,
            "[gpu-encode] sustained-debt resources:",
            "  nv12-pool outstanding=8/8",
            "  mf-encoder pending-depth=1 submitted-depth=7 input-credits=0 last-need-input=750ms ago last-have-output=500ms ago",
            "  video-input queue-depth=8 write-in-progress=true last-write=600ms ago"), diagnostic);
    }

    [Fact]
    public void FormatReportsNeverAndUsesLastCompletedWriteWhenIdle()
    {
        var pool = new FrameLeaseAccounting(
            8,
            8,
            0,
            0,
            new Dictionary<FrameLeaseReturnReason, long>());
        var encoder = new HardwareEncoderProgress(0, 0, 2, 0, 0);
        var writer = new VideoInputWriterProgress(
            1,
            1,
            1,
            100,
            200,
            250,
            0,
            false,
            false);

        string diagnostic = HardwareStallDiagnostic.Format(
            pool,
            encoder,
            writer,
            nowTicks: 2250,
            timestampFrequency: 1000);

        Assert.Contains("last-need-input=never last-have-output=never", diagnostic);
        Assert.Contains("write-in-progress=false last-write=2.0s ago", diagnostic);
    }
}
