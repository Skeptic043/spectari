using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spectari.Util;

/// <summary>The installable zip of a release, with the checksum GitHub published for it.</summary>
internal sealed record ReleasePackage(string AssetName, Uri DownloadUri, long Size, byte[] Sha256);

/// <summary>The latest full release. Package is null when no asset can be installed safely.</summary>
internal sealed record LatestRelease(string Tag, Version Version, ReleasePackage? Package);

/// <summary>Fail-silent lookup and pure numeric comparison for full releases.</summary>
internal static class UpdateChecker
{
    private static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/Skeptic043/spectari/releases/latest");
    private const string DownloadUrlPrefix =
        "https://github.com/Skeptic043/spectari/releases/download/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Client = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };
    private static readonly Regex PackageAssetName = new(
        @"^Spectari-v(?<version>[^/\\]+)\.zip$", RegexOptions.CultureInvariant);

    internal const string LatestReleasePageUrl =
        "https://github.com/Skeptic043/spectari/releases/latest";

    /// <summary>Makes one anonymous request. Null means the check failed.</summary>
    internal static async Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
            request.Headers.UserAgent.ParseAdd("Spectari-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseLatestRelease(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a GitHub release object. Drafts, prereleases, and unparseable tags
    /// yield null. The package is kept only when exactly one asset is named for
    /// the tag's version, downloads over HTTPS from this repository, and carries
    /// a SHA-256 digest.
    /// </summary>
    internal static LatestRelease? ParseLatestRelease(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;

            string? tag = GetString(root, "tag_name");
            Version? version = ParseVersion(tag);
            if (tag is null || version is null) return null;

            ReleasePackage? package = null;
            int matches = 0;
            if (root.TryGetProperty("assets", out JsonElement assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (!IsPackageAssetFor(GetString(asset, "name"), version)) continue;
                    matches++;
                    package = ReadPackage(asset);
                }
            }

            return new LatestRelease(tag, version, matches == 1 ? package : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the asset name is Spectari-vX.Y.Z.zip for exactly this version.</summary>
    internal static bool IsPackageAssetFor(string? assetName, Version version)
    {
        if (assetName is null) return false;
        Match match = PackageAssetName.Match(assetName);
        return match.Success && ParseVersion(match.Groups["version"].Value) is { } assetVersion
            && assetVersion.Equals(version);
    }

    /// <summary>Accepts only "sha256:" followed by 64 hex digits.</summary>
    internal static byte[]? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string hex = digest[prefix.Length..];
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(hex);
    }

    private static ReleasePackage? ReadPackage(JsonElement asset)
    {
        string? name = GetString(asset, "name");
        string? url = GetString(asset, "browser_download_url");
        byte[]? sha256 = ParseSha256Digest(GetString(asset, "digest"));
        long size = asset.TryGetProperty("size", out JsonElement sizeElement)
            && sizeElement.ValueKind == JsonValueKind.Number
            && sizeElement.TryGetInt64(out long parsed) ? parsed : 0;

        if (name is null || sha256 is null || size <= 0
            || url is null || !url.StartsWith(DownloadUrlPrefix, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return new ReleasePackage(name, uri, size, sha256);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Parses two to four numeric components, normalizing missing components to
    /// zero. A leading v and local build metadata are ignored.
    /// </summary>
    internal static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string numeric = value.Trim();
        if (numeric.StartsWith('v')) numeric = numeric[1..];

        int metadata = numeric.IndexOf('+');
        if (metadata >= 0)
        {
            if (metadata == 0 || metadata == numeric.Length - 1
                || numeric.IndexOf('+', metadata + 1) >= 0)
                return null;
            numeric = numeric[..metadata];
        }

        string[] parts = numeric.Split('.');
        if (parts.Length is < 2 or > 4) return null;

        int[] components = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture,
                    out components[i]))
                return null;
        }

        return new Version(components[0], components[1], components[2], components[3]);
    }

    /// <summary>True only when both inputs parse and remote is numerically newer.</summary>
    internal static bool IsRemoteVersionNewer(string? current, string? remote)
    {
        Version? currentVersion = ParseVersion(current);
        Version? remoteVersion = ParseVersion(remote);
        return currentVersion is not null && remoteVersion is not null
            && remoteVersion.CompareTo(currentVersion) > 0;
    }

    /// <summary>
    /// True when the release is newer than the running build and is not the
    /// version the user dismissed. Versions compare numerically, so v0.23 and
    /// 0.23.0 are the same dismissal.
    /// </summary>
    internal static bool ShouldOffer(string? current, string? remote, string? skippedVersion) =>
        IsRemoteVersionNewer(current, remote)
        && !string.Equals(CanonicalVersion(remote), CanonicalVersion(skippedVersion), StringComparison.Ordinal);

    /// <summary>Canonical four-component value for persisted dismissal state.</summary>
    internal static string? CanonicalVersion(string? value) => ParseVersion(value)?.ToString(4);

    /// <summary>Short user-facing form such as v0.22 or v0.22.1; null when unparseable.</summary>
    internal static string? DisplayVersion(string? value)
    {
        Version? version = ParseVersion(value);
        if (version is null) return null;
        int parts = version.Revision != 0 ? 4 : version.Build != 0 ? 3 : 2;
        return "v" + version.ToString(parts);
    }
}
