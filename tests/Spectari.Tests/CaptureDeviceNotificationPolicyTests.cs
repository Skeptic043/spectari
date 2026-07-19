using Spectari.Ui;
using Xunit;

namespace Spectari.Tests;

public sealed class CaptureDeviceNotificationPolicyTests
{
    [Theory]
    [InlineData(0x0007)]
    [InlineData(0x8000)]
    [InlineData(0x8004)]
    public void DeviceTopologyChangesRequireRefresh(int eventType)
    {
        Assert.True(CaptureDeviceNotificationPolicy.RequiresRefresh(eventType));
    }

    [Theory]
    [InlineData(0x0018)]
    [InlineData(0x8001)]
    [InlineData(0x8003)]
    public void OtherDeviceMessagesDoNotRequireRefresh(int eventType)
    {
        Assert.False(CaptureDeviceNotificationPolicy.RequiresRefresh(eventType));
    }
}
