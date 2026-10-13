using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class WindowCandidateRecoveryTests
{
    [Fact]
    public void SameIdentityRecoversAfterFirstFrameTimeoutAndCooldown()
    {
        long now = 0;
        var recovery = new WindowCandidateRecovery(() => now);
        var identity = new WindowIdentity((IntPtr)1, 42);
        var failed = new Candidate(() => now += 100);
        Assert.Null(recovery.Prepare(identity, () => failed, _ => false, true, default));
        Assert.True(SpinWait.SpinUntil(() => failed.Disposals == 1, 5000));
        Assert.False(recovery.CanTry(identity));
        now += 1999;
        Assert.False(recovery.CanTry(identity));
        now++;
        var ready = new Candidate(() => { }) { Version = 1 };
        Assert.Same(ready, recovery.Prepare(identity, () => ready, _ => false, true, default));
        Assert.True(recovery.CanTry(identity));
        Assert.Equal(0, ready.Disposals);
        ready.Dispose();
    }

    [Fact]
    public void RepeatedFailureBacksOffAndCancellationDoesNotCreate()
    {
        long now = 0;
        int attempts = 0;
        var recovery = new WindowCandidateRecovery(() => now);
        var identity = new WindowIdentity((IntPtr)1, 42);
        Candidate Create() { attempts++; throw new InvalidOperationException("synthetic failure"); }
        Assert.Null(recovery.Prepare(identity, Create, _ => false, true, default));
        Assert.Null(recovery.Prepare(identity, Create, _ => false, true, default));
        Assert.Equal(1, attempts);
        now = 2000;
        Assert.Null(recovery.Prepare(identity, Create, _ => false, true, default));
        now = 5999;
        Assert.False(recovery.CanTry(identity));
        now = 6000;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Null(recovery.Prepare(identity, Create, _ => false, true, stop.Token));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void StopDuringFirstFrameWaitRetiresCandidate()
    {
        using var stop = new CancellationTokenSource();
        var candidate = new Candidate(stop.Cancel);
        var recovery = new WindowCandidateRecovery(() => 0);
        var identity = new WindowIdentity((IntPtr)1, 42);
        Assert.Null(recovery.Prepare(identity, () => candidate, _ => false, true, stop.Token));
        Assert.True(SpinWait.SpinUntil(() => candidate.Disposals == 1, 5000));
        Assert.True(recovery.CanTry(identity));
    }

    private sealed class Candidate(Action wait) : ICaptureSource
    {
        internal int Disposals;
        internal long Version;
        public int Width => 1;
        public int Height => 1;
        public uint GpuVendorId => 0;
        public string AdapterName => "Synthetic";
        public string AdapterLuid => "";
        public string DriverVersion => "";
        public long FrameVersion => Version;
        public long FramesArrived => Version;
        public Exception? CaptureError => null;
        public bool CursorEnabled { set { } }
        public bool WaitForFreshFrame(long version, int timeout) { wait(); return Version > version; }
        public bool TryReadFrame(byte[] buffer) => true;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
