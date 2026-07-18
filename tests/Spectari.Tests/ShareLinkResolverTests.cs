using Xunit;

namespace Spectari.Tests;

public sealed class ShareLinkResolverTests
{
    [Fact]
    public void RanksTailscaleThenRoutedLanThenUnroutedLan()
    {
        IReadOnlyList<string> ranked = ShareLinkResolver.RankAddresses(
        [
            new ShareAddressCandidate("192.168.1.40", false, false, false),
            new ShareAddressCandidate("10.0.0.20", false, false, true),
            new ShareAddressCandidate("100.70.1.2", false, true, false),
            new ShareAddressCandidate("172.16.0.3", false, true, true),
            new ShareAddressCandidate("169.254.1.1", false, false, true),
            new ShareAddressCandidate("26.1.1.1", false, false, true),
            new ShareAddressCandidate("10.0.0.20", false, false, true),
        ]);

        Assert.Equal(
            ["100.70.1.2", "10.0.0.20", "192.168.1.40"],
            ranked);
    }

    [Fact]
    public void TailscaleAdapterIdentityWinsAddressRanking()
    {
        IReadOnlyList<string> ranked = ShareLinkResolver.RankAddresses(
        [
            new ShareAddressCandidate("192.168.1.40", false, false, true),
            new ShareAddressCandidate("10.1.2.3", true, false, false),
        ]);

        Assert.Equal(["10.1.2.3", "192.168.1.40"], ranked);
    }

    [Fact]
    public void BuildsIdleAndLiveLinksFromTheirOwnPortsAndKeys()
    {
        HostAccessService access = AccessService();
        var resolver = new ShareLinkResolver(access, () => ["100.80.1.2"]);
        var idle = new ShareLinkContext(
            IdlePort: 8093,
            LivePort: 0,
            HasLiveServer: false,
            LiveServerLocalOnly: false,
            IdleServerLocalOnly: false,
            LiveViewKey: null,
            PendingViewKey: "pending");
        var live = idle with
        {
            LivePort = 9000,
            HasLiveServer = true,
            LiveViewKey = "live",
        };

        Assert.Equal("http://100.80.1.2:8093/?k=pending", resolver.ResolvePrimaryUrl(idle, ""));
        Assert.Equal("http://100.80.1.2:9000/?k=live", resolver.ResolvePrimaryUrl(live, ""));
        Assert.Equal("http://100.80.1.2:9000/grid", resolver.ResolvePrimaryUrl(live, "grid"));
    }

    [Fact]
    public void PrimaryLinkRequiresPersistedLanStateForTheExactPort()
    {
        HostAccessService access = AccessService();
        var resolver = new ShareLinkResolver(access, () => ["192.168.1.40"]);
        ShareLinkContext context = IdleContext(8093, localOnly: false);

        Assert.Equal("http://localhost:8093/?k=pending", resolver.ResolvePrimaryUrl(context, ""));

        access.RestorePersistedState(allowLan: true, port: 8094);
        Assert.Equal("http://localhost:8093/?k=pending", resolver.ResolvePrimaryUrl(context, ""));

        access.RestorePersistedState(allowLan: true, port: 8093);
        Assert.Equal("http://192.168.1.40:8093/?k=pending", resolver.ResolvePrimaryUrl(context, ""));

        context = context with { IdleServerLocalOnly = true };
        Assert.Equal("http://localhost:8093/?k=pending", resolver.ResolvePrimaryUrl(context, ""));
    }

    [Fact]
    public void TailscaleAddressRemainsAdvertisedWithoutLanState()
    {
        HostAccessService access = AccessService();
        var resolver = new ShareLinkResolver(
            access,
            () => ["192.168.1.40", "100.80.1.2"]);

        string url = resolver.ResolvePrimaryUrl(IdleContext(8093, localOnly: false), "");

        Assert.Equal("http://100.80.1.2:8093/?k=pending", url);
    }

    [Fact]
    public void ExplicitLanLinkReturnsAddressAndWarnsWhenTrustIsUnconfirmed()
    {
        HostAccessService access = AccessService();
        var resolver = new ShareLinkResolver(access, () => ["192.168.1.40"]);

        LanLinkResolution unconfirmed = resolver.ResolveLanLink(
            IdleContext(8093, localOnly: false));
        LanLinkResolution localOnly = resolver.ResolveLanLink(
            IdleContext(8093, localOnly: true));

        Assert.Equal("http://192.168.1.40:8093/?k=pending", unconfirmed.Url);
        Assert.Equal(LanLinkWarning.AccessNotConfirmed, unconfirmed.Warning);
        Assert.Equal("http://192.168.1.40:8093/?k=pending", localOnly.Url);
        Assert.Equal(LanLinkWarning.ServerLocalOnly, localOnly.Warning);
    }

    [Fact]
    public void ExplicitLanLinkReportsWhenNoLanAddressExists()
    {
        HostAccessService access = AccessService();
        var resolver = new ShareLinkResolver(access, () => ["100.80.1.2"]);

        LanLinkResolution resolution = resolver.ResolveLanLink(
            IdleContext(8093, localOnly: false));

        Assert.Null(resolution.Url);
        Assert.Equal(LanLinkWarning.NoLanAddress, resolution.Warning);
    }

    private static HostAccessService AccessService() =>
        new(action => action(), _ => { });

    private static ShareLinkContext IdleContext(int port, bool localOnly) => new(
        IdlePort: port,
        LivePort: 0,
        HasLiveServer: false,
        LiveServerLocalOnly: false,
        IdleServerLocalOnly: localOnly,
        LiveViewKey: null,
        PendingViewKey: "pending");
}
