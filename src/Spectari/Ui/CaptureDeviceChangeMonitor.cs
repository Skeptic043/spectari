using System.Runtime.InteropServices;
using Spectari.Capture;

namespace Spectari.Ui;

internal static class CaptureDeviceNotificationPolicy
{
    private const int DeviceNodesChanged = 0x0007;
    private const int DeviceArrival = 0x8000;
    private const int DeviceRemoveComplete = 0x8004;

    internal static bool RequiresRefresh(nint eventType) => eventType is
        DeviceNodesChanged or DeviceArrival or DeviceRemoveComplete;
}

/// <summary>
/// Receives Windows device-topology broadcasts and publishes capture-device
/// snapshots on the UI thread. Enumeration stays off the window message loop.
/// </summary>
internal sealed class CaptureDeviceChangeMonitor : NativeWindow, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int WmApplyDeviceSnapshot = 0x8023;

    private readonly Func<List<CaptureDeviceDescription>> _enumerateDevices;
    private readonly object _snapshotLock = new();
    private readonly nint _windowHandle;
    private IReadOnlyList<CaptureDeviceDescription>? _pendingSnapshot;
    private bool _snapshotMessagePosted;
    private int _refreshRequested;
    private int _refreshWorkerActive;
    private int _disposed;

    internal CaptureDeviceChangeMonitor()
        : this(CaptureDeviceEnumerator.GetDevices)
    {
    }

    internal CaptureDeviceChangeMonitor(Func<List<CaptureDeviceDescription>> enumerateDevices)
    {
        _enumerateDevices = enumerateDevices;
        CreateHandle(new CreateParams());
        _windowHandle = Handle;
    }

    internal event Action<IReadOnlyList<CaptureDeviceDescription>>? DevicesChanged;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmDeviceChange &&
            CaptureDeviceNotificationPolicy.RequiresRefresh(message.WParam))
        {
            RequestRefresh();
        }
        else if (message.Msg == WmApplyDeviceSnapshot)
        {
            IReadOnlyList<CaptureDeviceDescription>? snapshot = TakePendingSnapshot();
            if (snapshot is not null) DevicesChanged?.Invoke(snapshot);
        }

        base.WndProc(ref message);
    }

    private void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _refreshRequested, 1);
        StartRefreshWorker();
    }

    private void StartRefreshWorker()
    {
        if (Interlocked.CompareExchange(ref _refreshWorkerActive, 1, 0) != 0) return;
        _ = Task.Run(RefreshLoop);
    }

    private void RefreshLoop()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0 &&
                   Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                IReadOnlyList<CaptureDeviceDescription> snapshot = [.. _enumerateDevices()];
                PostSnapshot(snapshot);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[capture-device-monitor] notification refresh failed: {CleanMessage(ex.Message)}");
        }
        finally
        {
            Volatile.Write(ref _refreshWorkerActive, 0);
            if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _refreshRequested) != 0)
                StartRefreshWorker();
        }
    }

    private void PostSnapshot(IReadOnlyList<CaptureDeviceDescription> snapshot)
    {
        bool postMessage;
        lock (_snapshotLock)
        {
            if (_disposed != 0) return;
            _pendingSnapshot = snapshot;
            postMessage = !_snapshotMessagePosted;
            _snapshotMessagePosted = true;
        }

        if (!postMessage || PostMessage(_windowHandle, WmApplyDeviceSnapshot, 0, 0)) return;

        lock (_snapshotLock) _snapshotMessagePosted = false;
        if (Volatile.Read(ref _disposed) == 0)
            Console.Error.WriteLine("[capture-device-monitor] posting refreshed device list failed");
    }

    private IReadOnlyList<CaptureDeviceDescription>? TakePendingSnapshot()
    {
        lock (_snapshotLock)
        {
            IReadOnlyList<CaptureDeviceDescription>? snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            _snapshotMessagePosted = false;
            return snapshot;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DevicesChanged = null;
        lock (_snapshotLock)
        {
            _pendingSnapshot = null;
            _snapshotMessagePosted = false;
        }
        DestroyHandle();
    }

    private static string CleanMessage(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ');

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
