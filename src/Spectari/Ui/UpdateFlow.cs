using Spectari.Util;

namespace Spectari.Ui;

/// <summary>
/// User-facing update actions: the on-demand check, the release notes, and the
/// install action. InstallAsync is the one install entry point for every
/// surface that offers an update.
/// </summary>
internal sealed class UpdateFlow
{
    private const string Caption = "Spectari";
    private static readonly TimeSpan StreamStopTimeout = TimeSpan.FromSeconds(20);

    private readonly Form _owner;
    private readonly StreamController _streams;
    private bool _busy;

    internal UpdateFlow(Form owner, StreamController streams)
    {
        _owner = owner;
        _streams = streams;
    }

    /// <summary>Checks now and says whether an update is available, the app is up to date, or the check failed.</summary>
    internal async Task CheckNowAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            IReadOnlyList<GitHubRelease>? releases = await FetchReleasesAsync();
            if (_owner.IsDisposed) return;
            if (releases is null)
            {
                Inform("Could not check for updates. Check your internet connection and try again.",
                    MessageBoxIcon.Warning);
                return;
            }

            IReadOnlyList<GitHubRelease> newer = UpdateChecker.NewerThan(releases, AppVersion.Current);
            if (newer.Count == 0)
            {
                Inform($"Spectari is up to date ({CurrentDisplayVersion}).", MessageBoxIcon.Information);
                return;
            }
            await InstallCoreAsync(newer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] check for updates failed: {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Shows every release's notes.</summary>
    internal async Task ShowReleaseNotesAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            IReadOnlyList<GitHubRelease>? releases = await FetchReleasesAsync();
            if (_owner.IsDisposed) return;
            if (releases is null)
            {
                Inform("Could not load the release notes. Check your internet connection and try again.",
                    MessageBoxIcon.Warning);
                return;
            }
            ReleaseNotesDialog.ShowAll(_owner, releases);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] showing release notes failed: {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Shows the notes for the missing versions, then downloads, verifies, and
    /// installs the newest one and relaunches on it. A running stream is only
    /// closed after the user confirms and the download has been verified.
    /// </summary>
    /// <param name="newer">Releases newer than the running build, newest first.</param>
    internal async Task InstallAsync(IReadOnlyList<GitHubRelease> newer)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await InstallCoreAsync(newer);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InstallCoreAsync(IReadOnlyList<GitHubRelease> newer)
    {
        GitHubRelease release = newer[0];
        string version = UpdateChecker.DisplayVersion(release.Tag) ?? release.Tag;
        string current = CurrentDisplayVersion;

        if (release.Package is not { } package)
        {
            OpenReleasePage($"Spectari {version} cannot be installed from inside the app. " +
                "The release page will open so you can download it.");
            return;
        }

        string? installDir = UpdateInstaller.CurrentInstallDirectory();
        var installer = installDir is null ? null : new UpdateInstaller(installDir);
        if (installer is null || !installer.CanWriteInstallFolder())
        {
            OpenReleasePage("Spectari cannot update itself in this folder. " +
                $"The release page will open so you can download {version}.");
            return;
        }

        if (!ReleaseNotesDialog.ConfirmInstall(_owner, newer, current, streaming: !_streams.IsIdleSurface)) return;

        using var dialog = new UpdateProgressDialog($"Updating to Spectari {version}", async (progressDialog, ct) =>
        {
            progressDialog.SetStatus("Downloading...", 0);
            var progress = new Progress<double>(fraction =>
                progressDialog.SetStatus($"Downloading... {(int)(fraction * 100)}%", fraction));
            await installer.StageAsync(package, release.Version, progress, ct);

            progressDialog.SetCancelable(false);
            if (ct.IsCancellationRequested)
            {
                installer.DiscardWork();
                throw new OperationCanceledException(ct);
            }
            if (!_streams.IsIdleSurface)
            {
                progressDialog.SetStatus("Closing the stream...");
                if (!await _streams.StopAndWaitAsync(StreamStopTimeout))
                {
                    installer.DiscardWork();
                    throw new UpdateException("The stream did not close, so the update was not installed.");
                }
            }

            progressDialog.SetStatus("Installing...");
            installer.ApplyStaged();
        });
        dialog.ShowDialog(_owner);

        switch (dialog.Failure)
        {
            case null:
                break;
            case OperationCanceledException:
                Inform($"Update canceled. Spectari {current} is still installed.", MessageBoxIcon.Information);
                return;
            case UpdateException failure:
                Console.Error.WriteLine($"[update] install of {release.Tag} failed: {failure.Message} {failure.InnerException?.Message}");
                Inform(failure.InstallUnchanged
                        ? $"The update did not install. {failure.Message}\n\nSpectari {current} is still installed and working."
                        : $"The update did not install. {failure.Message}",
                    MessageBoxIcon.Warning);
                return;
            default:
                Console.Error.WriteLine($"[update] install of {release.Tag} failed: {dialog.Failure}");
                Inform($"The update did not install. Spectari {current} is still installed.", MessageBoxIcon.Warning);
                return;
        }

        Console.WriteLine($"[update] installed {release.Tag}; relaunching.");
        SingleInstance.Release();
        try
        {
            installer.LaunchInstalled();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] relaunch failed: {ex.Message}");
            SingleInstance.TryAcquire();
            Inform($"Spectari {version} is installed. Close Spectari and open it again to use it.",
                MessageBoxIcon.Information);
            return;
        }
        Application.Exit();
    }

    private void OpenReleasePage(string message)
    {
        Inform(message, MessageBoxIcon.Information);
        OpenReleasePage();
    }

    /// <summary>Opens the latest release page in the default browser.</summary>
    internal static void OpenReleasePage() => Browser.Open(ProjectLinks.LatestReleasePage);

    private static string CurrentDisplayVersion =>
        UpdateChecker.DisplayVersion(AppVersion.Current) ?? AppVersion.Current;

    private async Task<IReadOnlyList<GitHubRelease>?> FetchReleasesAsync()
    {
        _owner.UseWaitCursor = true;
        try
        {
            return await UpdateChecker.GetReleasesAsync(CancellationToken.None);
        }
        finally
        {
            _owner.UseWaitCursor = false;
        }
    }

    private void Inform(string message, MessageBoxIcon icon)
    {
        if (_owner.IsDisposed) return;
        MessageBox.Show(_owner, message, Caption, MessageBoxButtons.OK, icon);
    }
}
