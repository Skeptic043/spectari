using System.ComponentModel;
using Spectari.Util;
using static Spectari.Ui.Palette;

namespace Spectari.Ui;

/// <summary>
/// Slim update-available bar drawn over the top of the window, just below the
/// menu bar. It is never docked, so showing or hiding it does not move any
/// other control.
/// </summary>
internal sealed class UpdateBanner : Panel
{
    private const int BarHeight = 36;

    private readonly UpdateFlow _updateFlow;
    private readonly Control _above;
    private readonly CancellationTokenSource _checkCts = new();
    private readonly Label _message = new() { AutoSize = true, ForeColor = Fg, Anchor = AnchorStyles.Left };
    private IReadOnlyList<GitHubRelease>? _newer;
    private string? _skippedVersion;

    /// <summary>Raised after the user dismisses the bar, so the dismissal can be saved.</summary>
    internal event Action? Dismissed;

    /// <param name="above">The control the bar sits directly below; it is never covered.</param>
    internal UpdateBanner(UpdateFlow updateFlow, Control above)
    {
        _updateFlow = updateFlow;
        _above = above;
        Visible = false;
        Height = BarHeight;
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Border;
        Padding = new Padding(0, 0, 0, 1);
        AccessibleRole = AccessibleRole.Alert;
        AccessibleName = "Update available";

        var viewRelease = MakeButton("Open on GitHub", Card);
        var updateNow = MakeButton("Update now", AccentDark);
        var dismiss = MakeButton("Dismiss", Card);
        viewRelease.Click += (_, _) => UpdateFlow.OpenReleasePage();
        updateNow.Click += async (_, _) => await InstallAsync();
        dismiss.Click += (_, _) => Dismiss();

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Card, ColumnCount = 4, RowCount = 1,
            Padding = new Padding(12, 3, 8, 2),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_message, 0, 0);
        row.Controls.Add(viewRelease, 1, 0);
        row.Controls.Add(updateNow, 2, 0);
        row.Controls.Add(dismiss, 3, 0);
        Controls.Add(row);

        _above.SizeChanged += (_, _) => Place();
        _above.LocationChanged += (_, _) => Place();
    }

    /// <summary>The dismissed version in canonical form, or null when none is dismissed.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string? SkippedVersion
    {
        get => _skippedVersion;
        set => _skippedVersion = UpdateChecker.CanonicalVersion(value);
    }

    /// <summary>Checks once for a newer release and shows the bar when it has not been dismissed.</summary>
    internal async Task CheckAsync()
    {
        try
        {
            CancellationToken token = _checkCts.Token;
            IReadOnlyList<GitHubRelease>? releases = await UpdateChecker.GetReleasesAsync(token);
            if (releases is null || token.IsCancellationRequested || IsDisposed || Disposing) return;
            IReadOnlyList<GitHubRelease> newer = UpdateChecker.NewerThan(releases, AppVersion.Current);
            if (newer.Count == 0 || !UpdateChecker.ShouldOffer(AppVersion.Current, newer[0].Tag, _skippedVersion))
                return;

            _newer = newer;
            _message.Text = $"Spectari {UpdateChecker.DisplayVersion(newer[0].Tag)} is available.";
            Place();
            BringToFront();
            Visible = true;
        }
        catch
        {
            // Update discovery must never interrupt the app or surface a failure.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checkCts.Cancel();
            _checkCts.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Place()
    {
        if (Parent is null) return;
        SetBounds(0, _above.Bottom, Parent.ClientSize.Width, BarHeight);
    }

    private async Task InstallAsync()
    {
        if (_newer is not { } newer) return;
        try
        {
            await _updateFlow.InstallAsync(newer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] install from the update bar failed: {ex}");
        }
    }

    private void Dismiss()
    {
        if (_newer is null) return;
        SkippedVersion = _newer[0].Tag;
        Visible = false;
        Dismissed?.Invoke();
    }

    private static Button MakeButton(string text, Color back)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, Height = 26, MinimumSize = new Size(70, 26),
            Anchor = AnchorStyles.Right, Margin = new Padding(4, 0, 0, 0),
            FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Fg,
        };
        button.FlatAppearance.BorderColor = Border;
        return button;
    }
}
