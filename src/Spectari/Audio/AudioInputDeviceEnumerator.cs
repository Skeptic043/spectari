using System.Runtime.InteropServices;

namespace Spectari.Audio;

internal sealed record AudioInputDeviceDescription(
    string Id,
    string FriendlyName,
    Guid? ContainerId = null);

internal sealed record AudioInputDeviceDisplayItem(
    AudioInputDeviceDescription Device,
    string DisplayName);

internal static class AudioInputDevicePolicy
{
    internal static IReadOnlyList<AudioInputDeviceDisplayItem> PrepareDisplayItems(
        IEnumerable<AudioInputDeviceDescription> devices)
    {
        List<AudioInputDeviceDescription> ordered = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => DisplayBaseName(device), StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AudioInputDeviceDisplayItem>(ordered.Count);
        foreach (AudioInputDeviceDescription device in ordered)
        {
            string baseName = DisplayBaseName(device);
            nameCounts.TryGetValue(baseName, out int previousCount);
            int count = previousCount + 1;
            nameCounts[baseName] = count;
            string suffix = count == 1 ? "" : $" ({count})";
            items.Add(new AudioInputDeviceDisplayItem(
                device,
                $"Capture device audio - {baseName}{suffix}"));
        }
        return items;
    }

    internal static IReadOnlyList<AudioInputDeviceDisplayItem> ForSource(
        IEnumerable<AudioInputDeviceDescription> devices, SourceKind kind, Guid? containerId)
    {
        if (kind != SourceKind.CaptureDevice) return [];
        var all = PrepareDisplayItems(devices);
        var matching = containerId is { } id && id != Guid.Empty
            ? all.Where(item => item.Device.ContainerId == id).Take(1).ToArray() : [];
        return matching.Length > 0 ? matching : all;
    }

    private static string DisplayBaseName(AudioInputDeviceDescription device) =>
        string.IsNullOrWhiteSpace(device.FriendlyName)
            ? "Capture audio input"
            : device.FriendlyName.Trim();
}

internal static class AudioInputDeviceEnumerator
{
    internal static List<AudioInputDeviceDescription> GetDevices()
    {
        try
        {
            return EnumerateDevices();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[audio-input] enumeration failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            return [];
        }
    }

    private static List<AudioInputDeviceDescription> EnumerateDevices()
    {
        nint enumerator = 0;
        nint collection = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CoreAudioInterop.CreateDeviceEnumerator(out enumerator));
            Marshal.ThrowExceptionForHR(CoreAudioInterop.EnumAudioEndpoints(
                enumerator,
                CoreAudioInterop.CaptureDataFlow,
                CoreAudioInterop.ActiveDeviceState,
                out collection));
            Marshal.ThrowExceptionForHR(CoreAudioInterop.GetCollectionCount(collection, out uint count));

            var devices = new List<AudioInputDeviceDescription>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                nint device = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(
                        CoreAudioInterop.GetCollectionItem(collection, index, out device));
                    Marshal.ThrowExceptionForHR(CoreAudioInterop.GetDeviceId(device, out string id));
                    string friendlyName = ReadStringProperty(
                        device,
                        CoreAudioInterop.DeviceFriendlyName);
                    devices.Add(new AudioInputDeviceDescription(id, friendlyName, ReadContainerId(device)));
                }
                finally
                {
                    CoreAudioInterop.Release(ref device);
                }
            }
            return devices;
        }
        finally
        {
            CoreAudioInterop.Release(ref collection);
            CoreAudioInterop.Release(ref enumerator);
        }
    }

    private static string ReadStringProperty(
        nint device,
        CoreAudioInterop.PropertyKey key)
    {
        CoreAudioInterop.PropVariant value = ReadProperty(device, key);
        try
        {
            const ushort VT_LPWSTR = 31;
            return value.VarType == VT_LPWSTR && value.PointerValue != 0
                ? Marshal.PtrToStringUni(value.PointerValue) ?? ""
                : "";
        }
        finally
        {
            CoreAudioInterop.ClearPropVariant(ref value);
        }
    }

    private static Guid? ReadContainerId(nint device)
    {
        CoreAudioInterop.PropVariant value = default;
        try
        {
            value = ReadProperty(device, Capture.DeviceContainerIdentity.ContainerId);
            if (value.VarType != 72 || value.PointerValue == 0) return null;
            Guid id = Marshal.PtrToStructure<Guid>(value.PointerValue);
            return id == Guid.Empty ? null : id;
        }
        catch (COMException) { return null; }
        finally { CoreAudioInterop.ClearPropVariant(ref value); }
    }

    private static CoreAudioInterop.PropVariant ReadProperty(
        nint device,
        CoreAudioInterop.PropertyKey key)
    {
        nint propertyStore = 0;
        try
        {
            Marshal.ThrowExceptionForHR(
                CoreAudioInterop.OpenPropertyStore(device, out propertyStore));
            Marshal.ThrowExceptionForHR(
                CoreAudioInterop.GetPropertyValue(propertyStore, key, out var value));
            return value;
        }
        finally
        {
            CoreAudioInterop.Release(ref propertyStore);
        }
    }
}
