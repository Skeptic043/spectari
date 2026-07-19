using System.Runtime.InteropServices;

namespace Spectari.Capture;

internal sealed record CaptureDeviceDescription(string SymbolicLink, string FriendlyName);

internal sealed record CaptureDeviceDisplayItem(CaptureDeviceDescription Device, string DisplayName);

internal enum CaptureDevicePixelFormat
{
    Nv12,
    Yuy2,
    Mjpeg,
}

internal sealed record CaptureDeviceFormat(
    CaptureDevicePixelFormat PixelFormat,
    int Width,
    int Height,
    uint FrameRateNumerator,
    uint FrameRateDenominator)
{
    internal double FramesPerSecond => FrameRateDenominator == 0
        ? 0
        : (double)FrameRateNumerator / FrameRateDenominator;

    internal int RoundedFramesPerSecond => Math.Max(1, (int)Math.Round(FramesPerSecond));
}

internal static class CaptureDevicePolicy
{
    private const double PreferredFrameRateCeiling = 60.5;

    internal static IReadOnlyList<CaptureDeviceDisplayItem> PrepareDisplayItems(
        IEnumerable<CaptureDeviceDescription> devices)
    {
        List<CaptureDeviceDescription> ordered = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.SymbolicLink))
            .GroupBy(device => device.SymbolicLink, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(device => DisplayBaseName(device), StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.SymbolicLink, StringComparer.Ordinal)
            .ToList();

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CaptureDeviceDisplayItem>(ordered.Count);
        foreach (CaptureDeviceDescription device in ordered)
        {
            string baseName = DisplayBaseName(device);
            nameCounts.TryGetValue(baseName, out int previousCount);
            int count = previousCount + 1;
            nameCounts[baseName] = count;
            string displayName = count == 1 ? baseName : $"{baseName} ({count})";
            items.Add(new CaptureDeviceDisplayItem(device, displayName));
        }
        return items;
    }

    internal static CaptureDeviceFormat? ChoosePreferredFormat(IEnumerable<CaptureDeviceFormat> formats)
    {
        List<CaptureDeviceFormat> supported = formats
            .Where(format => format.Width > 0 && format.Height > 0 && format.FramesPerSecond > 0)
            .ToList();
        if (supported.Count == 0) return null;

        List<CaptureDeviceFormat> withinCeiling = supported
            .Where(format => format.FramesPerSecond <= PreferredFrameRateCeiling)
            .ToList();
        IEnumerable<CaptureDeviceFormat> candidates = withinCeiling.Count > 0 ? withinCeiling : supported;

        return candidates
            .OrderByDescending(PixelRate)
            .ThenByDescending(format => format.FramesPerSecond)
            .ThenByDescending(format => (long)format.Width * format.Height)
            .ThenBy(format => PixelFormatOrder(format.PixelFormat))
            .ThenByDescending(format => format.Width)
            .ThenByDescending(format => format.Height)
            .First();
    }

    private static string DisplayBaseName(CaptureDeviceDescription device) =>
        string.IsNullOrWhiteSpace(device.FriendlyName) ? "Capture device" : device.FriendlyName.Trim();

    private static double PixelRate(CaptureDeviceFormat format) =>
        (double)format.Width * format.Height * format.FramesPerSecond;

    private static int PixelFormatOrder(CaptureDevicePixelFormat format) => format switch
    {
        CaptureDevicePixelFormat.Nv12 => 0,
        CaptureDevicePixelFormat.Yuy2 => 1,
        CaptureDevicePixelFormat.Mjpeg => 2,
        _ => int.MaxValue,
    };
}

internal static class CaptureDeviceEnumerator
{
    internal static List<CaptureDeviceDescription> GetDevices()
    {
        try
        {
            return EnumerateDevices();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[capture-device] enumeration failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            return [];
        }
    }

    private static List<CaptureDeviceDescription> EnumerateDevices()
    {
        nint attributes = 0;
        nint activateArray = 0;
        uint count = 0;
        bool mediaFoundationStarted = false;
        try
        {
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFStartup(MediaFoundationInterop.MfVersion, 0),
                "Media Foundation startup");
            mediaFoundationStarted = true;
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFCreateAttributes(out attributes, 1),
                "Capture device enumeration setup");
            MediaFoundationInterop.SetGuid(
                attributes,
                MediaFoundationInterop.MfDevSourceAttributeSourceType,
                MediaFoundationInterop.MfDevSourceAttributeSourceTypeVidcapGuid);
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFEnumDeviceSources(attributes, out activateArray, out count),
                "Capture device enumeration");

            var devices = new List<CaptureDeviceDescription>(checked((int)count));
            for (int index = 0; index < count; index++)
            {
                nint activate = Marshal.ReadIntPtr(activateArray, checked(index * nint.Size));
                string symbolicLink = MediaFoundationInterop.GetString(
                    activate,
                    MediaFoundationInterop.MfDevSourceAttributeSourceTypeVidcapSymbolicLink);
                string friendlyName = MediaFoundationInterop.GetString(
                    activate,
                    MediaFoundationInterop.MfDevSourceAttributeFriendlyName);
                if (!string.IsNullOrWhiteSpace(symbolicLink))
                    devices.Add(new CaptureDeviceDescription(symbolicLink, friendlyName));
            }
            return devices;
        }
        finally
        {
            if (activateArray != 0)
            {
                for (int index = 0; index < count; index++)
                {
                    nint activate = Marshal.ReadIntPtr(activateArray, checked(index * nint.Size));
                    MediaFoundationInterop.Release(ref activate);
                }
                Marshal.FreeCoTaskMem(activateArray);
            }
            MediaFoundationInterop.Release(ref attributes);
            if (mediaFoundationStarted) _ = MediaFoundationInterop.MFShutdown();
        }
    }
}
