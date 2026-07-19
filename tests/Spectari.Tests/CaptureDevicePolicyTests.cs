using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class CaptureDevicePolicyTests
{
    [Fact]
    public void DisplayNamesAreDeterministicAndDisambiguateDuplicates()
    {
        CaptureDeviceDescription[] devices =
        [
            new("device-z", "USB Camera"),
            new("device-a", " USB Camera "),
            new("device-a", "Duplicate identity"),
        ];

        IReadOnlyList<CaptureDeviceDisplayItem> items =
            CaptureDevicePolicy.PrepareDisplayItems(devices);

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("device-a", item.Device.SymbolicLink);
                Assert.Equal("USB Camera", item.DisplayName);
            },
            item =>
            {
                Assert.Equal("device-z", item.Device.SymbolicLink);
                Assert.Equal("USB Camera (2)", item.DisplayName);
            });
    }

    [Fact]
    public void PreferredFormatBalancesPixelRateAndFrameRateBelowTheUiCeiling()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 3840, 2160, 15),
            Format(CaptureDevicePixelFormat.Yuy2, 1280, 720, 120),
            Format(CaptureDevicePixelFormat.Nv12, 1920, 1080, 60),
            Format(CaptureDevicePixelFormat.Mjpeg, 1920, 1080, 30),
        ];

        CaptureDeviceFormat? selected = CaptureDevicePolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
        Assert.Equal(1920, selected.Width);
        Assert.Equal(1080, selected.Height);
        Assert.Equal(60, selected.RoundedFramesPerSecond);
    }

    [Fact]
    public void PreferredFormatUsesUncompressedSubtypeOrderForEquivalentModes()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 1920, 1080, 60),
            Format(CaptureDevicePixelFormat.Yuy2, 1920, 1080, 60),
            Format(CaptureDevicePixelFormat.Nv12, 1920, 1080, 60),
        ];

        CaptureDeviceFormat? selected = CaptureDevicePolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
    }

    private static CaptureDeviceFormat Format(
        CaptureDevicePixelFormat pixelFormat,
        int width,
        int height,
        uint fps) => new(pixelFormat, width, height, fps, 1);
}
