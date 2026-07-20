namespace Spectari.Capture;

internal readonly record struct WindowIdentity(IntPtr Handle, uint Pid);

/// <summary>Selects replacement windows without observing or controlling the desktop.</summary>
internal sealed class WindowReattachPolicy
{
    private readonly string _applicationName;
    private readonly HashSet<WindowIdentity> _windowsPresentAtLoss;

    internal WindowReattachPolicy(
        string applicationName,
        WindowIdentity lostWindow,
        IEnumerable<WindowDescription> windowsPresentAtLoss)
    {
        _applicationName = applicationName;
        _windowsPresentAtLoss = windowsPresentAtLoss
            .Where(IsSameApplication)
            .Select(IdentityOf)
            .ToHashSet();
        _windowsPresentAtLoss.Add(lostWindow);
    }

    internal bool IsEligible(WindowDescription candidate) =>
        HasKnownApplicationIdentity
        && IsSameApplication(candidate)
        && SourceSelectionModel.HasCapturableSurface(candidate)
        && !_windowsPresentAtLoss.Contains(IdentityOf(candidate));

    internal WindowDescription? SelectCandidate(IEnumerable<WindowDescription> windows) =>
        windows
            .Where(IsEligible)
            .OrderByDescending(window => (long)window.Width * window.Height)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Pid)
            .ThenBy(window => unchecked((ulong)window.Handle.ToInt64()))
            .FirstOrDefault();

    private bool HasKnownApplicationIdentity =>
        !string.IsNullOrWhiteSpace(_applicationName) && _applicationName != "?";

    private bool IsSameApplication(WindowDescription window) =>
        window.ProcessName.Equals(_applicationName, StringComparison.OrdinalIgnoreCase);

    internal static WindowIdentity IdentityOf(WindowDescription window) =>
        new(window.Handle, window.Pid);
}
