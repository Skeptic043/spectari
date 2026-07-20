using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Spectari.Capture;

internal static class CaptureBorderSuppression
{
    private const string CaptureAccessType = "Windows.Graphics.Capture.GraphicsCaptureAccess";
    private const string CaptureSessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string BorderProperty = "IsBorderRequired";

    private static readonly Lazy<Task<AppCapabilityAccessStatus>> AccessRequest = new(
        () => GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static int _accessResultLogged;
    private static int _applyFailureLogged;

    internal static void TryDisable(GraphicsCaptureSession session)
    {
        bool supported;
        try
        {
            supported = ApiInformation.IsTypePresent(CaptureAccessType)
                && ApiInformation.IsPropertyPresent(CaptureSessionType, BorderProperty);
        }
        catch (Exception ex)
        {
            LogAccessResultOnce(
                $"borderless support check failed (HRESULT 0x{ex.HResult:X8}): {SingleLine(ex.Message)}; capture continues with the border");
            return;
        }

        if (!supported)
        {
            LogAccessResultOnce("border suppression is not supported by this Windows version; capture continues with the border");
            return;
        }

        string accessOutcome;
        try
        {
            AppCapabilityAccessStatus status = AccessRequest.Value.GetAwaiter().GetResult();
            accessOutcome = $"borderless access was {status}";
        }
        catch (Exception ex)
        {
            accessOutcome =
                $"borderless access request failed (HRESULT 0x{ex.HResult:X8}): {SingleLine(ex.Message)}";
        }

        try
        {
            session.IsBorderRequired = false;
            LogAccessResultOnce($"{accessOutcome}; capture border suppression requested");
        }
        catch (Exception ex)
        {
            LogAccessResultOnce($"{accessOutcome}; capture continues with the border");
            if (Interlocked.Exchange(ref _applyFailureLogged, 1) == 0)
            {
                Console.WriteLine(
                    $"[capture-border] applying border suppression failed (HRESULT 0x{ex.HResult:X8}): {SingleLine(ex.Message)}; capture continues with the border");
            }
        }
    }

    private static void LogAccessResultOnce(string message)
    {
        if (Interlocked.Exchange(ref _accessResultLogged, 1) == 0)
            Console.WriteLine($"[capture-border] {message}");
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}
