using Spectari.Capture;
using static Spectari.Ui.Palette;
using static Spectari.Ui.SourcePickers;
using static Spectari.Ui.StreamOptions;

namespace Spectari.Ui;

/// <summary>The quality picks made in the Switch source dialog, as indexes into the main window's lists.</summary>
internal sealed record SwitchSourceChoice(int PresetIndex, int EncoderIndex, string? BitrateTier);

/// <summary>
/// Guided switch: a small popup with the source, preset, bitrate, encoder, and
/// audio pickers, prefilled from the current selections. Source and audio picks
/// are made on the snapshot model it is given; the quality picks come back as a
/// <see cref="SwitchSourceChoice"/>.
/// </summary>
internal sealed class SwitchSourceDialog : Form
{
    private const int ExpandedHeight = 306;

    private readonly SourceSelectionModel _sources;
    private readonly CaptureDeviceChangeMonitor _captureDeviceChanges;
    private readonly string _fallbackBitrateTier;
    private readonly TableLayoutPanel _grid;
    private readonly int _captureDeviceRow;
    private readonly RadioButton _rbWin = new() { Text = "Game / window", AutoSize = true };
    private readonly RadioButton _rbMon = new() { Text = "Monitor", AutoSize = true };
    private readonly RadioButton _rbCaptureDevice = new() { Text = "Capture device", AutoSize = true };
    private readonly ComboBox _winCombo = NewCombo(330);
    private readonly ComboBox _monCombo = NewCombo(330);
    private readonly ComboBox _captureDeviceCombo = NewCombo(330);
    private readonly ComboBox _presetCombo = NewCombo(300);
    private readonly ComboBox _bitrateCombo = NewCombo(200);
    private readonly ComboBox _encoderCombo = NewCombo(200);
    private readonly ComboBox _audioCombo = NewCombo(330);
    private readonly TaskCompletionSource<DialogResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _refreshingSourceOptions;

    /// <param name="sources">A fresh snapshot; the dialog's picks are written to it.</param>
    /// <param name="presetItems">The main preset list, so Native labels and indexes match.</param>
    /// <param name="encoderItems">The main encoder list, so indexes match.</param>
    internal SwitchSourceDialog(
        SourceSelectionModel sources,
        object[] presetItems, int presetIndex,
        object[] encoderItems, int encoderIndex,
        string? bitrateTier, bool live,
        CaptureDeviceChangeMonitor captureDeviceChanges)
    {
        _sources = sources;
        _captureDeviceChanges = captureDeviceChanges;
        _fallbackBitrateTier = bitrateTier ?? "med";

        Text = "Switch source";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, ExpandedHeight);
        BackColor = Bg;
        ForeColor = Fg;
        HandleCreated += (_, _) => DarkTheme.UseDarkTitleBar(this);

        bool showCaptureDeviceRow = sources.CaptureDevices.Count > 0;
        _rbWin.Checked = sources.Kind == SourceKind.Window;
        _rbMon.Checked = sources.Kind == SourceKind.Monitor;
        _rbCaptureDevice.Checked = showCaptureDeviceRow && sources.Kind == SourceKind.CaptureDevice;
        _rbCaptureDevice.Visible = showCaptureDeviceRow;
        _captureDeviceCombo.Visible = showCaptureDeviceRow;

        RenderSourcePickers(sources, _winCombo, _monCombo, _captureDeviceCombo, _audioCombo);
        _presetCombo.Items.AddRange(presetItems);
        _encoderCombo.Items.AddRange(encoderItems);
        _presetCombo.SelectedIndex = presetIndex;
        _encoderCombo.SelectedIndex = encoderIndex;
        WirePickers();
        RefreshSourceOptions();

