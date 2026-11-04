using Spectari.Util;
using static Spectari.Ui.Palette;

namespace Spectari.Ui;

/// <summary>Collects feedback and opens it as a prefilled GitHub issue for the user to review and post.</summary>
internal sealed class FeedbackDialog : Form
{
    private readonly RadioButton _problem = new() { Text = "Problem", AutoSize = true, Checked = true };
    private readonly RadioButton _suggestion = new() { Text = "Suggestion", AutoSize = true };
    private readonly TextBox _text = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        AcceptsReturn = true, MaxLength = 4000, BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "Describe the problem or idea.",
    };

    internal static void ShowFor(IWin32Window owner)
    {
        using var dialog = new FeedbackDialog();
        dialog.ShowDialog(owner);
    }

    private FeedbackDialog()
    {
        Text = "Send feedback on GitHub";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(460, 340);
        Padding = new Padding(14);

        var intro = new Label
        {
            Text = "Your feedback opens as a public issue on GitHub, where you can review it " +
                "before posting. Posting needs a GitHub account.",
            Dock = DockStyle.Top, AutoSize = false, Height = 44, ForeColor = Dim,
        };
        var kind = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        kind.Controls.Add(_problem);
        kind.Controls.Add(_suggestion);

        var send = new Button { Text = "Continue to GitHub", AutoSize = true, MinimumSize = new Size(82, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, MinimumSize = new Size(82, 28) };
        send.Click += (_, _) => Send();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(send);

        Controls.Add(_text);
        Controls.Add(buttons);
        Controls.Add(kind);
        Controls.Add(intro);
        CancelButton = cancel;
        DarkTheme.ApplyToDialog(this);
        send.BackColor = AccentDark;
    }

    private void Send()
    {
        if (string.IsNullOrWhiteSpace(_text.Text))
        {
            _text.Focus();
            return;
        }
        FeedbackKind kind = _problem.Checked ? FeedbackKind.Problem : FeedbackKind.Suggestion;
        string version = UpdateChecker.DisplayVersion(AppVersion.Current) ?? AppVersion.Current;
        Browser.Open(FeedbackLink.Build(kind, _text.Text, version, Environment.OSVersion.Version.ToString()));
        Close();
    }
}
