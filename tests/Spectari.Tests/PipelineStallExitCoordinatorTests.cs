using Xunit;

namespace Spectari.Tests;

public sealed class PipelineStallExitCoordinatorTests
{
    [Fact]
    public void FirstFailureArmsAndAbortsBeforeCancellation()
    {
        var calls = new List<string>();
        var coordinator = new PipelineStallExitCoordinator(
            reason => calls.Add($"record:{reason}"),
            (reason, stages) => calls.Add($"arm:{reason}:{stages()}"),
            () => calls.Add("abort"),
            () => calls.Add("cancel"));

        bool first = coordinator.Begin("stalled", () => "pacing=failed");
        bool second = coordinator.Begin("later", () => "ignored");

        Assert.True(first);
        Assert.False(second);
        Assert.Equal([
            "record:stalled",
            "arm:stalled:pacing=failed",
            "abort",
            "cancel",
        ], calls);
    }

    [Fact]
    public void CancellationStillRunsIfAbortThrows()
    {
        bool cancelled = false;
        var coordinator = new PipelineStallExitCoordinator(
            _ => { },
            (_, _) => { },
            () => throw new InvalidOperationException("abort failed"),
            () => cancelled = true);

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Begin("stalled", () => "pacing=failed"));
        Assert.True(cancelled);
    }
}
