using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Spectari.Util;

/// <summary>A failed update step. Message is plain text for the user.</summary>
internal sealed class UpdateException(string message, Exception? inner = null, bool installUnchanged = true)
    : Exception(message, inner)
{
    /// <summary>False only when a rollback could not fully restore the previous files.</summary>
    internal bool InstallUnchanged { get; } = installUnchanged;
}

/// <summary>
/// Downloads, verifies, and swaps a release package into the install folder.
/// All work happens in a folder inside the install folder so every move is a
/// same-volume rename. Old items are kept in WorkFolder\old until the next start.
/// </summary>
internal sealed class UpdateInstaller
{
    internal const string WorkFolderName = ".spectari-update";
    internal const string ExeName = "Spectari.exe";
    private const string CommitMarkerName = "committed";
    private const string WaitForProcessVariable = "SPECTARI_UPDATED_FROM_PID";
    private static readonly TimeSpan PreviousProcessExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(30);
    private const string CleanupInProgressMessage =
        "A previous update is still being cleaned up. Try again in a minute.";

    // The startup cleanup retry and a user-started update share one work folder
    // and must never interleave. Update steps on the UI thread fail fast instead
    // of waiting on a cleanup retry.
    private static readonly SemaphoreSlim WorkGate = new(1, 1);
    private static volatile bool _updateStarted;
    private static readonly HttpClient Client = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private readonly string _installDir;

    internal UpdateInstaller(string installDir) => _installDir = Path.GetFullPath(installDir);

    internal string InstallDirectory => _installDir;
    internal string ExePath => Path.Combine(_installDir, ExeName);
    private string WorkDir => Path.Combine(_installDir, WorkFolderName);
    private string StagedDir => Path.Combine(WorkDir, "new");
    private string BackupDir => Path.Combine(WorkDir, "old");

