using Spectari.Ui;
using Xunit;

namespace Spectari.Tests;

public sealed class StreamOptionsTests
{
    [Fact]
    public void PresetsStartWith480pAt30Fps()
    {
        StreamOptions.Preset first = StreamOptions.Presets[0];

        Assert.Equal((480, 30), (first.Height, first.Fps));
        Assert.Equal("480p · 30 fps", first.Label);
    }

    [Fact]
    public void A480p30StreamGetsBitratesForWeakConnections()
    {
        Assert.Equal((1000, 1500, 2500), StreamSession.BitrateTiers(854, 480, 30));
    }

    [Fact]
    public void A720p30StreamKeepsItsBitrates()
    {
        Assert.Equal((2500, 4000, 5500), StreamSession.BitrateTiers(1280, 720, 30));
    }
}
