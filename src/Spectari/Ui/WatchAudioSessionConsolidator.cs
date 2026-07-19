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

internal static unsafe class WindowsAudioSessionConsolidator
{
    private const int RenderDataFlow = 0;
    private const uint ActiveDeviceState = 0x00000001;
    private const uint ClassContextAll = 0x00000017;
    private const string DisplayName = "Spectari";

    private static readonly Guid MMDeviceEnumeratorClassId =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorId =
        new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioSessionManager2Id =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid AudioSessionControl2Id =
        new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid GroupingId =
        new("29B61C75-C3BD-4D42-8915-921A34E4896F");
    private static readonly Guid EventContext =
        new("4BFBB6E4-689A-4C6D-9A7F-1D16F15EE640");

    internal static void Apply(IReadOnlySet<uint> processIds)
    {
        nint deviceEnumerator = 0;
        nint devices = 0;
        try
        {
            Guid classId = MMDeviceEnumeratorClassId;
            Guid interfaceId = MMDeviceEnumeratorId;
            Marshal.ThrowExceptionForHR(CoCreateInstance(
                ref classId,
                0,
                ClassContextAll,
                ref interfaceId,
                out deviceEnumerator));
            Marshal.ThrowExceptionForHR(EnumAudioEndpoints(
                deviceEnumerator,
                RenderDataFlow,
                ActiveDeviceState,
                out devices));
            Marshal.ThrowExceptionForHR(GetCollectionCount(devices, out uint deviceCount));

            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                nint device = 0;
                nint manager = 0;
                nint sessions = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(GetCollectionItem(
                        devices,
                        deviceIndex,
                        out device));
                    Marshal.ThrowExceptionForHR(Activate(
                        device,
                        AudioSessionManager2Id,
                        ClassContextAll,
                        out manager));
                    Marshal.ThrowExceptionForHR(GetSessionEnumerator(manager, out sessions));
                    Marshal.ThrowExceptionForHR(GetSessionCount(sessions, out int sessionCount));

                    for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                        ApplyToSession(sessions, sessionIndex, processIds);
                }
                finally
                {
                    Release(sessions);
                    Release(manager);
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
        nint sessions,
        int sessionIndex,
        IReadOnlySet<uint> processIds)
    {
        nint control = 0;
        nint control2 = 0;
        try
        {
            Marshal.ThrowExceptionForHR(GetSession(sessions, sessionIndex, out control));
            Marshal.ThrowExceptionForHR(QueryInterface(
                control,
                AudioSessionControl2Id,
                out control2));

            int processIdHr = GetProcessId(control2, out uint processId);
            if (processIdHr < 0 || !processIds.Contains(processId)) return;

            Marshal.ThrowExceptionForHR(SetDisplayName(control2, DisplayName, EventContext));
            Marshal.ThrowExceptionForHR(SetGroupingParam(control2, GroupingId, EventContext));
        }
        finally
        {
            Release(control2);
            Release(control);
        }
    }

    private static int EnumAudioEndpoints(
        nint deviceEnumerator,
        int dataFlow,
        uint stateMask,
        out nint devices)
    {
        nint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)
            GetMethod(deviceEnumerator, 3);
        int hr = method(deviceEnumerator, dataFlow, stateMask, &result);
        devices = result;
        return hr;
    }

    private static int GetCollectionCount(nint devices, out uint count)
    {
        uint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, uint*, int>)GetMethod(devices, 3);
        int hr = method(devices, &result);
        count = result;
        return hr;
    }

    private static int GetCollectionItem(nint devices, uint index, out nint device)
    {
        nint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)
            GetMethod(devices, 4);
        int hr = method(devices, index, &result);
        device = result;
        return hr;
    }

    private static int Activate(
        nint device,
        Guid interfaceId,
        uint classContext,
        out nint instance)
    {
        nint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)
            GetMethod(device, 3);
        int hr = method(device, &interfaceId, classContext, 0, &result);
        instance = result;
        return hr;
    }

    private static int GetSessionEnumerator(nint manager, out nint sessions)
    {
        nint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(manager, 5);
        int hr = method(manager, &result);
        sessions = result;
        return hr;
    }

    private static int GetSessionCount(nint sessions, out int count)
    {
        int result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetMethod(sessions, 3);
        int hr = method(sessions, &result);
        count = result;
        return hr;
    }

    private static int GetSession(nint sessions, int index, out nint control)
    {
        nint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)GetMethod(sessions, 4);
        int hr = method(sessions, index, &result);
        control = result;
        return hr;
    }

    private static int QueryInterface(nint instance, Guid interfaceId, out nint result)
    {
        nint value = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
            GetMethod(instance, 0);
        int hr = method(instance, &interfaceId, &value);
        result = value;
        return hr;
    }

    private static int GetProcessId(nint control, out uint processId)
    {
        uint result = 0;
        var method = (delegate* unmanaged[Stdcall]<nint, uint*, int>)GetMethod(control, 14);
        int hr = method(control, &result);
        processId = result;
        return hr;
    }

    private static int SetDisplayName(nint control, string displayName, Guid eventContext)
    {
        var method = (delegate* unmanaged[Stdcall]<nint, char*, Guid*, int>)
            GetMethod(control, 5);
        fixed (char* displayNamePointer = displayName)
            return method(control, displayNamePointer, &eventContext);
    }

    private static int SetGroupingParam(nint control, Guid groupingId, Guid eventContext)
    {
        var method = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)
            GetMethod(control, 9);
        return method(control, &groupingId, &eventContext);
    }

    private static nint GetMethod(nint instance, int slot) => (*(nint**)instance)[slot];

    private static void Release(nint instance)
    {
        if (instance == 0) return;
        var method = (delegate* unmanaged[Stdcall]<nint, uint>)GetMethod(instance, 2);
        _ = method(instance);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        nint outer,
        uint classContext,
        ref Guid interfaceId,
        out nint instance);
}
