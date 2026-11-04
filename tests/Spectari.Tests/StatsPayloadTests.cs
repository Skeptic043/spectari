using Spectari.Server;
using Xunit;

namespace Spectari.Tests;

public sealed class StatsPayloadTests
{
    [Fact]
    public void LiveStatsReportTheSentFrameRateAndTheHostClock()
    {
        var broadcaster = new Broadcaster { StreamName = "Den" };
        broadcaster.SentFps = 30;
        broadcaster.SourceFps = 48;

        IReadOnlyDictionary<string, object?> stats = WebServer.BuildStats(broadcaster, "idle", nowMs: 1_700_000_000_123);

        Assert.Equal(30, stats["sentFps"]);
        Assert.Equal(48, stats["sourceFps"]);
        Assert.Equal(1_700_000_000_123L, stats["nowMs"]);
        Assert.False(stats.ContainsKey("key"));
    }

    [Fact]
    public void IdleStatsStillReportTheHostClock()
    {
        IReadOnlyDictionary<string, object?> stats = WebServer.BuildStats(null, "Den", nowMs: 42);

        Assert.Equal("idle", stats["state"]);
        Assert.Equal(42L, stats["nowMs"]);
    }
}
