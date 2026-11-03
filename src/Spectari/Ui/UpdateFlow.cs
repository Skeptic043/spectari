using Spectari.Util;

namespace Spectari.Ui;

/// <summary>
/// User-facing update actions: the on-demand check and the install action.
/// InstallAsync is the one install entry point for every surface that offers
/// an update.
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
            LatestRelease? release;
            _owner.UseWaitCursor = true;
            try
            {
                release = await UpdateChecker.GetLatestReleaseAsync(CancellationToken.None);
            }
            finally
            {
                _owner.UseWaitCursor = false;
            }
            if (_owner.IsDisposed) return;

            string current = UpdateChecker.DisplayVersion(AppVersion.Current) ?? AppVersion.Current;
            if (release is null)
            {
                Inform("Could not check for updates. Check your internet connection and try again.",
                    MessageBoxIcon.Warning);
                return;
            }
            if (!UpdateChecker.IsRemoteVersionNewer(AppVersion.Current, release.Tag))
            {
                Inform($"Spectari is up to date ({current}).", MessageBoxIcon.Information);
                return;
            }

            string available = UpdateChecker.DisplayVersion(release.Tag) ?? release.Tag;
            DialogResult answer = MessageBox.Show(_owner,
                $"Spectari {available} is available. You have {current}.\n\nInstall it now?",
                Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) await InstallCoreAsync(release);
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

    /// <summary>
    /// Downloads, verifies, and installs the release, then relaunches on it.
    /// A running stream is only closed after the user chooses Continue and the
    /// download has been verified.
    /// </summary>
    internal async Task InstallAsync(LatestRelease release)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await InstallCoreAsync(release);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InstallCoreAsync(LatestRelease release)
    {
        string version = UpdateChecker.DisplayVersion(release.Tag) ?? release.Tag;
        string current = UpdateChecker.DisplayVersion(AppVersion.Current) ?? AppVersion.Current;

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

        if (!_streams.IsIdleSurface && !ConfirmCloseStream()) return;

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

    private bool ConfirmCloseStream()
    {
        var continueButton = new TaskDialogButton("Continue");
        var stopButton = new TaskDialogButton("Stop");
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = "Updating will close your stream.",
            Text = "Continue closes the stream and installs the update. Stop keeps the stream running and does not update.",
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            Buttons = { continueButton, stopButton },
            DefaultButton = stopButton,
        };
        return TaskDialog.ShowDialog(_owner, page) == continueButton;
    }

    private void OpenReleasePage(string message)
    {
        Inform(message, MessageBoxIcon.Information);
        OpenReleasePage();
    }

    /// <summary>Opens the latest release page in the default browser.</summary>
    internal static void OpenReleasePage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateChecker.LatestReleasePageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] could not open the release page: {ex.Message}");
        }
    }

    private void Inform(string message, MessageBoxIcon icon)
    {
        if (_owner.IsDisposed) return;
        MessageBox.Show(_owner, message, Caption, MessageBoxButtons.OK, icon);
    }
}
