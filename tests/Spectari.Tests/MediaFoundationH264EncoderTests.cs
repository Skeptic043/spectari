using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class MediaFoundationH264EncoderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFriendlyNameUsesStableFallback(string? value)
    {
        Assert.Equal(
            MediaFoundationH264Encoder.DefaultFriendlyName,
            MediaFoundationH264Encoder.NormalizeFriendlyName(value));
    }

    [Fact]
    public void FriendlyNameIsTrimmed()
    {
        Assert.Equal(
            "NVIDIA H.264 Encoder MFT",
            MediaFoundationH264Encoder.NormalizeFriendlyName("  NVIDIA H.264 Encoder MFT  "));
    }
}
