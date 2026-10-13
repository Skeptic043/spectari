namespace Spectari.Capture;

internal static class CaptureRetirement
{
    internal static Task Start(IDisposable capture, object? accessGate = null) => Task.Factory.StartNew(() =>
    {
        try
        {
            if (accessGate is null) capture.Dispose();
            else lock (accessGate) capture.Dispose();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[capture] retirement failed: {ex.Message}"); }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
