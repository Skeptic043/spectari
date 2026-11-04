using System.Text.RegularExpressions;

namespace Spectari.Util;

internal enum NoteKind { Heading, Bullet, Text }

internal sealed record NoteLine(NoteKind Kind, string Text);

/// <summary>
/// Turns release notes into display lines. Only the markdown the release notes
/// use is handled: headings, bullets, emphasis, inline code, and links.
/// </summary>
internal static class ReleaseNotesText
{
    private static readonly Regex Link = new(@"\[([^\]]+)\]\((https?://[^)\s]+)\)", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<NoteLine> ToLines(string notes)
    {
        var lines = new List<NoteLine>();
        foreach (string raw in notes.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            NoteKind kind = NoteKind.Text;
            if (line.StartsWith('#'))
            {
                kind = NoteKind.Heading;
                line = line.TrimStart('#');
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                kind = NoteKind.Bullet;
                line = line[2..];
            }
            lines.Add(new NoteLine(kind, Inline(line.Trim())));
        }
        return lines;
    }

    private static string Inline(string text) =>
        Link.Replace(text, "$1 ($2)").Replace("**", "").Replace("`", "");
}
