using System.Collections.Specialized;
using System.Web;
using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class FeedbackLinkTests
{
    private static NameValueCollection Query(string url)
    {
        Assert.StartsWith("https://github.com/Skeptic043/spectari/issues/new?", url);
        return HttpUtility.ParseQueryString(new Uri(url).Query);
    }

    [Fact]
    public void ProblemOpensAnIssueWithTheTextAndVersions()
    {
        NameValueCollection query = Query(FeedbackLink.Build(
            FeedbackKind.Problem, "Audio drops after a minute\nOnly on the capture card.", "v0.24", "10.0.26200.0"));

        Assert.Equal("Problem: Audio drops after a minute", query["title"]);
        Assert.Equal(
            "Audio drops after a minute\nOnly on the capture card.\n\nSpectari v0.24\nWindows 10.0.26200.0",
            query["body"]);
        Assert.Equal(2, query.Count);
    }

    [Fact]
    public void SuggestionTitleSaysSuggestion()
    {
        Assert.Equal("Suggestion: Dark grid", Query(FeedbackLink.Build(
            FeedbackKind.Suggestion, "  Dark grid  ", "v0.24", "10.0"))["title"]);
    }

    [Fact]
    public void LongFirstLineIsShortenedInTheTitle()
    {
        string title = Query(FeedbackLink.Build(
            FeedbackKind.Problem, new string('a', 200), "v0.24", "10.0"))["title"]!;

        Assert.Equal("Problem: " + new string('a', 60) + "...", title);
    }

    [Fact]
    public void LongTextIsCutSoTheLinkStaysUnderGitHubsLimitAndKeepsTheVersions()
    {
        string url = FeedbackLink.Build(FeedbackKind.Problem, new string('ü', 20000), "v0.24", "10.0");

        Assert.True(url.Length <= FeedbackLink.MaxUrlLength);
        Assert.EndsWith("\n\nSpectari v0.24\nWindows 10.0", Query(url)["body"]);
    }
}
