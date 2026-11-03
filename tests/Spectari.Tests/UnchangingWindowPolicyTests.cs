using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class UnchangingWindowPolicyTests
{
    private const long Grace = UnchangingWindowPolicy.FirstFrameGraceMs;

    [Fact]
    public void LiveWindowWithoutFramesGetsStandInOnlyAfterGrace()
    {
        Assert.False(UnchangingWindowPolicy.NeedsStandIn(0, false, false, false, Grace - 1));
        Assert.True(UnchangingWindowPolicy.NeedsStandIn(0, false, false, false, Grace));
    }

    [Fact]
    public void FramesStillsAndRealFailuresNeverGetStandIn()
    {
        Assert.False(UnchangingWindowPolicy.NeedsStandIn(1, false, false, false, Grace * 10));
        Assert.False(UnchangingWindowPolicy.NeedsStandIn(0, true, false, false, Grace * 10));
        Assert.False(UnchangingWindowPolicy.NeedsStandIn(0, false, true, false, Grace * 10));
        Assert.False(UnchangingWindowPolicy.NeedsStandIn(0, false, false, true, Grace * 10));
    }
}
