using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spectari.Util;

/// <summary>Everything the app window remembers between runs.</summary>
internal sealed class AppSettings
{
    public string SourceKind { get; set; } = "window";
    public string WindowProcess { get; set; } = "";
    public string MonitorDeviceName { get; set; } = "";
    public string CaptureDeviceSymbolicLink { get; set; } = "";
    [JsonPropertyName("MonitorIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyMonitorIndex { get; set; }
    // Presets are stored by height+fps, not array index - adding a preset
    // used to silently shift everyone's saved choice.
    public int PresetHeight { get; set; } = 1080;
    public int PresetFps { get; set; } = 60;
    public string BitrateTier { get; set; } = "med"; // "low" | "med" | "high"
    public string AudioSource { get; set; } = "window"; // reserved mode key or process name
    public int Port { get; set; } = StreamLimits.DefaultPort;
    public string StreamName { get; set; } = ""; // shown to viewers; empty = machine name
    public string Encoder { get; set; } = "auto";
    // Set only when Open port with "Allow LAN" checked actually succeeded,
    // so the app knows LAN addresses are reachable (not merely requested).
    public bool AllowLan { get; set; }
    // The exact port that Open port last configured. LAN is advertised only
    // when AllowLan is set AND the port in play matches this, so editing the
    // port after a successful fix can't advertise LAN on a port never opened.
    // A missing value deserializes to 0, which matches no real port, so LAN
    // stays off until the next successful Open port on that port.
    public int AllowLanPort { get; set; }
    // Canonical four-component version dismissed from the update notice.
    public string SkipUpdateVersion { get; set; } = "";
    public bool CheckForUpdatesOnStartup { get; set; } = true;
}

/// <summary>Reads, checks, and writes settings.json.</summary>
internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Missing or unreadable settings give the defaults; read problems are logged once.</summary>
    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[settings] could not read settings, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Writes a sibling temp file and swaps it in, so a crash mid-write can't leave a truncated file.</summary>
    internal static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(settings));
        File.Move(tmp, path, overwrite: true);
    }

    internal static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, WriteOptions);

    /// <summary>Never throws: broken JSON gives the defaults, and out-of-range values are replaced.</summary>
    internal static AppSettings Parse(string json)
    {
        AppSettings? settings = null;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[settings] settings file is damaged, using defaults: {ex.Message}");
        }
        return Normalize(settings ?? new AppSettings());
    }

    private static AppSettings Normalize(AppSettings s)
    {
        var defaults = new AppSettings();
        s.SourceKind = s.SourceKind is "window" or "monitor" or "capture-device" ? s.SourceKind : defaults.SourceKind;
        s.WindowProcess ??= "";
        s.MonitorDeviceName ??= "";
        s.CaptureDeviceSymbolicLink ??= "";
        s.BitrateTier = s.BitrateTier is "low" or "med" or "high" ? s.BitrateTier : defaults.BitrateTier;
        s.AudioSource = string.IsNullOrEmpty(s.AudioSource) ? defaults.AudioSource : s.AudioSource;
        s.Port = StreamLimits.IsAppPort(s.Port) ? s.Port : defaults.Port;
        s.StreamName = StreamLimits.CleanStreamName(s.StreamName);
        s.Encoder = StreamLimits.IsKnownEncoder(s.Encoder) ? s.Encoder : defaults.Encoder;
        if (!StreamLimits.IsAppPort(s.AllowLanPort))
        {
            s.AllowLan = false;
            s.AllowLanPort = 0;
        }
        s.SkipUpdateVersion ??= "";
        return s;
    }
}

/// <summary>The accepted ranges for stream settings, shared by the app window and the command line.</summary>
internal static class StreamLimits
{
    internal const int DefaultPort = 8093;
    internal const int MinAppPort = 1024;
    internal const int MaxPort = 65535;
    internal const int MaxStreamNameLength = 32;
    internal const int MaxFps = 240;
    internal const int MinHeight = 144;
    internal const int MaxHeight = 4320;
    internal const int MaxBitrateKbps = 200_000;

    private static readonly string[] Encoders = ["auto", "h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    internal static bool IsKnownEncoder(string? encoder) => encoder is not null && Encoders.Contains(encoder);

    internal static bool IsAppPort(int port) => port is >= MinAppPort and <= MaxPort;

    internal static string CleanStreamName(string? name)
    {
        string trimmed = (name ?? "").Trim();
        return trimmed.Length <= MaxStreamNameLength ? trimmed : trimmed[..MaxStreamNameLength].TrimEnd();
    }
}
