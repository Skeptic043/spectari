using System.Security.Cryptography;
using System.Text;
using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _install = Path.Combine(Path.GetTempPath(), "spectari-update-test-" + Guid.NewGuid().ToString("N"));
    private string WorkDir => Path.Combine(_install, UpdateInstaller.WorkFolderName);
    private string StagedDir => Path.Combine(WorkDir, "new");
    private string BackupDir => Path.Combine(WorkDir, "old");

    public UpdateInstallerTests()
    {
        WritePackage(_install, "old");
        Write(Path.Combine(_install, "user-notes.txt"), "keep");
    }

    public void Dispose()
    {
        try { Directory.Delete(_install, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ApplyReplacesPackageItemsAndKeepsOtherFiles()
    {
        WritePackage(StagedDir, "new");

        new UpdateInstaller(_install).ApplyStaged();

        AssertPackage(_install, "new");
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_install, "user-notes.txt")));
        AssertPackage(BackupDir, "old");
    }

    [Fact]
    public void FailedApplyRollsBackToTheOldInstall()
    {
        WritePackage(StagedDir, "new");
        var installer = new UpdateInstaller(_install);

        UpdateException failure;
        using (File.Open(Path.Combine(StagedDir, "wwwroot", "index.html"), FileMode.Open, FileAccess.Read, FileShare.None))
            failure = Assert.Throws<UpdateException>(installer.ApplyStaged);

        Assert.True(failure.InstallUnchanged);
        AssertPackage(_install, "old");
    }

    [Fact]
    public void CleanupAfterACompletedSwapLeavesTheNewInstall()
    {
        WritePackage(StagedDir, "new");
        var installer = new UpdateInstaller(_install);
        installer.ApplyStaged();

        installer.RecoverOrCleanUp();

        AssertPackage(_install, "new");
        Assert.False(Directory.Exists(WorkDir));
    }

    [Fact]
    public void InterruptedSwapIsRestoredOnTheNextStart()
    {
        // State after a crash that moved ffmpeg.exe and wwwroot but not Spectari.exe.
        Directory.CreateDirectory(BackupDir);
        File.Move(Path.Combine(_install, "ffmpeg.exe"), Path.Combine(BackupDir, "ffmpeg.exe"));
        Directory.Move(Path.Combine(_install, "wwwroot"), Path.Combine(BackupDir, "wwwroot"));
        Write(Path.Combine(_install, "ffmpeg.exe"), "new ffmpeg.exe");

        new UpdateInstaller(_install).RecoverOrCleanUp();

        AssertPackage(_install, "old");
        Assert.False(Directory.Exists(WorkDir));
    }

    [Fact]
    public void UserEditedStartScriptSurvivesAnUpdate()
    {
        string script = Path.Combine(_install, "start-stream.bat");
        Write(script, "edited by the user");
        WritePackage(StagedDir, "new");
        Write(Path.Combine(StagedDir, "start-stream.bat"), "packaged");
        var installer = new UpdateInstaller(_install);

        installer.ApplyStaged();
        installer.RecoverOrCleanUp();

        AssertPackage(_install, "new");
        Assert.Equal("edited by the user", File.ReadAllText(script));
    }

    [Fact]
    public void MissingStartScriptIsInstalled()
    {
        WritePackage(StagedDir, "new");
        Write(Path.Combine(StagedDir, "start-stream.bat"), "packaged");

        new UpdateInstaller(_install).ApplyStaged();

        Assert.Equal("packaged", File.ReadAllText(Path.Combine(_install, "start-stream.bat")));
    }

    [Fact]
    public void CleanupNeverDeletesTheOnlyExe()
    {
        // State after a crash between moving the old exe out and the new exe in,
        // even with a commit marker present.
        Directory.CreateDirectory(BackupDir);
        File.Move(Path.Combine(_install, "ffmpeg.exe"), Path.Combine(BackupDir, "ffmpeg.exe"));
        File.Move(Path.Combine(_install, UpdateInstaller.ExeName), Path.Combine(BackupDir, UpdateInstaller.ExeName));
        Write(Path.Combine(_install, "ffmpeg.exe"), "new ffmpeg.exe");
        Write(Path.Combine(WorkDir, "committed"), "");

        new UpdateInstaller(_install).RecoverOrCleanUp();

        AssertPackage(_install, "old");
        Assert.False(Directory.Exists(WorkDir));
    }

    [Fact]
    public void SpectariExeIsSwappedLast()
    {
        WritePackage(StagedDir, "new");
        Write(Path.Combine(StagedDir, "zzz.txt"), "z");

        IReadOnlyList<string> order = UpdateInstaller.StagedEntriesExeLast(StagedDir);

        Assert.Equal(UpdateInstaller.ExeName, order[^1]);
        Assert.Equal(5, order.Count);
    }

    [Fact]
    public async Task ChecksumMustMatchExactly()
    {
        byte[] content = Encoding.UTF8.GetBytes("package bytes");
        byte[] expected = SHA256.HashData(content);
        byte[] wrong = (byte[])expected.Clone();
        wrong[0] ^= 1;

        Assert.True(await UpdateInstaller.MatchesSha256Async(new MemoryStream(content), expected, CancellationToken.None));
        Assert.False(await UpdateInstaller.MatchesSha256Async(new MemoryStream(content), wrong, CancellationToken.None));
    }

    private static void WritePackage(string root, string label)
    {
        Write(Path.Combine(root, UpdateInstaller.ExeName), $"{label} {UpdateInstaller.ExeName}");
        Write(Path.Combine(root, "ffmpeg.exe"), $"{label} ffmpeg.exe");
        Write(Path.Combine(root, "README.txt"), $"{label} README.txt");
        Write(Path.Combine(root, "wwwroot", "index.html"), $"{label} index.html");
    }

    private static void AssertPackage(string root, string label)
    {
        Assert.Equal($"{label} {UpdateInstaller.ExeName}", File.ReadAllText(Path.Combine(root, UpdateInstaller.ExeName)));
        Assert.Equal($"{label} ffmpeg.exe", File.ReadAllText(Path.Combine(root, "ffmpeg.exe")));
        Assert.Equal($"{label} README.txt", File.ReadAllText(Path.Combine(root, "README.txt")));
        Assert.Equal($"{label} index.html", File.ReadAllText(Path.Combine(root, "wwwroot", "index.html")));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
