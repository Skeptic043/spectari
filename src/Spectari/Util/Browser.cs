using System.Diagnostics;

namespace Spectari.Util;

/// <summary>Opens web links in the default browser.</summary>
internal static class Browser
{
    /// <summary>
    /// Opens an http(s) link. Anything else is refused: the shell would launch
    /// a file: or custom-protocol address as a local program.
    /// </summary>
    internal static void Open(string? url)
    {
        if (!IsWebLink(url))
        {
            Console.Error.WriteLine("[browser] refused to open a link that is not http or https.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[browser] could not open a link: {ex.Message}");
        }
    }

    internal static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
