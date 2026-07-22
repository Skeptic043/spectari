using Spectari.Encode;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Xunit;

namespace Spectari.Tests;

public sealed class SdrVideoColorConventionTests
{
    [Fact]
    public void CapturedDesktopIsFullRangeSrgb()
    {
        Assert.Equal(
            ColorSpaceType.RgbFullG22NoneP709,
            SdrVideoColorConvention.ProcessorInput);
    }

    [Fact]
    public void Nv12AndEncoderUseCpuLaneLimitedBt601Convention()
    {
        Assert.Equal(
            ColorSpaceType.YcbcrStudioG22LeftP601,
            SdrVideoColorConvention.ProcessorOutput);
        Assert.Equal(
            NominalRange.Range16_235,
            SdrVideoColorConvention.EncoderNominalRange);
        Assert.Equal(
            VideoTransferMatrix.Bt601,
            SdrVideoColorConvention.EncoderYuvMatrix);
    }

    [Fact]
    public void EncoderMediaTypesCarryMatchingRangeAndMatrixAttributes()
    {
        IReadOnlyDictionary<Guid, uint> attributes = SdrVideoColorConvention
            .EncoderMediaTypeAttributes
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

        Assert.Equal(2, attributes.Count);
        Assert.Equal(
            (uint)NominalRange.Range16_235,
            attributes[MediaTypeAttributeKeys.VideoNominalRange]);
        Assert.Equal(
            (uint)VideoTransferMatrix.Bt601,
            attributes[MediaTypeAttributeKeys.YuvMatrix]);
    }
}
