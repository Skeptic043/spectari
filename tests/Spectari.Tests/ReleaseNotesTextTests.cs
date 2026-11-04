using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class ReleaseNotesTextTests
{
    [Fact]
    public void HeadingsBulletsAndTextBecomeLinesWithoutMarkdownSymbols()
    {
        IReadOnlyList<NoteLine> lines = ReleaseNotesText.ToLines(
            "## Added\r\n\r\n- **Help > Release notes** shows every release.\r\n* Second `item`\r\nPlain text.");

        Assert.Equal(
            [
                new NoteLine(NoteKind.Heading, "Added"),
                new NoteLine(NoteKind.Bullet, "Help > Release notes shows every release."),
                new NoteLine(NoteKind.Bullet, "Second item"),
                new NoteLine(NoteKind.Text, "Plain text."),
            ],
            lines);
    }

    [Fact]
    public void MarkdownLinksKeepTheirAddressSoTheyStayClickable()
    {
        NoteLine line = Assert.Single(ReleaseNotesText.ToLines(
            "- See [the guide](https://github.com/Skeptic043/spectari#readme) first."));

        Assert.Equal("See the guide (https://github.com/Skeptic043/spectari#readme) first.", line.Text);
    }

    [Fact]
    public void EmptyNotesHaveNoLines()
    {
        Assert.Empty(ReleaseNotesText.ToLines("  \n\n "));
    }
}
