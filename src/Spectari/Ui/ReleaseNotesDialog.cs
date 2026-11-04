using Spectari.Util;
using static Spectari.Ui.Palette;

namespace Spectari.Ui;

/// <summary>Shows release notes, either to confirm an update or to browse every release.</summary>
internal sealed class ReleaseNotesDialog : Form
{
    private readonly IReadOnlyList<GitHubRelease> _releases;
    private readonly RichTextBox _notes = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        DetectUrls = true, BackColor = Card, ForeColor = Fg, TabStop = false,
    };

    private readonly FlowLayoutPanel _buttons = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
        Padding = new Padding(0, 10, 0, 0),
    };

    /// <summary>Shows the missing versions' notes. True when the user chose to install.</summary>
    internal static bool ConfirmInstall(IWin32Window owner, IReadOnlyList<GitHubRelease> newer, string current, bool streaming)
    {
        string available = UpdateChecker.DisplayVersion(newer[0].Tag) ?? newer[0].Tag;
        string summary = $"Spectari {available} is available. You have {current}.";
        if (streaming) summary += "\nInstalling will close your stream.";

        using var dialog = new ReleaseNotesDialog("Update available", summary, newer);
        var install = dialog.AddButton(streaming ? "Close stream and install" : "Install", DialogResult.OK);
        var cancel = dialog.AddButton("Cancel", DialogResult.Cancel);
        install.BackColor = AccentDark;
        dialog.CancelButton = cancel;
        if (!streaming) dialog.AcceptButton = install;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>Shows every release's notes.</summary>
    internal static void ShowAll(IWin32Window owner, IReadOnlyList<GitHubRelease> releases)
    {
        using var dialog = new ReleaseNotesDialog("Release notes", null, releases);
        dialog.CancelButton = dialog.AddButton("Close", DialogResult.Cancel);
        dialog.ShowDialog(owner);
    }

    private ReleaseNotesDialog(string title, string? summary, IReadOnlyList<GitHubRelease> releases)
    {
        _releases = releases;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 480);
        MinimumSize = new Size(380, 300);
        Padding = new Padding(14);

        _notes.LinkClicked += (_, e) => Browser.Open(e.LinkText);
        Controls.Add(_notes);
        Controls.Add(_buttons);
        if (summary is not null)
        {
            Controls.Add(new Label
            {
                Text = summary, Dock = DockStyle.Top, AutoSize = true,
                Padding = new Padding(0, 0, 0, 10),
            });
        }
        DarkTheme.ApplyToDialog(this);
    }

    private Button AddButton(string text, DialogResult result)
    {
        var button = new Button
        {
            Text = text, DialogResult = result, AutoSize = true,
            MinimumSize = new Size(82, 28), Margin = new Padding(6, 0, 0, 0),
        };
        DarkTheme.Style(button);
        _buttons.Controls.Add(button);
        return button;
    }

    // Selection formatting is only kept once the rich text box has a handle.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        using var versionFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        using var headingFont = new Font(Font.FontFamily, 10f, FontStyle.Bold);

        foreach (GitHubRelease release in _releases)
        {
            Append(UpdateChecker.DisplayVersion(release.Tag) ?? release.Tag, versionFont);
            IReadOnlyList<NoteLine> lines = ReleaseNotesText.ToLines(release.Notes);
            if (lines.Count == 0) Append("No notes for this version.", Font, Dim);
            foreach (NoteLine line in lines)
            {
                switch (line.Kind)
                {
                    case NoteKind.Heading: Append(line.Text, headingFont); break;
                    case NoteKind.Bullet: Append("•  " + line.Text, Font, indent: 14); break;
                    default: Append(line.Text, Font); break;
                }
            }
            Append("", Font);
        }
        _notes.Select(0, 0);
    }

    private void Append(string text, Font font, Color? color = null, int indent = 0)
    {
        _notes.SelectionStart = _notes.TextLength;
        _notes.SelectionFont = font;
        _notes.SelectionColor = color ?? Fg;
        _notes.SelectionIndent = indent;
        _notes.SelectionHangingIndent = indent == 0 ? 0 : 12;
        _notes.AppendText(text + "\n");
    }
}
