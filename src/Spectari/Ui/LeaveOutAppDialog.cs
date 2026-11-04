using static Spectari.Ui.Palette;

namespace Spectari.Ui;

/// <summary>Asks which running app to leave out of desktop audio.</summary>
internal sealed class LeaveOutAppDialog : Form
{
    private readonly ListBox _apps = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
        BackColor = Card, ForeColor = Fg,
    };

    /// <summary>Re-reads the running apps and asks for one. True when an app was chosen.</summary>
    internal static bool Choose(IWin32Window owner, SourceSelectionModel sources)
    {
        sources.RefreshAudioApps();
        using var dialog = new LeaveOutAppDialog(sources.AudioAppNames, sources.DesktopExcludedApp);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog._apps.SelectedItem is not string app)
            return false;
        sources.SelectDesktopExcept(app);
        return true;
    }

    private LeaveOutAppDialog(IReadOnlyList<string> apps, string? current)
    {
        Text = "Leave an app out of desktop audio";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(360, 320);
        Padding = new Padding(14);

        var intro = new Label
        {
            Text = apps.Count > 0
                ? "Viewers hear all desktop audio except this app, for example a voice chat app."
                : "No apps with open windows are running. Open the app first, then try again.",
            Dock = DockStyle.Top, AutoSize = false, Height = 40, ForeColor = Dim,
        };
        _apps.Items.AddRange(apps.ToArray<object>());
        int index = current is null ? -1 : apps.ToList().FindIndex(app =>
            app.Equals(current, StringComparison.OrdinalIgnoreCase));
        _apps.SelectedIndex = index >= 0 ? index : apps.Count > 0 ? 0 : -1;

        var ok = new Button { Text = "Leave out", DialogResult = DialogResult.OK, MinimumSize = new Size(82, 28), AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, MinimumSize = new Size(82, 28) };
        _apps.DoubleClick += (_, _) => { if (_apps.SelectedItem is not null) DialogResult = DialogResult.OK; };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_apps);
        Controls.Add(buttons);
        Controls.Add(intro);
        AcceptButton = ok;
        CancelButton = cancel;
        DarkTheme.ApplyToDialog(this);
        ok.BackColor = AccentDark;
    }
}
