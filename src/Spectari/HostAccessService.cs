using System.ComponentModel;
using System.Diagnostics;
using Spectari.Util;

namespace Spectari;

internal enum HostReservationStatus
{
    AvailableOrOwned,
    UnknownOwner,
    ForeignOwner,
}

internal readonly record struct HostReservationReview(
    HostReservationStatus Status,
    string? Owner);

internal enum HostAccessSetupOutcome
{
    Succeeded,
    ApprovalDeclined,
    Failed,
}

internal readonly record struct HostAccessSetupRequest(
    int Port,
    bool AllowLan);

internal readonly record struct HostAccessSetupResult(
    HostAccessSetupRequest Request,
    int ExitCode,
    HostAccessSetupOutcome Outcome);

internal readonly record struct HostAccessPersistedState(
    bool AllowLan,
    int Port);

internal readonly record struct HostAccessRollbackState(
    string? ReservationUser,
    string FirewallRemoteIp);

internal sealed class HostAccessService
{
    internal const string TailscaleRange = "100.64.0.0/10";
    internal const string LanRanges = "192.168.0.0/16,10.0.0.0/8,172.16.0.0/12";
    // Sentinel for a reservation that exists but whose owner cannot be read
    // (e.g. a non-English Windows labels the "User:" line differently). Callers
    // treat it as "do not touch", never as absent.
    internal const string UnknownOwner = "an account Spectari could not identify";

    private readonly Action<Action> _dispatch;
    private readonly Action<string> _log;
    private readonly object _stateLock = new();
    private HostAccessPersistedState _persistedState;
    private int _configuring;

    internal HostAccessService(Action<Action> dispatch, Action<string> log)
    {
        _dispatch = dispatch;
        _log = log;
    }

    internal event Action<HostAccessSetupRequest>? SetupStarted;
    internal event Action<HostAccessSetupResult>? SetupCompleted;

    internal HostAccessPersistedState PersistedState
    {
        get
        {
            lock (_stateLock) return _persistedState;
        }
    }

    internal void RestorePersistedState(bool allowLan, int port)
    {
        lock (_stateLock)
            _persistedState = new HostAccessPersistedState(allowLan, port);
    }

    internal bool IsLanAppliedForPort(int port)
    {
        HostAccessPersistedState state = PersistedState;
        return state.AllowLan && state.Port == port;
    }

    internal HostReservationReview ReviewReservation(int port) =>
        ReviewReservation(port, CurrentUser());

    internal static HostReservationReview ReviewReservation(int port, string currentUser) =>
        ClassifyReservationOwner(ReadReservationOwner(port), currentUser);

    internal static HostReservationReview ClassifyReservationOwner(
        string? owner,
        string currentUser)
    {
        if (owner is null || owner.Equals(currentUser, StringComparison.OrdinalIgnoreCase))
            return new HostReservationReview(HostReservationStatus.AvailableOrOwned, owner);
        return owner == UnknownOwner
            ? new HostReservationReview(HostReservationStatus.UnknownOwner, null)
            : new HostReservationReview(HostReservationStatus.ForeignOwner, owner);
    }

    internal bool TryConfigure(int port, bool allowLan, string executablePath)
    {
        if (Interlocked.CompareExchange(ref _configuring, 1, 0) != 0) return false;

        var request = new HostAccessSetupRequest(port, allowLan);
        _log($"Asking for administrator approval to configure port {port}…");
        SetupStarted?.Invoke(request);

        var thread = new Thread(() => RunHelper(request, executablePath))
        {
            IsBackground = true,
            Name = "host-access",
        };
        try
        {
            thread.Start();
        }
        catch
        {
            CompleteSetup(request, -1);
        }
        return true;
    }

    internal static HostAccessSetupOutcome InterpretHelperExitCode(int exitCode) => exitCode switch
    {
        0 => HostAccessSetupOutcome.Succeeded,
        -2 => HostAccessSetupOutcome.ApprovalDeclined,
        _ => HostAccessSetupOutcome.Failed,
    };

    internal static string FirewallRemoteIp(bool allowLan) =>
        allowLan ? $"{TailscaleRange},{LanRanges}" : TailscaleRange;

    internal static HostAccessRollbackState ReadRollbackState(int port) =>
        new(
            ReadReservationUser(port),
            ResolveRollbackFirewallRemoteIp(ReadRuleRemoteIp(port)));