    /// <summary>The folder holding the running Spectari.exe, or null when not running from one.</summary>
    internal static string? CurrentInstallDirectory()
    {
        string? process = Environment.ProcessPath;
        if (process is null
            || !string.Equals(Path.GetFileName(process), ExeName, StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetDirectoryName(process);
    }

    internal bool CanWriteInstallFolder()
    {
        string probe = Path.Combine(_installDir, $"{WorkFolderName}-{Guid.NewGuid():N}.probe");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads the package, checks its size and SHA-256 against the release
    /// metadata, extracts it, and checks the extracted exe reports the expected
    /// version. The install folder itself is not touched. Any failure removes
    /// the work folder and throws UpdateException.
    /// </summary>
    internal async Task StageAsync(
        ReleasePackage package, Version expectedVersion, IProgress<double>? progress, CancellationToken ct)
    {
        if (!await WorkGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            throw new UpdateException(CleanupInProgressMessage);
        try
        {
            _updateStarted = true;
            try
            {
                RecoverOrCleanUpCore();
            }
            catch (Exception ex)
            {
                throw new UpdateException(CleanupInProgressMessage, ex);
            }

            await StageCoreAsync(package, expectedVersion, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            WorkGate.Release();
        }
    }

    private async Task StageCoreAsync(
        ReleasePackage package, Version expectedVersion, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(WorkDir);
            string zipPath = Path.Combine(WorkDir, "package.zip");
            await DownloadAsync(package, zipPath, progress, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            await using (FileStream zip = File.OpenRead(zipPath))
            {
                if (!await MatchesSha256Async(zip, package.Sha256, ct).ConfigureAwait(false))
                    throw new UpdateException("The download did not match the published checksum.");
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                ZipFile.ExtractToDirectory(zipPath, StagedDir);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new UpdateException("The update could not be unpacked.", ex);
            }
            File.Delete(zipPath);
            ValidateStagedPackage(StagedDir, expectedVersion);
        }
        catch (OperationCanceledException)
        {
            DiscardWorkCore();
            throw;
        }
        catch (UpdateException)
        {
            DiscardWorkCore();
            throw;
        }
        catch (Exception ex)
        {
            DiscardWorkCore();
            throw new UpdateException("The update could not be downloaded.", ex);
        }
    }

    /// <summary>
    /// Moves every staged top-level item into the install folder, keeping the
    /// replaced items in the backup folder. Spectari.exe moves last, so the
    /// folder never pairs a new exe with old files. Any failure rolls back.
    /// </summary>
    internal void ApplyStaged()
    {
        if (!WorkGate.Wait(0)) throw new UpdateException(CleanupInProgressMessage);
        try
        {
            ApplyStagedCore();
        }
        finally
        {
            WorkGate.Release();
        }
    }

    private void ApplyStagedCore()
    {
        if (!Directory.Exists(StagedDir))
            throw new UpdateException("The update was not ready to install.");

        var applied = new List<(string Name, bool HadOld, bool Placed)>();
        try
        {
            Directory.CreateDirectory(BackupDir);
            foreach (string name in StagedEntriesExeLast(StagedDir))
            {
                string target = Path.Combine(_installDir, name);
                bool hadOld = EntryExists(target);
                if (hadOld && IsUserEditable(name)) continue;
                applied.Add((name, hadOld, false));
                if (hadOld) MoveEntry(target, Path.Combine(BackupDir, name));
                MoveEntry(Path.Combine(StagedDir, name), target);
                applied[^1] = (name, hadOld, true);
            }
        }
        catch (Exception ex)
        {
            bool restored = RollBack(applied);
            if (restored) DiscardWorkCore();
            throw new UpdateException(restored
                ? "Spectari could not replace its files. Close any Watch windows and try again."
                : "The update could not finish. Spectari will restore the previous files the next time it starts.",
                ex, installUnchanged: restored);
        }

        try
        {
            File.WriteAllText(Path.Combine(WorkDir, CommitMarkerName), "");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] could not record the finished update: {ex.Message}");
        }
    }

    /// <summary>Removes the work folder. Best effort; the next start retries.</summary>
    internal void DiscardWork()
    {
        WorkGate.Wait();
        try
        {
            DiscardWorkCore();
        }
        finally
        {
            WorkGate.Release();
        }
    }

    private void DiscardWorkCore()
    {
        try
        {
            if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] could not remove the update work folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Pass for a leftover work folder. A committed swap is only deleted, with the
    /// commit evidence removed last so a partial delete is never mistaken for an
    /// interrupted swap. Otherwise the backed-up items, including a backed-up
    /// Spectari.exe, are put back over any new ones. Throws when files are still
    /// in use; callers retry.
    /// </summary>
    internal void RecoverOrCleanUp()
    {
        WorkGate.Wait();
        try
        {
            RecoverOrCleanUpCore();
        }
        finally
        {
            WorkGate.Release();
        }
    }

    private void RecoverOrCleanUpCore()
    {
        if (!Directory.Exists(WorkDir)) return;

        string marker = Path.Combine(WorkDir, CommitMarkerName);
        // Committed needs an installed exe, so cleanup can never delete the only exe.
        bool committed = File.Exists(ExePath)
            && (File.Exists(marker) || File.Exists(Path.Combine(BackupDir, ExeName)));
        if (!committed && Directory.Exists(BackupDir))
        {
            foreach (string backup in Directory.EnumerateFileSystemEntries(BackupDir))
            {
                string target = Path.Combine(_installDir, Path.GetFileName(backup));
                DeleteEntry(target);
                MoveEntry(backup, target);
            }
            Console.WriteLine("[update] restored files from an interrupted update.");
        }

        if (committed && !File.Exists(marker)) File.WriteAllText(marker, "");
        foreach (string entry in Directory.EnumerateFileSystemEntries(WorkDir))
        {
            if (!string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase)) DeleteEntry(entry);
        }
        Directory.Delete(WorkDir, recursive: true);
    }

    /// <summary>Runs RecoverOrCleanUp for the running install, retrying while the
    /// previous process may still hold its renamed exe open. Retries stop once
    /// the user starts an update, which does its own recovery first.</summary>
    internal static void CleanUpAtStartup()
    {
        string? installDir = CurrentInstallDirectory();
        if (installDir is null) return;
        var installer = new UpdateInstaller(installDir);
        if (!Directory.Exists(installer.WorkDir)) return;
        if (installer.TryRecoverOrCleanUp(out _)) return;

        _ = Task.Run(async () =>
        {
            const int attempts = 10;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (installer.TryRecoverOrCleanUp(out Exception? failure)) return;
                if (attempt == attempts)
                    Console.Error.WriteLine($"[update] could not clean up the update work folder: {failure?.Message}");
            }
        });
    }

    private bool TryRecoverOrCleanUp(out Exception? failure)
    {
        failure = null;
        if (!WorkGate.Wait(0)) return false;
        try
        {
            if (!_updateStarted) RecoverOrCleanUpCore();
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
        finally
        {
            WorkGate.Release();
        }
    }

    // start-stream.bat is the file users are told to edit; an update installs it only when missing.
    private static bool IsUserEditable(string name) =>
        string.Equals(name, "start-stream.bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the installed exe with no arguments so it opens the window. The
    /// environment variable makes it wait for this process to exit first, so
    /// it never reads settings while this process is still saving them.
    /// </summary>
    internal void LaunchInstalled()
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = _installDir,
        };
        start.Environment[WaitForProcessVariable] =
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Called first thing at startup: waits, bounded, for the process that launched this one after an update.</summary>
    internal static void WaitForUpdatingProcess()
    {
        string? value = Environment.GetEnvironmentVariable(WaitForProcessVariable);
        if (value is null) return;
        Environment.SetEnvironmentVariable(WaitForProcessVariable, null);
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int pid))
            return;

        try
        {
            using Process previous = Process.GetProcessById(pid);
            // A reused PID belongs to some other program; never wait on it.
            if (string.Equals(previous.ProcessName, Path.GetFileNameWithoutExtension(ExeName),
                    StringComparison.OrdinalIgnoreCase))
                previous.WaitForExit(PreviousProcessExitTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The previous process has already exited.
        }
    }

    internal static async Task<bool> MatchesSha256Async(Stream content, byte[] expected, CancellationToken ct)
    {
        byte[] actual = await SHA256.HashDataAsync(content, ct).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Top-level names in the staged package with Spectari.exe last.</summary>
    internal static IReadOnlyList<string> StagedEntriesExeLast(string stagedDir)
    {
        var names = Directory.EnumerateFileSystemEntries(stagedDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();
        names.Sort((a, b) =>
        {
            bool aExe = string.Equals(a, ExeName, StringComparison.OrdinalIgnoreCase);
            bool bExe = string.Equals(b, ExeName, StringComparison.OrdinalIgnoreCase);
            return aExe == bExe ? StringComparer.OrdinalIgnoreCase.Compare(a, b) : aExe ? 1 : -1;
        });
        return names;
    }

    private async Task DownloadAsync(
        ReleasePackage package, string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUri);
        request.Headers.UserAgent.ParseAdd("Spectari-updater");

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(ReadStallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("The download timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException("The download failed.");
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new UpdateException("The download was redirected to an insecure address.");

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            byte[] buffer = new byte[81920];
            long total = 0;
            int lastPercent = -1;
            while (true)
            {
                stall.CancelAfter(ReadStallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new UpdateException("The download stopped responding.");
                }
                if (read == 0) break;

                total += read;
                if (total > package.Size)
                    throw new UpdateException("The download was larger than expected.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                int percent = (int)(total * 100 / package.Size);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent / 100.0);
                }
            }

            if (total != package.Size)
                throw new UpdateException("The download was incomplete.");
        }
    }

    private static void ValidateStagedPackage(string stagedDir, Version expectedVersion)
    {
        string exe = Path.Combine(stagedDir, ExeName);
        if (!File.Exists(exe) || EntryExists(Path.Combine(stagedDir, WorkFolderName)))
            throw new UpdateException("The update package is not a Spectari release.");

        string? productVersion = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        if (UpdateChecker.ParseVersion(productVersion) is not { } packaged || !packaged.Equals(expectedVersion))
            throw new UpdateException("The update package has the wrong version.");
    }

    private bool RollBack(List<(string Name, bool HadOld, bool Placed)> applied)
    {
        bool restored = true;
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            (string name, bool hadOld, bool placed) = applied[i];
            string target = Path.Combine(_installDir, name);
            string backup = Path.Combine(BackupDir, name);
            try
            {
                if (placed) MoveEntry(target, Path.Combine(StagedDir, name));
                if (hadOld && EntryExists(backup)) MoveEntry(backup, target);
            }
            catch (Exception ex)
            {
                if (hadOld && string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase)
                    && TryCopyFile(backup, target))
                    continue;
                restored = false;
                Console.Error.WriteLine($"[update] rollback could not restore {name}: {ex.Message}");
            }
        }
        return restored;
    }

    private static bool TryCopyFile(string source, string destination)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] rollback could not copy {Path.GetFileName(source)} back: {ex.Message}");
            return false;
        }
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
