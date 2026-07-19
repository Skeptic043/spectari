using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class DefaultAudioDeviceChangePolicyTests
{
    [Fact]
    public void FirstEndpointIsBoundAndStableIdentityNeedsNoWork()
    {
        var policy = new DefaultAudioDeviceChangePolicy();

        Assert.Equal(DefaultAudioDeviceChangeAction.Bind, policy.Evaluate("speakers"));
        policy.MarkBound("speakers");

        Assert.Equal(DefaultAudioDeviceChangeAction.None, policy.Evaluate("SPEAKERS"));
    }

    [Fact]
    public void ChangedEndpointRebindsWithoutRestartingTheOwner()
    {
        var policy = new DefaultAudioDeviceChangePolicy();
        policy.MarkBound("speakers");

        Assert.Equal(DefaultAudioDeviceChangeAction.Bind, policy.Evaluate("headphones"));
        policy.MarkUnbound();
        Assert.Equal(DefaultAudioDeviceChangeAction.Bind, policy.Evaluate("headphones"));
        policy.MarkBound("headphones");

        Assert.Equal("headphones", policy.BoundDeviceId);
    }

    [Fact]
    public void MissingEndpointUnbindsUntilAReplacementAppears()
    {
        var policy = new DefaultAudioDeviceChangePolicy();
        policy.MarkBound("speakers");

        Assert.Equal(DefaultAudioDeviceChangeAction.Unbind, policy.Evaluate(null));
        policy.MarkUnbound();

        Assert.Equal(DefaultAudioDeviceChangeAction.None, policy.Evaluate(null));
        Assert.Equal(DefaultAudioDeviceChangeAction.Bind, policy.Evaluate("headphones"));
    }
}
