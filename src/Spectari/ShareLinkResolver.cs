using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Spectari;

internal readonly record struct ShareAddressCandidate(
    string Address,
    bool IsTailscaleAdapter,
    bool IsVirtualAdapter,
    bool HasDefaultRoute);

internal readonly record struct ShareLinkContext(
    int IdlePort,
    int LivePort,
    bool HasLiveServer,
    bool LiveServerLocalOnly,
    bool IdleServerLocalOnly,
    string? LiveViewKey,
    string? PendingViewKey);

internal readonly record struct ActiveShareTarget(
    int Port,
    bool ServerLocalOnly,
    string? ViewKey);

internal enum ShareReachability
{
    LocalOnly,
    Tailscale,
    Lan,
    None,
}

internal enum LanLinkWarning
{
    None,
    NoLanAddress,
    ServerLocalOnly,
    AccessNotConfirmed,
}

internal readonly record struct LanLinkResolution(
    string? Url,
    int Port,
    LanLinkWarning Warning);

internal sealed class ShareLinkResolver
{
    private readonly HostAccessService _hostAccess;
    private readonly Func<IReadOnlyList<string>> _readAddresses;

    internal ShareLinkResolver(
        HostAccessService hostAccess,
        Func<IReadOnlyList<string>>? readAddresses = null)
    {
        _hostAccess = hostAccess;
        _readAddresses = readAddresses ?? (() => GetShareAddresses(includeLan: true));
    }

    internal string ResolvePrimaryUrl(ShareLinkContext context, string pathSuffix)
    {
        ActiveShareTarget target = ResolveActiveTarget(context);
        IReadOnlyList<string> addresses = _readAddresses();
        string host = ChooseAdvertisedHost(
            addresses,
            target.ServerLocalOnly,
            _hostAccess.IsLanAppliedForPort(target.Port));
        return BuildViewerUrl(host, target.Port, pathSuffix, target.ViewKey);
    }

    internal LanLinkResolution ResolveLanLink(ShareLinkContext context)
    {
        ActiveShareTarget target = ResolveActiveTarget(context);
        string? lanAddress = _readAddresses().FirstOrDefault(address => !IsTailscaleAddress(address));
        if (lanAddress is null)
            return new LanLinkResolution(null, target.Port, LanLinkWarning.NoLanAddress);

        LanLinkWarning warning = target.ServerLocalOnly
            ? LanLinkWarning.ServerLocalOnly
            : !_hostAccess.IsLanAppliedForPort(target.Port)
                ? LanLinkWarning.AccessNotConfirmed
                : LanLinkWarning.None;
        return new LanLinkResolution(
            BuildViewerUrl(lanAddress, target.Port, "", target.ViewKey),
            target.Port,
            warning);
    }

    internal ShareReachability ResolveReachability(int port, bool serverLocalOnly)
    {
        if (serverLocalOnly) return ShareReachability.LocalOnly;

        IReadOnlyList<string> addresses = _readAddresses();
        if (addresses.Any(IsTailscaleAddress)) return ShareReachability.Tailscale;
        if (_hostAccess.IsLanAppliedForPort(port)
            && addresses.Any(address => !IsTailscaleAddress(address)))
            return ShareReachability.Lan;
        return ShareReachability.None;
    }

    internal static ActiveShareTarget ResolveActiveTarget(ShareLinkContext context) =>
        new(
            context.LivePort > 0 ? context.LivePort : context.IdlePort,
            context.HasLiveServer ? context.LiveServerLocalOnly : context.IdleServerLocalOnly,
            context.LiveViewKey ?? context.PendingViewKey);

    internal static string ChooseAdvertisedHost(
        IReadOnlyList<string> rankedAddresses,
        bool serverLocalOnly,
        bool lanAccessConfirmed)
    {
        if (serverLocalOnly) return "localhost";

        string? tailscale = rankedAddresses.FirstOrDefault(IsTailscaleAddress);
        if (tailscale is not null) return tailscale;
        if (lanAccessConfirmed)
        {
            string? lan = rankedAddresses.FirstOrDefault(address => !IsTailscaleAddress(address));
            if (lan is not null) return lan;
        }
        return "localhost";
    }

    internal static string BuildViewerUrl(
        string host,
        int port,
        string pathSuffix,
        string? viewKey)
    {
        string url = $"http://{host}:{port}/{pathSuffix}";
        if (pathSuffix.Length == 0 && viewKey is not null)
            url += $"?k={viewKey}";
        return url;
    }

    internal static IReadOnlyList<string> RankAddresses(
        IEnumerable<ShareAddressCandidate> candidates)
    {
        var ranked = new List<(int Rank, string Address)>();
        foreach (ShareAddressCandidate candidate in candidates)
        {
            if (!IPAddress.TryParse(candidate.Address, out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetwork)
                continue;

            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254) continue;
            bool tailscaleRange = bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
            bool privateRange = bytes[0] == 10
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
            if (!tailscaleRange && !privateRange) continue;
            if (candidate.IsVirtualAdapter && !candidate.IsTailscaleAdapter && !tailscaleRange)
                continue;

            int rank = candidate.IsTailscaleAdapter || tailscaleRange
                ? 0
                : candidate.HasDefaultRoute ? 1 : 2;
            ranked.Add((rank, candidate.Address));
        }

        return ranked
            .OrderBy(item => item.Rank)
            .Select(item => item.Address)
            .Distinct()
            .ToList();
    }

    internal static List<string> GetShareAddresses(bool includeLan = true)
    {
        var candidates = new List<ShareAddressCandidate>();
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                string label = $"{adapter.Name} {adapter.Description}";
                bool isTailscale = label.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
                bool isVirtual =
                    label.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

                IPInterfaceProperties properties = adapter.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork);
                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    candidates.Add(new ShareAddressCandidate(
                        unicast.Address.ToString(), isTailscale, isVirtual, hasGateway));
                }
            }
        }
        catch
        {
        }

        IReadOnlyList<string> ranked = RankAddresses(candidates);
        if (ranked.Count > 0)
            return includeLan
                ? ranked.ToList()
                : ranked.Where(IsTailscaleAddress).ToList();

        try
        {
            List<string> fallback = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .ToList();
            return includeLan ? fallback : fallback.Where(IsTailscaleAddress).ToList();
        }
        catch
        {
            return [];
        }
    }

    internal static bool IsTailscaleAddress(string address)
    {
        string[] parts = address.Split('.');
        return parts.Length == 4
            && byte.TryParse(parts[0], out byte first) && first == 100
            && byte.TryParse(parts[1], out byte second) && second is >= 64 and <= 127
            && byte.TryParse(parts[2], out _)
            && byte.TryParse(parts[3], out _);
    }
}
