using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class AppSettingsStoreTests
{
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void UnreadableSettingsFallBackToDefaults(string json)
    {
        AppSettings settings = AppSettingsStore.Parse(json);

        Assert.Equal(8093, settings.Port);
        Assert.Equal("auto", settings.Encoder);
        Assert.True(settings.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void OutOfRangeOrUnknownValuesFallBackToSafeDefaults()
    {
        AppSettings settings = AppSettingsStore.Parse("""
            {
              "Port": 80,
              "Encoder": "h265_magic",
              "BitrateTier": "ultra",
              "SourceKind": "satellite",
              "StreamName": "  A name that is far too long to show to anyone watching  ",
              "AllowLan": true,
              "AllowLanPort": 99999,
              "AudioSource": null
            }
            """);

        Assert.Equal(8093, settings.Port);
        Assert.Equal("auto", settings.Encoder);
        Assert.Equal("med", settings.BitrateTier);
        Assert.Equal("window", settings.SourceKind);
        Assert.Equal("A name that is far too long to s", settings.StreamName);
        Assert.False(settings.AllowLan);
        Assert.Equal(0, settings.AllowLanPort);
        Assert.Equal("window", settings.AudioSource);
    }

    [Fact]
    public void ExistingSettingsCarryOver()
    {
        AppSettings settings = AppSettingsStore.Parse("""
            {
              "SourceKind": "monitor",
              "MonitorDeviceName": "DISPLAY-A",
              "MonitorIndex": 2,
              "PresetHeight": 720,
              "PresetFps": 30,
              "BitrateTier": "high",
              "AudioSource": "mode:desktop-except:discord",
              "Port": 9000,
              "StreamName": "Den",
              "Encoder": "h264_nvenc",
              "AllowLan": true,
              "AllowLanPort": 9000,
              "SkipUpdateVersion": "0.24.0.0"
            }
            """);

        Assert.Equal("monitor", settings.SourceKind);
        Assert.Equal("DISPLAY-A", settings.MonitorDeviceName);
        Assert.Equal(2, settings.LegacyMonitorIndex);
        Assert.Equal((720, 30), (settings.PresetHeight, settings.PresetFps));
        Assert.Equal("high", settings.BitrateTier);
        Assert.Equal("mode:desktop-except:discord", settings.AudioSource);
        Assert.Equal(9000, settings.Port);
        Assert.Equal("Den", settings.StreamName);
        Assert.Equal("h264_nvenc", settings.Encoder);
        Assert.True(settings.AllowLan);
        Assert.Equal(9000, settings.AllowLanPort);
        Assert.Equal("0.24.0.0", settings.SkipUpdateVersion);
        Assert.True(settings.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var original = new AppSettings { Port = 9100, StreamName = "Den", CheckForUpdatesOnStartup = false };

        AppSettings restored = AppSettingsStore.Parse(AppSettingsStore.Serialize(original));

        Assert.Equal(9100, restored.Port);
        Assert.Equal("Den", restored.StreamName);
        Assert.False(restored.CheckForUpdatesOnStartup);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("libx264", true)]
    [InlineData("h264_nvenc", true)]
    [InlineData("hevc_nvenc", false)]
    [InlineData("", false)]
    public void OnlyKnownEncodersAreAccepted(string encoder, bool known)
    {
        Assert.Equal(known, StreamLimits.IsKnownEncoder(encoder));
    }

    [Fact]
    public void EveryEncoderChoiceInTheAppIsKnown()
    {
        Assert.All(Spectari.Ui.StreamOptions.Encoders, choice => Assert.True(StreamLimits.IsKnownEncoder(choice.Value)));
    }
}
