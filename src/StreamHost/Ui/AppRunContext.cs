namespace StreamHost.Ui;

/// <summary>
/// Owns the process lifetime and the single-instance ping, so the control panel
/// can always be brought back. A hidden top-level window catches
/// SingleInstance.ShowMessage (a message-only window would not: HWND_BROADCAST
/// skips those) and recreates the panel if it was closed while a Watch window
/// kept the process alive. The app exits when its last user window closes.
/// </summary>
internal sealed class AppRunContext : ApplicationContext
{
    /// <summary>The one live context; the crash logger reads session state through it.</summary>
    public static AppRunContext? Current { get; private set; }

    private readonly PingWindow _ping;
    private MainForm? _main;
    private int _liveWindows;

    public AppRunContext()
    {
        Current = this;
        _ping = new PingWindow(Surface);
        ShowMain();
    }

    /// <summary>Count a user window toward the app's lifetime: when the last one
    /// closes, the message loop ends. Called for the control panel and for every
    /// Watch window it opens.</summary>
    public void Track(Form window)
    {
        _liveWindows++;
        window.FormClosed += (_, _) => { if (--_liveWindows <= 0) ExitThread(); };
    }

    /// <summary>One-line session summary for the crash log, guarded so reading it
    /// while the app is falling over can't throw a second exception.</summary>
    public string DescribeState()
    {
        try
        {
            return _main is { IsDisposed: false } m ? $"session: {m.DescribeSessionState()}" : "control panel closed";
        }
        catch (Exception ex) { return $"state unavailable ({ex.Message})"; }
    }

    private void ShowMain()
    {
        _main = new MainForm();
        Track(_main);
        _main.Show();
    }

    /// <summary>Answer a single-instance ping: surface the control panel, recreating
    /// it if it was closed while a Watch window stayed open.</summary>
    private void Surface()
    {
        if (_main is { IsDisposed: false } m) m.ShowAndActivate();
        else ShowMain();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ping.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Hidden top-level window whose only job is to receive the broadcast
    /// ShowMessage. Top-level, not message-only: HWND_BROADCAST skips message-only
    /// windows. Its WndProc runs on the UI thread, so surfacing is marshal-free.</summary>
    private sealed class PingWindow : NativeWindow, IDisposable
    {
        private readonly Action _onPing;

        public PingWindow(Action onPing)
        {
            _onPing = onPing;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Util.SingleInstance.ShowMessage) _onPing();
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
