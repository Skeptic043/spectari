using Xunit;

namespace Spectari.Tests;

public sealed class CommandLineOptionsTests
{
    [Theory]
    [InlineData("--fps", "100000")]
    [InlineData("--bitrate", "-5")]
    [InlineData("--bitrate", "999999999")]
    [InlineData("--height", "50")]
    [InlineData("--height", "100000")]
    [InlineData("--encoder", "h265_magic")]
    [InlineData("--monitor", "-1")]
    [InlineData("--port", "0")]
    public void OutOfRangeValuesAreRejectedWithAMessage(string flag, string value)
    {
        var error = Assert.Throws<ArgumentException>(() => Program.Options.Parse([flag, value]));
        Assert.Contains(flag, error.Message);
    }

    [Fact]
    public void ValidValuesAreKept()
    {
        Program.Options options = Program.Options.Parse(
            ["--fps", "30", "--bitrate", "0", "--height", "720", "--encoder", "libx264", "--monitor", "1",
             "--name", "  A name that is far too long to show to anyone watching  "]);

        Assert.Equal(30, options.Fps);
        Assert.Equal(0, options.BitrateKbps);
        Assert.Equal(720, options.OutHeight);
        Assert.Equal("libx264", options.Encoder);
        Assert.Equal(1, options.Monitor);
        Assert.Equal("A name that is far too long to s", options.Name);
    }
}