    internal static string ResolveRollbackFirewallRemoteIp(string? remoteIp) =>
        remoteIp ?? TailscaleRange;

    internal static string? ReadReservationOwner(int port)
    {
        try
        {
            ProcessResult result = ProcessRunner.Run(
                "netsh", $"http show urlacl url=http://+:{port}/", 5000);
            return InterpretReservationOwnerProbe(port, result);
        }
        catch
        {
            return UnknownOwner;
        }
    }

    internal static string? InterpretReservationOwnerProbe(int port, ProcessResult result)
    {
        // FAIL CLOSED: null only when the probe ran cleanly and nothing is
        // reserved. A timeout, nonzero exit, or unreadable owner returns the
        // sentinel so a caller never deletes a reservation it could not
        // positively read as absent or its own.
        if (result.TimedOut || result.ExitCode is not 0)
            return UnknownOwner;

        bool reserved = result.StdOut.Contains(
            $"http://+:{port}/", StringComparison.OrdinalIgnoreCase);
        foreach (string line in result.StdOut.Split('\n'))
        {
            int index = line.IndexOf("User:", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            string owner = line[(index + 5)..].Trim();
            if (owner.Length > 0) return owner;
        }
        return reserved ? UnknownOwner : null;
    }

    private static string CurrentUser() =>
        $"{Environment.UserDomainName}\\{Environment.UserName}";

    private void RunHelper(HostAccessSetupRequest request, string executablePath)
    {
        string arguments =
            $"--setup-port {request.Port} --setup-user \"{CurrentUser()}\"";
        if (request.AllowLan) arguments += " --setup-lan";
        var startInfo = new ProcessStartInfo(executablePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        int exitCode;
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                exitCode = -1;
            }
            else
            {
                // The helper is ELEVATED and this process is not, so it cannot
                // be killed across the boundary; releasing the configure guard
                // before the real exit would let a re-click run a SECOND helper
                // on the same URL reservation. The wait must stay unbounded.
                if (!process.WaitForExit(60000))
                {
                    _log("Still configuring the port. Waiting for the administrator step to finish…");
                    process.WaitForExit();
                }
                exitCode = process.ExitCode;
            }
        }
        catch (Win32Exception)
        {
            exitCode = -2;
        }
        catch
        {
            exitCode = -1;
        }

        CompleteSetup(request, exitCode);
    }

    private void CompleteSetup(HostAccessSetupRequest request, int exitCode)
    {
        HostAccessSetupOutcome outcome = InterpretHelperExitCode(exitCode);
        if (outcome == HostAccessSetupOutcome.Succeeded)
        {
            lock (_stateLock)
                _persistedState = new HostAccessPersistedState(request.AllowLan, request.Port);
        }

        Volatile.Write(ref _configuring, 0);
        var result = new HostAccessSetupResult(request, exitCode, outcome);
        try
        {
            _dispatch(() => SetupCompleted?.Invoke(result));
        }
        catch (Exception ex)
        {
            _log($"[host-access] completion dispatch failed: {ex.Message}");
        }
    }

    // Unlike the owner probe above, this DELIBERATELY returns null (not the
    // sentinel) when unreadable: it feeds the rollback's `add urlacl user=`
    // argument, where the sentinel would corrupt the restore. A best-effort
    // rollback that skips is the right failure here.
    private static string? ReadReservationUser(int port)
    {
        try
        {
            ProcessResult result = ProcessRunner.Run(
                "netsh", $"http show urlacl url=http://+:{port}/", 5000);
            foreach (string line in result.StdOut.Split('\n'))
            {
                int index = line.IndexOf("User:", StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                string owner = line[(index + 5)..].Trim();
                return owner.Length == 0 ? null : owner;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? ReadRuleRemoteIp(int port) =>
        ReadRuleRemoteIp(port, "Spectari") ?? ReadRuleRemoteIp(port, "StreamHost");

    private static string? ReadRuleRemoteIp(int port, string ruleApp)
    {
        try
        {
            ProcessResult result = ProcessRunner.Run(
                "netsh",
                $"advfirewall firewall show rule name=\"{ruleApp} {port}\"",
                5000);
            foreach (string line in result.StdOut.Split('\n'))
            {
                int index = line.IndexOf("RemoteIP:", StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                string address = line[(index + 9)..].Trim();
                return address.Length == 0 ? null : address;
            }
        }
        catch
        {
        }
        return null;
    }
}
