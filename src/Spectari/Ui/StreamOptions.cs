namespace Spectari.Ui;

/// <summary>
/// The quality choices shared by the main window and the Switch source dialog.
/// Presets are the target output (resolution + fps); the bitrate options are
/// derived from the preset and the selected source, so the streamer sees the
/// real Mbps before starting. Height 0 = Native: the label is rewritten with
/// the selected source's actual size.
/// </summary>
internal static class StreamOptions
{
    internal sealed record Preset(string Label, int Height, int Fps)
    {
        public override string ToString() => Label;
    }

    internal sealed record BitrateChoice(string Label, int Kbps, string Tier)
    {
        public override string ToString() => Label;
    }

    internal sealed record EncoderChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    internal static readonly Preset[] Presets =
    [
        new("720p · 30 fps", 720, 30),
        new("720p · 60 fps", 720, 60),
        new("1080p · 30 fps", 1080, 30),
        new("1080p · 60 fps", 1080, 60),
        new("1440p · 30 fps", 1440, 30),
        new("1440p · 60 fps", 1440, 60),
        new("Native · 30 fps", 0, 30),
        new("Native · 60 fps", 0, 60),
    ];

    internal static readonly EncoderChoice[] Encoders =
    [
        new("Auto (recommended)", "auto"),
        new("NVIDIA (NVENC)", "h264_nvenc"),
        new("AMD (AMF)", "h264_amf"),
        new("Intel (QSV)", "h264_qsv"),
        new("CPU (x264)", "libx264"),
    ];

    internal static readonly int DefaultPresetIndex =
        Array.FindIndex(Presets, p => p.Height == 1080 && p.Fps == 60);

    internal static string ComputeNativeLabel(int fps, (int W, int H) sourceSize)
    {
        var (w, h) = sourceSize;
        return w > 0 && h > 0 ? $"Native · {fps} fps  ({w}x{h})" : $"Native · {fps} fps  (source size)";
    }

    /// <summary>The Low/Medium/High options for a preset applied to a source:
    /// the numbers come from the actual output size (pixel area, so a tall
    /// portrait window is billed by its real pixel count, not its height).</summary>
    internal static List<BitrateChoice> BuildBitrateChoices(Preset preset, (int W, int H) sourceSize)
    {
        var (srcW, srcH) = sourceSize;
        if (srcH <= 0) (srcW, srcH) = (1920, 1080); // nothing resolved yet
        int outH = preset.Height > 0 && preset.Height < srcH ? preset.Height : srcH;
        int outW = (int)Math.Round((double)srcW * outH / srcH);
        var t = StreamSession.BitrateTiers(outW, outH, preset.Fps);
        return
        [
            new BitrateChoice($"Low · {Mb(t.Low)} Mbps", t.Low, "low"),
            new BitrateChoice($"Medium · {Mb(t.Medium)} Mbps", t.Medium, "med"),
            new BitrateChoice($"High · {Mb(t.High)} Mbps", t.High, "high"),
        ];
    }

    private static string Mb(int kbps) => kbps % 1000 == 0 ? (kbps / 1000).ToString() : (kbps / 1000.0).ToString("0.#");
}
