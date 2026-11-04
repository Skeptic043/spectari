using System.Runtime.InteropServices;

namespace Spectari.Audio;

internal sealed record ProcessEntry(uint Pid, uint ParentPid, string Name);

/// <summary>
/// The process to leave out of desktop audio, followed by app name for the
/// whole stream so the app is still left out after it starts or restarts.
/// While the app is not running, Spectari's own process is left out instead,
/// which captures all other desktop audio.
/// </summary>
internal sealed class ExcludedAppTarget : IWindowAudioTarget, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _app;
    private readonly uint _ownPid;
    private readonly Func<IReadOnlyList<ProcessEntry>> _snapshot;
    private readonly System.Threading.Timer? _timer;
    private readonly Lock _gate = new();
    private uint _pid;

    public event Action<uint>? ProcessAttached;

    /// <param name="snapshot">Lists running processes; the default reads them from Windows and polls on a timer.</param>
    internal ExcludedAppTarget(string app, uint ownPid, Func<IReadOnlyList<ProcessEntry>>? snapshot = null)
    {
        _app = app;
        _ownPid = ownPid;
        _snapshot = snapshot ?? ProcessSnapshot.Take;
        _pid = ChoosePid(_snapshot(), _app, _ownPid);
        // One-shot and re-armed after each poll, so polls never overlap and
        // report an older process after a newer one.
        if (snapshot is null)
            _timer = new System.Threading.Timer(_ => PollAndRearm(), null, PollInterval, Timeout.InfiniteTimeSpan);
    }

    public uint AudioProcessId { get { lock (_gate) return _pid; } }

    private void PollAndRearm()
    {
        Poll();
        try { _timer?.Change(PollInterval, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* stream stopped */ }
    }

    /// <summary>Re-reads the processes and reports a change of the excluded process.</summary>
    internal void Poll()
    {
        uint pid;
        try
        {
            pid = ChoosePid(_snapshot(), _app, _ownPid);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] reading processes for the left-out app failed: {ex.Message}");
            return;
        }
        lock (_gate)
        {
            if (pid == _pid) return;
            _pid = pid;
        }
        ProcessAttached?.Invoke(pid);
    }

    /// <summary>
    /// The app's root process: the first process with the app's name whose
    /// parent has a different name, so excluding its tree covers the app's
    /// helper processes. Falls back to Spectari's own process.
    /// </summary>
    internal static uint ChoosePid(IReadOnlyList<ProcessEntry> processes, string app, uint ownPid)
    {
        var named = processes.Where(p => p.Name.Equals(app, StringComparison.OrdinalIgnoreCase)).ToList();
        var namedPids = named.Select(p => p.Pid).ToHashSet();
        ProcessEntry? root = named.FirstOrDefault(p => !namedPids.Contains(p.ParentPid)) ?? named.FirstOrDefault();
        return root?.Pid ?? ownPid;
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Starts desktop audio that leaves the app out for the whole stream,
    /// following it as it starts and restarts. If switching fails, audio goes
    /// silent rather than including the app.
    /// </summary>
    internal static IDisposable StartDesktopCapture(string app, long epoch, Action<byte[], int> write)
    {
        var target = new ExcludedAppTarget(app, (uint)Environment.ProcessId);
        try
        {
            var rebinder = new ProcessAudioRebinder(target, epoch, write,
                (pid, start, emit) => new ProcessAudioCapture(pid, start, emit, excludeTargetTree: true),
                failClosed: true);
            return new Owned(target, rebinder);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private sealed class Owned(IDisposable target, IDisposable capture) : IDisposable
    {
        public void Dispose()
        {
            target.Dispose();
            capture.Dispose();
        }
    }
}

/// <summary>Reads the running processes with their parents from Windows.</summary>
internal static class ProcessSnapshot
{
    private const uint Th32csSnapProcess = 0x2;

    internal static IReadOnlyList<ProcessEntry> Take()
    {
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException($"process snapshot failed ({Marshal.GetLastWin32Error()})");
        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        var processes = new List<ProcessEntry>();
        // An empty result would read as "app not running" and include it.
        if (!Process32FirstW(snapshot, ref entry))
            throw new InvalidOperationException($"process list unreadable ({Marshal.GetLastWin32Error()})");
        do
        {
            processes.Add(new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID,
                Path.GetFileNameWithoutExtension(entry.szExeFile)));
        }
        while (Process32NextW(snapshot, ref entry));
        return processes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref PROCESSENTRY32W entry);
}
