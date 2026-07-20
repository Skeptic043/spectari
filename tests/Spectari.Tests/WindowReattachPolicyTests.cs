using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class WindowReattachPolicyTests
{
    [Fact]
    public void SelectsOnlySameApplicationWindowsThatWereNotPresentAtLoss()
    {
        WindowDescription lost = Window(10, 100, "game", "Game");
        WindowDescription existingSecondWindow = Window(20, 100, "game", "Settings");
        var policy = new WindowReattachPolicy(
            "game",
            WindowReattachPolicy.IdentityOf(lost),
            [existingSecondWindow]);

        WindowDescription candidate = Window(30, 200, "GAME", "Game");
        WindowDescription otherApplication = Window(40, 300, "browser", "Game");

        Assert.False(policy.IsEligible(lost));
        Assert.False(policy.IsEligible(existingSecondWindow));
        Assert.False(policy.IsEligible(otherApplication));
        Assert.True(policy.IsEligible(candidate));
        Assert.Equal(candidate, policy.SelectCandidate(
            [lost, existingSecondWindow, otherApplication, candidate]));
    }

    [Fact]
    public void WindowHandleReusedByANewProcessIsANewWindowIdentity()
    {
        WindowDescription lost = Window(10, 100, "game", "Game");
        var policy = new WindowReattachPolicy(
            "game",
            WindowReattachPolicy.IdentityOf(lost),
            [lost]);

        WindowDescription reusedHandle = Window(10, 200, "game", "Game");

        Assert.True(policy.IsEligible(reusedHandle));
        Assert.Equal(reusedHandle, policy.SelectCandidate([reusedHandle]));
    }

    [Fact]
    public void UnknownApplicationIdentityNeverMatchesUnrelatedWindows()
    {
        WindowDescription lost = Window(10, 0, "?", "Game");
        var policy = new WindowReattachPolicy(
            "?",
            WindowReattachPolicy.IdentityOf(lost),
            []);

        Assert.Null(policy.SelectCandidate([Window(20, 0, "?", "Other")]));
    }

    [Fact]
    public void SurfaceLessNewWindowDoesNotQualify()
    {
        WindowDescription lost = Window(10, 100, "game", "Game");
        var policy = new WindowReattachPolicy(
            "game",
            WindowReattachPolicy.IdentityOf(lost),
            []);

        Assert.Null(policy.SelectCandidate([
            Window(20, 200, "game", "Starting", width: 1, height: 720),
        ]));
    }

    [Fact]
    public void SelectionIsDeterministicAcrossEnumerationOrders()
    {
        WindowDescription lost = Window(10, 100, "game", "Game");
        var policy = new WindowReattachPolicy(
            "game",
            WindowReattachPolicy.IdentityOf(lost),
            []);
        WindowDescription small = Window(20, 200, "game", "A", 800, 600);
        WindowDescription largeZulu = Window(30, 300, "game", "Zulu", 1920, 1080);
        WindowDescription largeAlpha = Window(40, 400, "game", "Alpha", 1920, 1080);

        WindowDescription? forward = policy.SelectCandidate(
            [small, largeZulu, largeAlpha]);
        WindowDescription? reverse = policy.SelectCandidate(
            [largeAlpha, largeZulu, small]);

        Assert.Equal(largeAlpha, forward);
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void EqualCandidatesUseProcessThenHandleAsStableTieBreakers()
    {
        WindowDescription lost = Window(10, 100, "game", "Game");
        var policy = new WindowReattachPolicy(
            "game",
            WindowReattachPolicy.IdentityOf(lost),
            []);
        WindowDescription laterPid = Window(20, 300, "game", "Game");
        WindowDescription higherHandle = Window(40, 200, "game", "Game");
        WindowDescription selected = Window(30, 200, "game", "Game");

        Assert.Equal(
            selected,
            policy.SelectCandidate([laterPid, higherHandle, selected]));
    }

    private static WindowDescription Window(
        int handle,
        uint pid,
        string processName,
        string title,
        int width = 1280,
        int height = 720) =>
        new(new IntPtr(handle), title, processName, pid, width, height);
}
