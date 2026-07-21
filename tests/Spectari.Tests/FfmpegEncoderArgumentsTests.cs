using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class FfmpegEncoderArgumentsTests
{
    [Theory]
    [InlineData(30, "-bsf:v setts=pts=N/30/TB:dts=N/30/TB:duration=1/30/TB")]
    [InlineData(60, "-bsf:v setts=pts=N/60/TB:dts=N/60/TB:duration=1/60/TB")]
    public void H264CopyLaneStampsEveryPacketAtSessionRate(int fps, string expected)
    {
        Assert.Equal(expected, FfmpegEncoder.H264CfrTimestampOptions(fps));
    }

    [Fact]
    public void H264TimestampRateMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegEncoder.H264CfrTimestampOptions(0));
    }
}
