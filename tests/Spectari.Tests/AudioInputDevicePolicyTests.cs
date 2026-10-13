using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class AudioInputDevicePolicyTests
{
    [Fact]
    public void OnlyCaptureSharesOfferInputsAndMatchingContainerWins()
    {
        Guid card = new("11111111-1111-1111-1111-111111111111");
        AudioInputDeviceDescription[] devices = [new("mic", "Microphone"), new("card", "Card", card)];
        Assert.Empty(AudioInputDevicePolicy.ForSource(devices, SourceKind.Window, card));
        Assert.Empty(AudioInputDevicePolicy.ForSource(devices, SourceKind.Monitor, card));
        Assert.Equal("card", Assert.Single(AudioInputDevicePolicy.ForSource(devices, SourceKind.CaptureDevice, card)).Device.Id);
        Assert.Equal(2, AudioInputDevicePolicy.ForSource(devices, SourceKind.CaptureDevice, null).Count);
        Assert.Equal(2, AudioInputDevicePolicy.ForSource(devices, SourceKind.CaptureDevice, Guid.Empty).Count);
        Assert.Equal(2, AudioInputDevicePolicy.ForSource(devices, SourceKind.CaptureDevice, Guid.NewGuid()).Count);
    }

    [Fact]
    public void MultipleEndpointsOnOneCardProduceOneStableChoice()
    {
        Guid card = Guid.NewGuid();
        AudioInputDeviceDescription[] devices = [new("b", "Card", card), new("a", "Card", card)];
        Assert.Equal("a", Assert.Single(AudioInputDevicePolicy.ForSource(devices, SourceKind.CaptureDevice, card)).Device.Id);
    }
    [Fact]
    public void DeduplicatesStableIdsAndDisambiguatesDuplicateNames()
    {
        IReadOnlyList<AudioInputDeviceDisplayItem> items =
            AudioInputDevicePolicy.PrepareDisplayItems(
            [
                Device("device-b", "Capture Card"),
                Device("device-a", "Capture Card"),
                Device("DEVICE-A", "Ignored duplicate"),
                Device("", "Missing id"),
            ]);

        Assert.Equal(["device-a", "device-b"], items.Select(item => item.Device.Id));
        Assert.Equal(
            [
                "Capture device audio - Capture Card",
                "Capture device audio - Capture Card (2)",
            ],
            items.Select(item => item.DisplayName));
    }

    private static AudioInputDeviceDescription Device(
        string id,
        string name) => new(id, name);
}
