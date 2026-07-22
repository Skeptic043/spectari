using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Spectari.Encode;

internal static class SdrVideoColorConvention
{
    internal const ColorSpaceType ProcessorInput =
        ColorSpaceType.RgbFullG22NoneP709;

    internal const ColorSpaceType ProcessorOutput =
        ColorSpaceType.YcbcrStudioG22LeftP601;

    internal const NominalRange EncoderNominalRange =
        NominalRange.Range16_235;

    internal const VideoTransferMatrix EncoderYuvMatrix =
        VideoTransferMatrix.Bt601;

    internal static IReadOnlyList<(Guid Key, uint Value)> EncoderMediaTypeAttributes { get; } =
    [
        (MediaTypeAttributeKeys.VideoNominalRange, (uint)EncoderNominalRange),
        (MediaTypeAttributeKeys.YuvMatrix, (uint)EncoderYuvMatrix),
    ];
}
