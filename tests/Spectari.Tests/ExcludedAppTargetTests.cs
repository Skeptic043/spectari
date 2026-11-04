using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class ExcludedAppTargetTests
{
    private const uint Own = 100;

    [Fact]
    public void ExcludesTheAppsRootProcessSoItsChildProcessesAreLeftOutToo()
    {
        ProcessEntry[] processes =
        [
            new(10, 1, "explorer"),
            new(20, 10, "Discord"),
            new(21, 20, "Discord"),
            new(22, 20, "Discord"),
        ];

        Assert.Equal(20u, ExcludedAppTarget.ChoosePid(processes, "discord", Own));
    }

    [Fact]
    public void ExcludesOnlySpectariWhileTheAppIsNotRunning()
    {
        ProcessEntry[] processes = [new(10, 1, "explorer"), new(30, 10, "game")];

        Assert.Equal(Own, ExcludedAppTarget.ChoosePid(processes, "discord", Own));
    }

    [Fact]
    public void FollowsTheAppWhenItStartsAndWhenItRestarts()
    {
        ProcessEntry[] processes = [];
        var raised = new List<uint>();
        using var target = new ExcludedAppTarget("discord", Own, () => processes);
        target.ProcessAttached += raised.Add;
        Assert.Equal(Own, target.AudioProcessId);

        processes = [new(20, 1, "Discord")];
        target.Poll();
        processes = [new(40, 1, "Discord")];
        target.Poll();
        target.Poll();

        Assert.Equal([20u, 40u], raised);
        Assert.Equal(40u, target.AudioProcessId);
    }
}
