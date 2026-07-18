using System.Diagnostics;

namespace Spectari.Util;

/// <summary>
/// Elevated port configuration, same two steps as packaging/setup.bat: reserve
/// the http.sys URL for the streaming user and open the firewall. The firewall
/// rule is scoped to Tailscale only by default; the caller opts in to the
/// private LAN ranges via <paramref name="allowLan"/>. Runs silently (no
/// console) because it is invoked via UAC from the "Open port" button; the
/// caller reads the exit code.
/// Exit codes: 0 ok, 2 urlacl failed, 3 firewall rule failed.
/// </summary>
public static class PortSetup
{
    public static int Run(int port, string? user, bool allowLan)
    {
        // The UAC prompt may elevate as a different account than the one that
        // will run Spectari, so the unelevated app passes its own identity.
        user ??= $"{Environment.UserDomainName}\\{Environment.UserName}";

        // Capture whatever account currently holds this URL so a half-failed
        // setup can put it back instead of leaving nothing reserved. Reading the
        // reservation needs no admin; the mutating netsh calls below do. Restore
        // re-grants the prior USER account only - it does not preserve a full
        // custom SDDL - which is acceptable here and far better than silently
        // destroying a foreign reservation.
        HostAccessRollbackState prior = HostAccessService.ReadRollbackState(port);

        Exec($"http delete urlacl url=http://+:{port}/");
        if (Exec($"http add urlacl url=http://+:{port}/ user=\"{user}\"") != 0)
        {
            if (prior.ReservationUser is not null)
                Exec($"http add urlacl url=http://+:{port}/ user=\"{prior.ReservationUser}\"");
            return 2;
        }

        string remoteip = HostAccessService.FirewallRemoteIp(allowLan);
        Exec($"advfirewall firewall delete rule name=\"Spectari {port}\"");
        // Legacy cleanup: machines migrating from the pre-rename app still hold a
        // rule under the old name; leaving it would keep a stale scope open.
        Exec($"advfirewall firewall delete rule name=\"StreamHost {port}\"");
        if (Exec($"advfirewall firewall add rule name=\"Spectari {port}\" dir=in action=allow protocol=TCP localport={port} remoteip={remoteip}") != 0)
        {
            // Roll the urlacl back to how we found it: drop ours, restore theirs.
            Exec($"http delete urlacl url=http://+:{port}/");
            if (prior.ReservationUser is not null)
                Exec($"http add urlacl url=http://+:{port}/ user=\"{prior.ReservationUser}\"");
            // The failed add followed a delete, so no rule is left. Re-add one at
            // the prior scope; if that scope was unreadable, fail CLOSED to the
            // Tailscale-only default rather than silently reopening the LAN.
            // Best-effort - ignore its exit; the user is never left with no rule.
            Exec($"advfirewall firewall add rule name=\"Spectari {port}\" dir=in action=allow protocol=TCP localport={port} remoteip={prior.FirewallRemoteIp}");
            return 3;
        }

        return 0;
    }

    private static int Exec(string netshArgs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", netshArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return -1;
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
