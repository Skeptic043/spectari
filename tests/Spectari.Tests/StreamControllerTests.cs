using Xunit;

namespace Spectari.Tests;

public sealed class StreamControllerTests
{
    [Fact]
    public void ValidTransitionsLogOnceWithTheirTriggers()
    {
        var logs = new List<string>();
        var tracker = new StreamControllerStateTracker(logs.Add);

        tracker.Transition(StreamControllerState.Starting, "start click");
        tracker.Transition(StreamControllerState.Live, "first frame");
        tracker.Transition(StreamControllerState.Stopping, "stop click");
        tracker.Transition(StreamControllerState.Idle, "teardown complete");

        Assert.Equal(StreamControllerState.Idle, tracker.State);
        Assert.Equal(4, logs.Count);
        Assert.Contains("trigger: start click", logs[0]);
        Assert.Contains("trigger: first frame", logs[1]);
        Assert.Contains("trigger: stop click", logs[2]);
        Assert.Contains("trigger: teardown complete", logs[3]);
    }

    [Fact]
    public void InvalidTransitionDoesNotChangeStateOrLog()
    {
        var logs = new List<string>();
        var tracker = new StreamControllerStateTracker(logs.Add);

        Assert.Throws<InvalidOperationException>(() =>
            tracker.Transition(StreamControllerState.Live, "invalid direct live"));

        Assert.Equal(StreamControllerState.Idle, tracker.State);
        Assert.Empty(logs);
    }

    [Fact]
    public void CpuRecoveryCanOnlyBeUsedOncePerCycle()
    {
        var tracker = new StreamControllerStateTracker(_ => { });

        tracker.ResetCpuRecovery();

        Assert.True(tracker.TryUseCpuRecovery());
        Assert.False(tracker.TryUseCpuRecovery());

        tracker.ResetCpuRecovery();

        Assert.True(tracker.TryUseCpuRecovery());
    }

    [Fact]
    public void CpuRecoveryPolicyRejectsASecondRecovery()
    {
        var config = new SessionConfig { Encoder = "auto", Fps = 60 };

        CpuRecoveryPlan? first = StreamController.PlanCpuRecovery(
            "encoder exited with code 1", config, 2560, 1440,
            recoveryAlreadyUsed: false, userRequested: false);
        CpuRecoveryPlan? second = StreamController.PlanCpuRecovery(
            "encoder exited with code 1", config, 2560, 1440,
            recoveryAlreadyUsed: true, userRequested: false);

        Assert.NotNull(first);
        Assert.Equal("libx264", first.FallbackConfig.Encoder);
        Assert.True(first.InvalidateAutoProbe);
        Assert.Null(second);
    }

    [Fact]
    public void SwitchWhileLiveReturnsToStartingAndLive()
    {
        var tracker = new StreamControllerStateTracker(_ => { });

        tracker.Transition(StreamControllerState.Starting, "user start");
        tracker.Transition(StreamControllerState.Live, "first captured frame");
        tracker.Transition(StreamControllerState.Switching, "source switch");
        tracker.Transition(StreamControllerState.Starting, "source switch");
        tracker.Transition(StreamControllerState.Live, "first captured frame");

        Assert.Equal(StreamControllerState.Live, tracker.State);
    }
}
