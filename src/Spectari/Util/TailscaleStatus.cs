using System.Text.Json;

namespace Spectari.Util;

internal enum TailscaleStage { NotInstalled, NotRunning, SignedOut, Disconnected, Running }

/// <param name="MachineName">This PC's name on the tailnet, when connected.</param>
internal sealed record TailscaleState(TailscaleStage Stage, string? MachineName = null);

/// <summary>Reads this PC's Tailscale state from the Tailscale CLI. Never handles keys.</summary>
internal static class TailscaleStatus
{
    internal const string DownloadPage = "https://tailscale.com/download";
    internal const string MachinesPage = "https://login.tailscale.com/admin/machines";

    private static readonly string InstallFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale");

    /// <summary>Runs the CLI (up to a few seconds); call off the UI thread.</summary>
    internal static TailscaleState Read() =>
        Parse(RunCli("status --json"), File.Exists(Path.Combine(InstallFolder, "tailscale.exe")));

    internal static TailscaleState Parse(string? statusJson, bool installed)
    {
        if (statusJson is null)
            return new TailscaleState(installed ? TailscaleStage.NotRunning : TailscaleStage.NotInstalled);
        try
        {
            using JsonDocument document = JsonDocument.Parse(statusJson);
            JsonElement root = document.RootElement;
            string? backend = root.TryGetProperty("BackendState", out JsonElement state) ? state.GetString() : null;
            return backend switch
            {
                "Running" => new TailscaleState(TailscaleStage.Running, MachineName(root)),
                "NeedsLogin" or "NeedsMachineAuth" => new TailscaleState(TailscaleStage.SignedOut),
                "Stopped" => new TailscaleState(TailscaleStage.Disconnected),
                _ => new TailscaleState(TailscaleStage.NotRunning),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new TailscaleState(TailscaleStage.NotRunning);
        }
    }

    /// <summary>Opens the Tailscale app, which handles starting, signing in, and connecting.</summary>
    internal static void OpenApp()
    {
        string app = Path.Combine(InstallFolder, "tailscale-ipn.exe");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tailscale] could not open the Tailscale app: {ex.Message}");
            Browser.Open(DownloadPage);
        }
    }

    /// <summary>Output of a successful CLI run, or null when the CLI is missing, fails, or hangs.</summary>
    internal static string? RunCli(string args)
    {
        string[] candidates = ["tailscale", Path.Combine(InstallFolder, "tailscale.exe")];
        foreach (string exe in candidates)
        {
            try
            {
                // Drains both pipes and can't block past 5s - a tailscale that
                // hangs without closing stdout can't wedge the caller.
                var r = ProcessRunner.Run(exe, args, 5000);
                if (!r.TimedOut && r.ExitCode == 0 && r.StdOut.Length > 0) return r.StdOut;
            }
            catch
            {
                // Missing executable: try the next candidate.
            }
        }
        return null;
    }

    private static string? MachineName(JsonElement root)
    {
        if (!root.TryGetProperty("Self", out JsonElement self)) return null;
        string? dns = self.TryGetProperty("DNSName", out JsonElement d) ? d.GetString() : null;
        if (!string.IsNullOrEmpty(dns)) return dns.Split('.')[0];
        return self.TryGetProperty("HostName", out JsonElement h) ? h.GetString() : null;
    }
}
