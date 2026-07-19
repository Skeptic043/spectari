using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Spectari.Ui;

internal sealed class WatchAudioSessionConsolidator : IDisposable
{
    private readonly CoreWebView2Environment _environment;
    private readonly CoreWebView2 _webView;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1_000 };
    private bool _disposed;
    private bool _failureLogged;

    internal WatchAudioSessionConsolidator(
        CoreWebView2Environment environment,
        CoreWebView2 webView)
    {
        _environment = environment;
        _webView = webView;
        _refreshTimer.Tick += OnRefreshTimerTick;
        _environment.ProcessInfosChanged += OnProcessInfosChanged;
        _webView.IsDocumentPlayingAudioChanged += OnDocumentPlayingAudioChanged;

        RefreshSessions();
        UpdateRefreshTimer();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _environment.ProcessInfosChanged -= OnProcessInfosChanged;
        _webView.IsDocumentPlayingAudioChanged -= OnDocumentPlayingAudioChanged;
        _refreshTimer.Dispose();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e) => RefreshSessions();

    private void OnProcessInfosChanged(object? sender, object e) => RefreshSessions();

    private void OnDocumentPlayingAudioChanged(object? sender, object e)
    {
        RefreshSessions();
        UpdateRefreshTimer();
    }

    private void UpdateRefreshTimer()
    {
        if (_disposed) return;
        if (_webView.IsDocumentPlayingAudio)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void RefreshSessions()
    {
        if (_disposed) return;

        try
        {
            var processIds = _environment.GetProcessInfos()
                .Select(info => checked((uint)info.ProcessId))
                .Append(checked((uint)Environment.ProcessId))
                .ToHashSet();
            WindowsAudioSessionConsolidator.Apply(processIds);
        }
        catch (Exception ex)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            Console.Error.WriteLine(
                $"[watch-audio] volume mixer session consolidation failed: {ex.Message}");
        }
    }
}

internal static class WindowsAudioSessionConsolidator
{
    private const int RenderDataFlow = 0;
    private const uint ActiveDeviceState = 0x00000001;
    private const uint ClassContextAll = 0x00000017;
    private const string DisplayName = "Spectari";

    private static readonly Guid AudioSessionManager2Id =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid GroupingId =
        new("29B61C75-C3BD-4D42-8915-921A34E4896F");
    private static readonly Guid EventContext =
        new("4BFBB6E4-689A-4C6D-9A7F-1D16F15EE640");

    internal static void Apply(IReadOnlySet<uint> processIds)
    {
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(deviceEnumerator.EnumAudioEndpoints(
                RenderDataFlow,
                ActiveDeviceState,
                out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out uint deviceCount));

            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                object? managerObject = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    Marshal.ThrowExceptionForHR(devices.Item(deviceIndex, out device));
                    Guid managerId = AudioSessionManager2Id;
                    Marshal.ThrowExceptionForHR(device.Activate(
                        ref managerId,
                        ClassContextAll,
                        IntPtr.Zero,
                        out managerObject));
                    var manager = (IAudioSessionManager2)managerObject;
                    Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                    Marshal.ThrowExceptionForHR(sessions.GetCount(out int sessionCount));

                    for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                        ApplyToSession(sessions, sessionIndex, processIds);
                }
                finally
                {
                    Release(sessions);
                    Release(managerObject);
                    Release(device);
                }
            }
        }
        finally
        {
            Release(devices);
            Release(deviceEnumerator);
        }
    }

    private static void ApplyToSession(
        IAudioSessionEnumerator sessions,
        int sessionIndex,
        IReadOnlySet<uint> processIds)
    {
        IAudioSessionControl? control = null;
        try
        {
            Marshal.ThrowExceptionForHR(sessions.GetSession(sessionIndex, out control));
            var control2 = (IAudioSessionControl2)control;
            int processIdHr = control2.GetProcessId(out uint processId);
            if (processIdHr < 0 || !processIds.Contains(processId)) return;

            Guid eventContext = EventContext;
            Marshal.ThrowExceptionForHR(control2.SetDisplayName(DisplayName, ref eventContext));

            Guid groupingId = GroupingId;
            eventContext = EventContext;
            Marshal.ThrowExceptionForHR(control2.SetGroupingParam(ref groupingId, ref eventContext));
        }
        finally
        {
            Release(control);
        }
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { _ = Marshal.FinalReleaseComObject(value); } catch { }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(
            int dataFlow,
            uint stateMask,
            out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(
            ref Guid sessionId,
            uint streamFlags,
            out IAudioSessionControl sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(
            ref Guid sessionId,
            uint streamFlags,
            out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(
            [MarshalAs(UnmanagedType.LPWStr)] string sessionId,
            IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr displayName);
        [PreserveSig] int SetDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref Guid eventContext);
        [PreserveSig] int GetIconPath(out IntPtr iconPath);
        [PreserveSig] int SetIconPath(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr displayName);
        [PreserveSig] int SetDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref Guid eventContext);
        [PreserveSig] int GetIconPath(out IntPtr iconPath);
        [PreserveSig] int SetIconPath(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier(out IntPtr sessionId);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr sessionInstanceId);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
