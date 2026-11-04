using Spectari.Util;
using static Spectari.Ui.Palette;
using static Spectari.Ui.StreamOptions;

namespace Spectari.Ui;

/// <summary>What the Settings window can ask the main window to do right away.</summary>
/// <param name="OpenPort">Owner window for prompts, the port in the dialog, and whether to allow LAN viewers.</param>
internal sealed record SettingsActions(Action<IWin32Window, int, bool> OpenPort, Action CopyLog, Action OpenLogs);

/// <summary>
/// The set-and-forget options: stream name, port, viewer access, Tailscale,
/// encoder, update checks, and logs. Edits a copy of the settings; Save returns it.
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly HostAccessService _hostAccess;
    private readonly TextBox _name = new()
    {
        Width = 220, MaxLength = StreamLimits.MaxStreamNameLength, BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = Environment.MachineName,
    };
    private readonly NumericUpDown _port = new()
    {
        Minimum = StreamLimits.MinAppPort, Maximum = StreamLimits.MaxPort, Width = 90,
    };
    private readonly ComboBox _encoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _allowLan = new() { Text = "Allow LAN viewers", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    private readonly Button _openPort = new() { Text = "Open port", AutoSize = true, MinimumSize = new Size(90, 28) };
    private readonly CheckBox _checkUpdates = new() { Text = "Check for updates when Spectari starts", AutoSize = true };
    private readonly Label _tailscaleStatus = new() { Text = "Checking...", AutoSize = true, Margin = new Padding(3, 8, 8, 0) };
    private readonly Button _tailscaleAction = new() { AutoSize = true, MinimumSize = new Size(90, 28), Visible = false };
    private readonly Button _tailscaleRecheck = new() { Text = "Check again", AutoSize = true, MinimumSize = new Size(90, 28) };
    private readonly Label _tailscaleSteps = new()
    {
        AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Dim, Margin = new Padding(3, 0, 3, 6), Visible = false,
        Text = "On the Tailscale page, find this PC, open its menu, choose Share, and send the invite link " +
            "to your viewer. They sign in with their own free Tailscale account.",
    };
    private Action _tailscaleNextStep = () => { };
    private readonly TableLayoutPanel _grid = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(4),
    };
    private int _row;
    private bool _accessBusy;

    /// <summary>Shows the window. Returns the edited settings, or null when canceled.</summary>
    internal static AppSettings? Edit(IWin32Window owner, AppSettings current, IReadOnlyList<EncoderChoice> encoders,
        HostAccessService hostAccess, SettingsActions actions, bool live)
    {
        using var dialog = new SettingsDialog(current, encoders, hostAccess, actions, live);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result() : null;
    }

    private SettingsDialog(AppSettings current, IReadOnlyList<EncoderChoice> encoders,
        HostAccessService hostAccess, SettingsActions actions, bool live)
    {
        _settings = AppSettingsStore.Parse(AppSettingsStore.Serialize(current));
        _hostAccess = hostAccess;

        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _name.Text = _settings.StreamName;
        _port.Value = _settings.Port;
        _encoder.Items.AddRange(encoders.ToArray<object>());
        int encoderIndex = encoders.ToList().FindIndex(e => e.Value == _settings.Encoder);
        _encoder.SelectedIndex = encoderIndex >= 0 ? encoderIndex : 0;
        _allowLan.Checked = hostAccess.PersistedState.AllowLan;
        _checkUpdates.Checked = _settings.CheckForUpdatesOnStartup;

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow("Stream name:", _name);
        AddRow("Port:", _port);
        AddRow("Encoder:", _encoder);
        AddRow("Viewer access:", Flow(_openPort, _allowLan));
        AddNote("Open port asks for administrator approval once and lets Tailscale viewers reach this port, plus LAN viewers if allowed. It also saves the port right away.");
        AddRow("Tailscale:", Flow(_tailscaleStatus, _tailscaleAction, _tailscaleRecheck));
        _grid.Controls.Add(_tailscaleSteps, 1, _row++);
        AddRow("Updates:", _checkUpdates);
        var openLogs = new Button { Text = "Open logs", AutoSize = true, MinimumSize = new Size(90, 28) };
        var copyLog = new Button { Text = "Copy log", AutoSize = true, MinimumSize = new Size(90, 28) };
        AddRow("Logs:", Flow(openLogs, copyLog));
        if (live) AddNote("Port changes apply to the next stream. Name and encoder changes apply at the next start or Switch source.");

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        _grid.Controls.Add(buttons, 0, _row);
        _grid.SetColumnSpan(buttons, 2);

        Controls.Add(_grid);
        AcceptButton = save;
        CancelButton = cancel;
        DarkTheme.ApplyToDialog(this);
        save.BackColor = AccentDark;

        _openPort.Click += (_, _) =>
        {
            if (!_accessBusy) actions.OpenPort(this, (int)_port.Value, _allowLan.Checked);
        };
        openLogs.Click += (_, _) => actions.OpenLogs();
        _tailscaleAction.Click += (_, _) => _tailscaleNextStep();
        _tailscaleRecheck.Click += async (_, _) => await RefreshTailscaleAsync();
        Shown += async (_, _) => await RefreshTailscaleAsync();
        copyLog.Click += (_, _) => actions.CopyLog();
        _hostAccess.SetupStarted += OnSetupStarted;
        _hostAccess.SetupCompleted += OnSetupCompleted;
        FormClosed += (_, _) =>
        {
            _hostAccess.SetupStarted -= OnSetupStarted;
            _hostAccess.SetupCompleted -= OnSetupCompleted;
        };
    }

    private async Task RefreshTailscaleAsync()
    {
        _tailscaleStatus.Text = "Checking...";
        TailscaleState state = await Task.Run(TailscaleStatus.Read);
        if (IsDisposed) return;

        (string status, string action, Action next) = state.Stage switch
        {
            TailscaleStage.NotInstalled => ("Not installed.", "Download Tailscale",
                (Action)(() => Browser.Open(TailscaleStatus.DownloadPage))),
            TailscaleStage.NotRunning => ("Installed, but not running.", "Open Tailscale", TailscaleStatus.OpenApp),
            TailscaleStage.SignedOut => ("Signed out.", "Sign in", TailscaleStatus.OpenApp),
            TailscaleStage.Disconnected => ("Signed in, but disconnected.", "Connect", TailscaleStatus.OpenApp),
            _ => ($"Connected as {state.MachineName ?? "this PC"}.", "Share with a viewer",
                (Action)(() => Browser.Open(TailscaleStatus.MachinesPage))),
        };
        _tailscaleStatus.Text = status;
        _tailscaleAction.Text = action;
        _tailscaleAction.Visible = true;
        _tailscaleNextStep = next;
        _tailscaleSteps.Visible = state.Stage == TailscaleStage.Running;
    }

    private AppSettings Result()
    {
        _settings.StreamName = StreamLimits.CleanStreamName(_name.Text);
        _settings.Port = (int)_port.Value;
        _settings.Encoder = (_encoder.SelectedItem as EncoderChoice)?.Value ?? "auto";
        _settings.CheckForUpdatesOnStartup = _checkUpdates.Checked;
        return _settings;
    }

    // Controls are never disabled (dark disabled text is unreadable); while the
    // elevated setup helper runs, the button says so and ignores clicks.
    private void OnSetupStarted(HostAccessSetupRequest _) => SetAccessBusy(true);
    private void OnSetupCompleted(HostAccessSetupResult _) => SetAccessBusy(false);

    private void SetAccessBusy(bool busy)
    {
        if (IsDisposed) return;
        _accessBusy = busy;
        _openPort.Text = busy ? "Opening..." : "Open port";
    }

    private void AddRow(string label, Control control)
    {
        _grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 8, 0), ForeColor = Dim }, 0, _row);
        _grid.Controls.Add(control, 1, _row++);
    }

    private void AddNote(string text)
    {
        var note = new Label { Text = text, AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Dim, Margin = new Padding(3, 0, 3, 6) };
        _grid.Controls.Add(note, 1, _row++);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        flow.Controls.AddRange(controls);
        return flow;
    }
}
