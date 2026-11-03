namespace Spectari.Ui;

/// <summary>
/// Modal progress window that runs one async operation on the UI thread and
/// closes itself when the operation ends. Closing early only requests
/// cancellation; the window stays until the operation has cleaned up.
/// </summary>
internal sealed class UpdateProgressDialog : Form
{
    private readonly Func<UpdateProgressDialog, CancellationToken, Task> _work;
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 28 };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 82, Height = 28 };
    private bool _finished;
    private bool _cancelable = true;

    internal Exception? Failure { get; private set; }

    internal UpdateProgressDialog(string title, Func<UpdateProgressDialog, CancellationToken, Task> work)
    {
        _work = work;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 116);
        Padding = new Padding(14, 12, 14, 12);
        BackColor = Palette.Bg;
        ForeColor = Palette.Fg;

        _cancel.FlatStyle = FlatStyle.Flat;
        _cancel.FlatAppearance.BorderColor = Palette.Border;
        _cancel.BackColor = Palette.Card;
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancel.Location = new Point(ClientSize.Width - Padding.Right - _cancel.Width,
            ClientSize.Height - Padding.Bottom - _cancel.Height);
        _cancel.Click += (_, _) => RequestCancel();

        Controls.Add(_cancel);
        Controls.Add(_bar);
        Controls.Add(_status);
    }

    internal bool WasCanceled => _cts.IsCancellationRequested;

    /// <summary>Shows a step. A null fraction shows an indeterminate bar.</summary>
    internal void SetStatus(string text, double? fraction = null)
    {
        if (IsDisposed) return;
        _status.Text = text;
        if (fraction is { } value)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Clamp(value * _bar.Maximum, 0, _bar.Maximum);
        }
        else
        {
            _bar.Style = ProgressBarStyle.Marquee;
        }
    }

    /// <summary>Once a step must not be interrupted, cancel is removed.</summary>
    internal void SetCancelable(bool cancelable)
    {
        _cancelable = cancelable;
        _cancel.Visible = cancelable;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await _work(this, _cts.Token);
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            _finished = true;
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }

    private void RequestCancel()
    {
        if (!_cancelable || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        _status.Text = "Canceling...";
    }
}
