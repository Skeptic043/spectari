using System.Net;

namespace Spectari.Server;

internal sealed class ViewerAccessPolicy(int port, IEnumerable<string> names, Func<IEnumerable<IPAddress>> addresses)
{
    internal const string BootstrapHeader = "X-Spectari-Bootstrap";
    private readonly HashSet<string> _names = names.Select(CanonicalHost).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal bool AllowsAuthority(Uri? target, string? authority)
    {
        if (target is null || target.Scheme != "http" || target.Port != port
            || !TryOrigin("http://" + authority, out Uri? supplied)
            || !SameOrigin(target, supplied!)) return false;
        string host = CanonicalHost(target.IdnHost);
        if (IPAddress.TryParse(host, out IPAddress? ip))
            return IPAddress.IsLoopback(ip) || addresses().Any(a => Normalize(a).Equals(Normalize(ip)));
        return _names.Contains(host);
    }

    internal static bool AllowsOrigin(Uri target, string? origin) =>
        origin is null || TryOrigin(origin, out Uri? parsed) && SameOrigin(target, parsed!);

    internal static bool AllowsBootstrap(Uri target, string? origin, string? marker, IPAddress? source) =>
        marker == "1" && AllowsOrigin(target, origin) && IsTrustedCaller(source);

    internal static bool KeyMatches(string? required, string? supplied) => required is null || required == supplied;

    internal static bool IsTrustedCaller(IPAddress? address)
    {
        if (address is null) return false;
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private static string CanonicalHost(string host) => host.TrimEnd('.').Trim('[', ']').ToLowerInvariant();
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Port == b.Port
        && CanonicalHost(a.IdnHost) == CanonicalHost(b.IdnHost);

    private static bool TryOrigin(string value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace)
            || value.Contains(',') || value.Contains('\\') || value.Contains('%')) return false;
        int start = value.IndexOf("://", StringComparison.Ordinal);
        if (start < 0 || value[(start + 3)..].IndexOfAny(['/', '?', '#', '@']) >= 0) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
    }
}
