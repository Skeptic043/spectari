namespace StreamHost.Capture;

internal sealed class CaptureCreationTrace
{
    private string _lastCompletedStep = "none";
    private string _currentStep = "not started";

    public CaptureCreationTrace(string targetKind)
    {
        TargetKind = targetKind;
    }

    public string TargetKind { get; }
    public string LastCompletedStep => Volatile.Read(ref _lastCompletedStep);
    public string CurrentStep => Volatile.Read(ref _currentStep);

    public void Begin(string step)
    {
        Volatile.Write(ref _currentStep, step);
        Console.WriteLine($"[preview] {TargetKind} creation entering {step}");
    }

    public void Complete(string step)
    {
        Volatile.Write(ref _lastCompletedStep, step);
        Volatile.Write(ref _currentStep, "none");
    }
}
