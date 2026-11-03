using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class UpdateCheckerTests
{
    private const string Digest = "sha256:5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static string Release(
        string tag = "v0.23.0",
        bool prerelease = false,
        bool draft = false,
        string assets = "") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [{{assets}}]
        }
        """;

    private static string Asset(
        string name = "Spectari-v0.23.0.zip",
        string? url = null,
        string? digest = Digest,
        long size = 144201712) =>
        $$"""
        {
          "name": "{{name}}",
          "size": {{size}},
          "browser_download_url": "{{url ?? $"https://github.com/Skeptic043/spectari/releases/download/v0.23.0/{name}"}}"
          {{(digest is null ? "" : $", \"digest\": \"{digest}\"")}}
        }
        """;

    [Fact]
    public void FullReleaseSelectsTheVersionedZipWithItsDigest()
    {
        LatestRelease? release = UpdateChecker.ParseLatestRelease(Release(assets:
            Asset("Spectari-v0.22.0.zip") + "," + Asset() + "," + Asset("notes.txt")));

        Assert.NotNull(release);
        Assert.Equal("v0.23.0", release.Tag);
        Assert.Equal(new Version(0, 23, 0, 0), release.Version);
        Assert.NotNull(release.Package);
        Assert.Equal("Spectari-v0.23.0.zip", release.Package.AssetName);
        Assert.Equal(144201712, release.Package.Size);
        Assert.Equal(Convert.FromHexString(Digest["sha256:".Length..]), release.Package.Sha256);
        Assert.Equal(
            "https://github.com/Skeptic043/spectari/releases/download/v0.23.0/Spectari-v0.23.0.zip",
            release.Package.DownloadUri.AbsoluteUri);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PrereleasesAndDraftsAreNeverOffered(bool prerelease, bool draft)
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(
            Release(prerelease: prerelease, draft: draft, assets: Asset())));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    public void UnreadableReleasesFailTheCheck(string json)
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(json));
    }

    [Fact]
    public void ReleaseWithoutAnInstallableAssetIsStillReportedButHasNoPackage()
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset(digest: null)))!.Package);
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset(digest: "sha256:abc")))!.Package);
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset(digest: "md5:" + new string('a', 64))))!.Package);
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset(size: 0)))!.Package);
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset("Spectari-v0.23.0-rc.1.zip")))!.Package);
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset() + "," + Asset("Spectari-v0.23.zip")))!.Package);
    }

    [Theory]
    [InlineData("http://github.com/Skeptic043/spectari/releases/download/v0.23.0/Spectari-v0.23.0.zip")]
    [InlineData("https://github.com/someone-else/spectari/releases/download/v0.23.0/Spectari-v0.23.0.zip")]
    [InlineData("https://example.com/Skeptic043/spectari/releases/download/v0.23.0/Spectari-v0.23.0.zip")]
    public void PackagesFromOutsideThisRepositoryOverHttpsAreRejected(string url)
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(Release(assets: Asset(url: url)))!.Package);
    }

    [Theory]
    [InlineData("Spectari-v0.23.0.zip", true)]
    [InlineData("Spectari-v0.23.zip", true)]
    [InlineData("Spectari-v0.23.1.zip", false)]
    [InlineData("Spectari-v0.23.0-rc.1.zip", false)]
    [InlineData("Spectari-0.23.0.zip", false)]
    [InlineData("spectari-v0.23.0.zip.sig", false)]
    public void PackageAssetMustNameTheReleaseVersion(string name, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsPackageAssetFor(name, new Version(0, 23, 0, 0)));
    }

    [Theory]
    [InlineData(Digest, true)]
    [InlineData("SHA256:5F70BF18A086007016E948B04AED3B82103A36BEA41755B6CDDFAF10ACE3C6EF", true)]
    [InlineData("sha256:5f70bf18", false)]
    [InlineData("sha256:zz70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef", false)]
    [InlineData("sha512:5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef", false)]
    [InlineData(null, false)]
    public void DigestMustBeAFullSha256(string? digest, bool valid)
    {
        byte[]? parsed = UpdateChecker.ParseSha256Digest(digest);
        Assert.Equal(valid, parsed is { Length: 32 });
    }

    [Theory]
    [InlineData("0.22.0+d8012be", "v0.23.0", true)]
    [InlineData("0.22.0+d8012be", "v0.22.0", false)]
    [InlineData("0.22.1", "v0.22", false)]
    [InlineData("dev", "v0.23.0", false)]
    [InlineData("0.22.0", null, false)]
    public void OnlyANumericallyNewerReleaseIsAnUpdate(string current, string? remote, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsRemoteVersionNewer(current, remote));
    }

    [Theory]
    [InlineData("v0.22.0", "v0.22")]
    [InlineData("0.22.1+abc", "v0.22.1")]
    [InlineData("v1.2.3.4", "v1.2.3.4")]
    [InlineData("dev", null)]
    public void DisplayVersionDropsTrailingZeroComponents(string value, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.DisplayVersion(value));
    }
}
