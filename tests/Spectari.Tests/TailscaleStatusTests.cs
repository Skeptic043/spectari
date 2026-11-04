using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class TailscaleStatusTests
{
    [Fact]
    public void MissingCliMeansNotInstalledOrNotRunning()
    {
        Assert.Equal(TailscaleStage.NotInstalled, TailscaleStatus.Parse(null, installed: false).Stage);
        Assert.Equal(TailscaleStage.NotRunning, TailscaleStatus.Parse(null, installed: true).Stage);
    }

    [Theory]
    [InlineData("NeedsLogin", (int)TailscaleStage.SignedOut)]
    [InlineData("NeedsMachineAuth", (int)TailscaleStage.SignedOut)]
    [InlineData("Stopped", (int)TailscaleStage.Disconnected)]
    [InlineData("Starting", (int)TailscaleStage.NotRunning)]
    [InlineData("NoState", (int)TailscaleStage.NotRunning)]
    public void BackendStateMapsToAStage(string backendState, int expected)
    {
        Assert.Equal((TailscaleStage)expected, TailscaleStatus.Parse($$"""{ "BackendState": "{{backendState}}" }""", installed: true).Stage);
    }

    [Fact]
    public void RunningReportsThisPcsTailscaleName()
    {
        TailscaleState state = TailscaleStatus.Parse("""
            { "BackendState": "Running", "Self": { "HostName": "DESKTOP-ABC", "DNSName": "den-pc.example-tailnet.ts.net." } }
            """, installed: true);

        Assert.Equal(TailscaleStage.Running, state.Stage);
        Assert.Equal("den-pc", state.MachineName);
    }

    [Fact]
    public void UnreadableOutputIsNotTreatedAsConnected()
    {
        Assert.Equal(TailscaleStage.NotRunning, TailscaleStatus.Parse("not json", installed: true).Stage);
    }
}
