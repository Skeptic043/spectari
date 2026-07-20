using System.Text.RegularExpressions;

namespace Spectari.Util;

/// <summary>Removes secrets and personal data before text reaches a persisted
/// log or clipboard report.</summary>
public static class BundleScrubber
{
    private static readonly Regex KeyParam =
        new(@"[?&]k=[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    private static readonly Regex SecretBearingUrl =
        new("https?://[^\\s\\\"']*[?&]k=[A-Za-z0-9_\\-]+[^\\s\\\"']*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WindowTitle =
        new(@"window '.*?' \[", RegexOptions.Compiled);

    public static string RedactKeyParam(string text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : KeyParam.Replace(text, m => m.Value[0] + "k=[key]");

    private static readonly Regex TailscaleIp =
        new(@"100\.\d{1,3}\.\d{1,3}\.\d{1,3}", RegexOptions.Compiled);
    private static readonly Regex TailscaleIpv6 =
        new(@"\bfd7a:115c:a1e0:[0-9a-f:]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TailscaleDnsName =
        new(@"\b[a-z0-9-]+\.[a-z0-9-]+\.ts\.net\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Named pipes are transport identifiers, not file paths, and remain useful
    // for diagnosing encoder setup.
    private static readonly Regex WindowsPath =
        new(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\(?!\.\\pipe\\))[^\r\n]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Fails closed so scrub errors cannot expose the original text.</summary>
    public static string Scrub(
        string text,
        IEnumerable<string?>? extraSecrets = null,
        IEnumerable<string?>? extraPrivateValues = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            string s = text;

            s = SecretBearingUrl.Replace(s, "[viewer link removed]");
            if (extraSecrets is not null)
                foreach (string secret in extraSecrets
                             .Where(x => !string.IsNullOrEmpty(x))
                             .Select(x => x!)
                             .Distinct()
                             .OrderByDescending(x => x.Length))
                    s = s.Replace(secret, "[key]", StringComparison.Ordinal);

            s = KeyParam.Replace(s, m => m.Value[0] + "k=[key]");

            if (extraPrivateValues is not null)
                s = ReplaceValues(s, extraPrivateValues, "[private]");
            s = ReplaceValues(s,
                [Environment.MachineName, Environment.UserDomainName],
                "[machine]");
            s = ReplaceValues(s, [Environment.UserName], "[user]");

            s = WindowTitle.Replace(s, "window '[title]' [");
            s = WindowsPath.Replace(s, "[path]");
            s = TailscaleIp.Replace(s, "100.x.x.x");
            s = TailscaleIpv6.Replace(s, "[tailscale address]");
            s = TailscaleDnsName.Replace(s, "[tailscale address]");

            return s;
        }
        catch
        {
            return "(log scrub failed; diagnostic text withheld)";
        }
    }

    private static string ReplaceValues(string text, IEnumerable<string?> values, string replacement)
    {
        string result = text;
        foreach (string value in values
                     .Where(value => !string.IsNullOrEmpty(value))
                     .Select(value => value!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(value => value.Length))
        {
            string pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}_])";
            result = Regex.Replace(result, pattern, replacement, RegexOptions.IgnoreCase);
        }
        return result;
    }
}
