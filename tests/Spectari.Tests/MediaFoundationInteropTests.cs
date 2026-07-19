using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class MediaFoundationInteropTests
{
    [Fact]
    public void AttributeInteropRoundTripsGuidAndString()
    {
        nint attributes = 0;
        int startupHr = MediaFoundationInterop.MFStartup(MediaFoundationInterop.MfVersion, 0);
        Assert.True(startupHr >= 0, $"MFStartup failed with HRESULT 0x{startupHr:X8}.");
        try
        {
            int createHr = MediaFoundationInterop.MFCreateAttributes(out attributes, 2);
            Assert.True(createHr >= 0, $"MFCreateAttributes failed with HRESULT 0x{createHr:X8}.");

            Guid guidKey = new("7DB58B22-CCB1-4D90-A1B2-0A9C9D345A01");
            Guid stringKey = new("7DB58B22-CCB1-4D90-A1B2-0A9C9D345A02");
            Guid expectedGuid = new("7DB58B22-CCB1-4D90-A1B2-0A9C9D345A03");
            MediaFoundationInterop.SetGuid(attributes, guidKey, expectedGuid);
            MediaFoundationInterop.SetString(attributes, stringKey, "capture-device");

            int getGuidHr = MediaFoundationInterop.GetGuid(attributes, guidKey, out Guid actualGuid);

            Assert.True(getGuidHr >= 0, $"GetGUID failed with HRESULT 0x{getGuidHr:X8}.");
            Assert.Equal(expectedGuid, actualGuid);
            Assert.Equal("capture-device", MediaFoundationInterop.GetString(attributes, stringKey));
        }
        finally
        {
            MediaFoundationInterop.Release(ref attributes);
            _ = MediaFoundationInterop.MFShutdown();
        }
    }
}
