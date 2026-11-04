namespace Spectari.Util;

internal enum FeedbackKind { Problem, Suggestion }

/// <summary>Builds the prefilled GitHub new-issue link for in-app feedback.</summary>
internal static class FeedbackLink
{
    /// <summary>GitHub refuses longer links, so long feedback is cut to fit.</summary>
    internal const int MaxUrlLength = 8000;
    private const int MaxTitleLength = 60;

    /// <summary>Only the user's text and the two versions go into the link.</summary>
    internal static string Build(FeedbackKind kind, string text, string appVersion, string windowsVersion)
    {
        text = text.Replace("\r\n", "\n").Trim();
        string firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length > MaxTitleLength) firstLine = firstLine[..MaxTitleLength] + "...";
        string title = $"{kind}: {firstLine}";
        string footer = $"\n\nSpectari {appVersion}\nWindows {windowsVersion}";

        string url = Url(title, text + footer);
        while (url.Length > MaxUrlLength && text.Length > 0)
        {
            text = text[..Math.Max(0, text.Length - 200)];
            url = Url(title, text + footer);
        }
        return url;
    }

    private static string Url(string title, string body) =>
        $"{ProjectLinks.NewIssue}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
}