        _grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Padding = new Padding(10),
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int index = 0; index < 7; index++)
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        int row = 0;
        _grid.Controls.Add(_rbWin, 0, row);
        _grid.Controls.Add(_winCombo, 1, row++);
        _grid.Controls.Add(_rbMon, 0, row);
        _grid.Controls.Add(_monCombo, 1, row++);
        _captureDeviceRow = row;
        _grid.Controls.Add(_rbCaptureDevice, 0, row);
        _grid.Controls.Add(_captureDeviceCombo, 1, row++);
        AddLabeledRow("Audio:", _audioCombo, row++);
        AddLabeledRow("Preset:", _presetCombo, row++);
        AddLabeledRow("Bitrate:", _bitrateCombo, row++);
        AddLabeledRow("Encoder:", _encoderCombo, row++);

        var ok = new Button { Text = live ? "Switch" : "Start", Width = 96, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        _grid.SetColumnSpan(buttons, 2);
        _grid.Controls.Add(buttons, 0, row);
        Controls.Add(_grid);
        AcceptButton = ok;
        CancelButton = cancel;
        DarkTheme.Apply(this);
        ok.BackColor = AccentDark;

        RenderCaptureDeviceRow();

        ok.Click += (_, _) => Complete(DialogResult.OK);
        cancel.Click += (_, _) => Complete(DialogResult.Cancel);
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Complete(DialogResult.Cancel);
        };
    }

    /// <summary>
    /// Shows the dialog modelessly over a disabled owner, so capture device
    /// notifications keep reaching it. Null means the user canceled.
    /// </summary>
    internal async Task<SwitchSourceChoice?> ShowAsync(Form owner)
    {
        DialogResult result;
        _captureDeviceChanges.DevicesChanged += RefreshCaptureDevices;
        try
        {
            owner.Enabled = false;
            Show(owner);
            result = await _completion.Task;
        }
        finally
        {
            _captureDeviceChanges.DevicesChanged -= RefreshCaptureDevices;
            owner.Enabled = true;
        }
        if (result != DialogResult.OK) return null;
        return new SwitchSourceChoice(
            _presetCombo.SelectedIndex,
            _encoderCombo.SelectedIndex,
            (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier);
    }

    private static ComboBox NewCombo(int width) =>
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, FlatStyle = FlatStyle.Flat };

    private void AddLabeledRow(string label, Control control, int row)
    {
        _grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, row);
        _grid.Controls.Add(control, 1, row);
    }

    private void WirePickers()
    {
        _rbWin.CheckedChanged += (_, _) => SelectKind(SourceKind.Window, _rbWin);
        _rbMon.CheckedChanged += (_, _) => SelectKind(SourceKind.Monitor, _rbMon);
        _rbCaptureDevice.CheckedChanged += (_, _) => SelectKind(SourceKind.CaptureDevice, _rbCaptureDevice);
        _winCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _winCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (_sources.SelectWindowIndex(_winCombo.SelectedIndex))
                RefreshSourceOptions();
        };
        _monCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _monCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (_sources.SelectMonitorIndex(_monCombo.SelectedIndex))
                RefreshSourceOptions();
        };
        _captureDeviceCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _captureDeviceCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (_sources.SelectCaptureDeviceIndex(_captureDeviceCombo.SelectedIndex))
            {
                RenderAudioPicker(_sources, _audioCombo);
                RefreshSourceOptions();
            }
        };
        _audioCombo.DropDown += (_, _) =>
        {
            _sources.RefreshAudioApps();
            RenderAudioPicker(_sources, _audioCombo);
        };
        _audioCombo.SelectionChangeCommitted += (_, _) =>
            _sources.SelectAudioIndex(_audioCombo.SelectedIndex);
        _presetCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
    }

    private void SelectKind(SourceKind kind, RadioButton radio)
    {
        if (!radio.Checked) return;
        _sources.SelectKind(kind);
        RenderAudioPicker(_sources, _audioCombo);
        RefreshSourceOptions();
    }

    // Refreshes the Native labels and bitrate choices from one immutable
    // enumerated size snapshot, matching the main controls.
    private void RefreshSourceOptions()
    {
        if (_refreshingSourceOptions) return;
        _refreshingSourceOptions = true;
        try
        {
            var sourceSize = _sources.SelectedSourceSize;
            for (int idx = 0; idx < Presets.Length && idx < _presetCombo.Items.Count; idx++)
            {
                if (Presets[idx].Height != 0) continue;
                string label = ComputeNativeLabel(Presets[idx].Fps, sourceSize);
                if (_presetCombo.Items[idx] is Preset current && current.Label == label) continue;
                int sel = _presetCombo.SelectedIndex;
                _presetCombo.Items[idx] = Presets[idx] with { Label = label };
                _presetCombo.SelectedIndex = sel;
            }

            if (_presetCombo.SelectedItem is not Preset p) return;
            string keep = (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier ?? _fallbackBitrateTier;
            var choices = BuildBitrateChoices(p, sourceSize);
            _bitrateCombo.BeginUpdate();
            _bitrateCombo.Items.Clear();
            foreach (var c in choices) _bitrateCombo.Items.Add(c);
            _bitrateCombo.EndUpdate();
            int i = choices.FindIndex(c => c.Tier == keep);
            _bitrateCombo.SelectedIndex = i >= 0 ? i : 1;
        }
        finally
        {
            _refreshingSourceOptions = false;
        }
    }

    private void RenderCaptureDeviceRow()
    {
        bool visible = _sources.CaptureDevices.Count > 0;
        _rbWin.Checked = _sources.Kind == SourceKind.Window;
        _rbMon.Checked = _sources.Kind == SourceKind.Monitor;
        _rbCaptureDevice.Checked = visible && _sources.Kind == SourceKind.CaptureDevice;
        _grid.SuspendLayout();
        _rbCaptureDevice.Visible = visible;
        _captureDeviceCombo.Visible = visible;
        RowStyle rowStyle = _grid.RowStyles[_captureDeviceRow];
        rowStyle.SizeType = visible ? SizeType.AutoSize : SizeType.Absolute;
        rowStyle.Height = 0;
        ClientSize = new Size(480, ExpandedHeight - (visible ? 0 : PickerRowHeight(_captureDeviceCombo)));
        _grid.ResumeLayout(performLayout: true);
    }

    private void RefreshCaptureDevices(IReadOnlyList<CaptureDeviceDescription> devices)
    {
        if (IsDisposed || Disposing) return;
        _sources.RefreshCaptureDevices(devices);
        RenderPicker(_captureDeviceCombo, _sources.CaptureDeviceDisplayItems, _sources.SelectedCaptureDeviceIndex);
        RenderCaptureDeviceRow();
        RefreshSourceOptions();
    }

    private void Complete(DialogResult result)
    {
        if (!_completion.TrySetResult(result)) return;
        Hide();
    }
}
