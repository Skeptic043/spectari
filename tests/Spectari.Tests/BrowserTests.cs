using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class BrowserTests
{
    [Theory]
    [InlineData("https://github.com/Skeptic043/spectari", true)]
    [InlineData("http://example.com/page", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("calc.exe", false)]
    [InlineData(null, false)]
    public void OnlyWebLinksAreOpened(string? url, bool opens)
    {
        Assert.Equal(opens, Browser.IsWebLink(url));
    }
}
