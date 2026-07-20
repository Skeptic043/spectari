using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class LogEventClassifierTests
{
    [Theory]
    [InlineData("[ready] first frame captured; streaming 1080p60", true)]
    [InlineData("[ws] viewer connected (1 total)", true)]
    [InlineData("[ws] viewer disconnected (0 total)", true)]
    [InlineData("[ws] viewer rejected; at capacity", true)]
    [InlineData("[ws] viewer 123 fell behind; resyncing", false)]
    [InlineData("[audio] desktop loopback failed; feeding silence instead", true)]
    [InlineData("[audio] aligned to video timeline (+14 ms lead-in silence)", false)]
    [InlineData("[capture] no frames received within 5 seconds", true)]
    [InlineData("[capture] adapter: Example GPU, driver 1.2.3", false)]
    [InlineData("[encoder] h264_nvenc failed its self-test; falling back to CPU", true)]
    [InlineData("[encoder] ffmpeg -hide_banner -loglevel warning", false)]
    [InlineData("[pipeline] ffmpeg output stopped; the live video pipeline is stalled", true)]
    [InlineData("[pipeline] progress over 5.0s: frame-enqueue +300", false)]
    [InlineData("[pipeline] active stages: capture-callback=idle", false)]
    [InlineData("[stream-controller] state Starting -> Live; trigger: first frame", false)]
    [InlineData("[stream-controller] start preparation failed: timed out", true)]
    [InlineData("Video pipeline stalled; restarting once with libx264", true)]
    [InlineData("Asking for administrator approval to configure port 8093", true)]
    [InlineData("[boot] settings load start", false)]
    [InlineData("System.InvalidOperationException: example", false)]
    public void ClassifySeparatesOperatorEventsFromDiagnosticDetail(
        string line,
        bool expectedInOperatorView)
    {
        Assert.Equal(
            expectedInOperatorView,
            LogEventClassifier.Classify(line) == LogAudience.OperatorAndDiagnostic);
    }
}
