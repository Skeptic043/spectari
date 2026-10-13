using System.Net;
using Spectari.Server;
using Xunit;

namespace Spectari.Tests;

public sealed class ViewerAccessPolicyTests
{
    private static readonly Uri Target = new("http://host.test:8093/api/viewer-key");

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://host.test:8093", true)]
    [InlineData("http://HOST.test.:8093", true)]
    [InlineData("http://host.test", false)]
    [InlineData("https://host.test:8093", false)]
    [InlineData("http://other.test:8093", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData("http://host.test:8093/", false)]
    [InlineData("http://host.test:8093,http://other.test", false)]
    [InlineData("http://user@host.test:8093", false)]
    public void BrowserOriginIsExactOrAbsent(string? origin, bool allowed) =>
        Assert.Equal(allowed, ViewerAccessPolicy.AllowsOrigin(Target, origin));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:100.64.1.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.1.1", false)]
    [InlineData("192.168.1.2", false)]
    public void BootstrapNeedsBothAddressTrustAndMarker(string source, bool trusted)
    {
        IPAddress address = IPAddress.Parse(source);
        Assert.Equal(trusted, ViewerAccessPolicy.AllowsBootstrap(Target, null, "1", address));
        Assert.False(ViewerAccessPolicy.AllowsBootstrap(Target, null, null, address));
        Assert.False(ViewerAccessPolicy.AllowsBootstrap(Target, "null", "1", address));
        Assert.False(ViewerAccessPolicy.AllowsBootstrap(Target, "http://other.test:8093", "1", address));
    }

    [Theory]
    [InlineData("host.test:8093", true)]
    [InlineData("HOST.test.:8093", true)]
    [InlineData("other.test:8093", false)]
    [InlineData("host.test:8094", false)]
    [InlineData("host.test:8093/", false)]
    public void RequestHostCannotGrantItselfTrust(string authority, bool allowed)
    {
        var policy = new ViewerAccessPolicy(8093, ["host.test"], () => []);
        Assert.Equal(allowed, policy.AllowsAuthority(new Uri("http://" + authority), authority));
    }

    [Fact]
    public void CurrentInterfacesAreCheckedWithoutResolvingRemoteNames()
    {
        IPAddress[] addresses = [IPAddress.Parse("192.168.1.2")];
        var policy = new ViewerAccessPolicy(8093, [], () => addresses);
        var target = new Uri("http://192.168.1.2:8093/");
        Assert.True(policy.AllowsAuthority(target, target.Authority));
        addresses = [];
        Assert.False(policy.AllowsAuthority(target, target.Authority));
        Assert.False(policy.AllowsAuthority(Target, "other.test:8093"));
    }

    [Fact]
    public void BootstrapTrustDoesNotReplaceMediaAuthorization()
    {
        Assert.False(ViewerAccessPolicy.KeyMatches("synthetic-current", null));
        Assert.False(ViewerAccessPolicy.KeyMatches("synthetic-current", "synthetic-old"));
        Assert.True(ViewerAccessPolicy.KeyMatches("synthetic-current", "synthetic-current"));
        Assert.True(ViewerAccessPolicy.KeyMatches(null, null));
    }
}
