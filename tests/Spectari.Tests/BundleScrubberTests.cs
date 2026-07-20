using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class BundleScrubberTests
{
    [Fact]
    public void ScrubRemovesPersonalDataAndKeepsTechnicalDetail()
    {
        string machine = Environment.MachineName;
        string domain = Environment.UserDomainName;
        string user = Environment.UserName;
        string input = $"""
            machine {machine}
            domain {domain}
            account {user}
            stream Quiet Friday Game
            source window 'Private bank tab' [browser]
            file C:\Users\PrivatePerson\Documents\capture.txt
            tailscale 100.75.4.3 fd7a:115c:a1e0::5 friend.example.ts.net
            capture HRESULT 0x887A0005 after 512 ms on Example GPU
            """;

        string scrubbed = BundleScrubber.Scrub(
            input,
            extraPrivateValues: ["Quiet Friday Game"]);

        Assert.DoesNotContain(machine, scrubbed, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(domain))
            Assert.DoesNotContain(domain, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quiet Friday Game", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("Private bank tab", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("100.75.4.3", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("fd7a:115c:a1e0::5", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("friend.example.ts.net", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HRESULT 0x887A0005 after 512 ms on Example GPU", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubRemovesSecretBearingUrlsAndExactSecrets()
    {
        const string key = "abc-DEF_1234567890xyz";
        string input = $"watch https://100.70.2.4:8093/?k={key}&mode=grid raw {key}";

        string scrubbed = BundleScrubber.Scrub(input, [key]);

        Assert.DoesNotContain("https://", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key, scrubbed, StringComparison.Ordinal);
        Assert.Contains("[viewer link removed]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("raw [key]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactKeyParamHandlesTheFullViewerKeyAlphabet()
    {
        string scrubbed = BundleScrubber.RedactKeyParam(
            "https://localhost:8093/?k=abc-DEF_123&mode=grid");

        Assert.Equal("https://localhost:8093/?k=[key]&mode=grid", scrubbed);
    }

    [Fact]
    public void ScrubKeepsNamedPipeDiagnostics()
    {
        const string input = @"[encoder] ffmpeg -i \\.\pipe\spectari-audio-8093 -f mp4 pipe:1";

        string scrubbed = BundleScrubber.Scrub(input);

        Assert.Equal(input, scrubbed);
    }
}
