using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class AutoMonitorCaptureTests
{
    [Fact]
    public async Task NativeRetirementWaitsForReadWithoutBlockingShutdown()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var capture = new FakeCapture { Read = () => { entered.Set(); return resume.Wait(5000); } };
        var owner = new AutoMonitorCapture(0, _ => capture, false);
        Task read = Task.Run(() => owner.TryReadFrame([]));
        try
        {
            Assert.True(entered.Wait(5000));
            owner.Dispose();
            Assert.Equal(0, capture.Disposals);
        }
        finally { resume.Set(); await read; }
        Assert.True(SpinWait.SpinUntil(() => capture.Disposals == 1, 5000));
    }
    [Fact]
    public async Task ShutdownRetiresReplacementThatFinishesLater()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var first = new FakeCapture();
        var late = new FakeCapture();
        int calls = 0;
        var owner = new AutoMonitorCapture(0, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return first;
            entered.Set();
            Assert.True(resume.Wait(5000));
            return late;
        }, false);
        Task swap = Task.Run(() => owner.Swap(false, "synthetic replacement"));
        try
        {
            Assert.True(entered.Wait(5000));
            owner.Dispose();
            owner.Dispose();
            Assert.False(owner.TryReadFrame([]));
        }
        finally { resume.Set(); await swap; }
        Assert.True(SpinWait.SpinUntil(() => first.Disposals == 1 && late.Disposals == 1, 5000));
        Assert.Equal(0, owner.FrameVersion);
    }

    [Fact]
    public void ReplacementPreservesVersionAndRetiresEachOwnerOnce()
    {
        var first = new FakeCapture();
        var next = new FakeCapture();
        int calls = 0;
        var owner = new AutoMonitorCapture(0, _ => ++calls == 1 ? first : next, false);
        owner.Swap(false, "synthetic replacement");
        Assert.Equal(2, owner.FrameVersion);
        owner.Dispose();
        owner.Dispose();
        Assert.True(SpinWait.SpinUntil(() => first.Disposals == 1 && next.Disposals == 1, 5000));
    }

    private sealed class FakeCapture : ICaptureSource
    {
        internal int Disposals;
        internal Func<bool>? Read;
        public int Width => 1;
        public int Height => 1;
        public uint GpuVendorId => 0;
        public string AdapterName => "Synthetic";
        public string AdapterLuid => "";
        public string DriverVersion => "";
        public long FrameVersion => 1;
        public long FramesArrived => 1;
        public Exception? CaptureError => null;
        public bool CursorEnabled { set { } }
        public bool WaitForFreshFrame(long version, int timeout) => true;
        public bool TryReadFrame(byte[] buffer) => Read?.Invoke() ?? true;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
