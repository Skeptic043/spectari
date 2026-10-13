using System.Diagnostics;
using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class ProcessAudioRebinderTests
{
    [Fact]
    public async Task SamePidIsStableAndNewPidRetiresTheOldCapture()
    {
        var target = new Target();
        var captures = new List<FakeAudio>();
        var callbacks = new List<Action<byte[], int>>();
        long bytes = 0;
        using var owner = new ProcessAudioRebinder(target, Stopwatch.GetTimestamp(),
            (_, n) => bytes += n, (pid, epoch, emit) =>
            {
                var capture = new FakeAudio(); captures.Add(capture); callbacks.Add(emit); return capture;
            });
        await owner.RebindAsync(42);
        Assert.Single(captures);
        callbacks[0](new byte[80], 80);
        await owner.RebindAsync(43);
        Assert.Equal(2, captures.Count);
        Assert.True(SpinWait.SpinUntil(() => captures[0].Disposals == 1, 5000));
        long before = bytes;
        callbacks[0](new byte[80], 80);
        Assert.Equal(before, bytes);
        callbacks[1](new byte[80], 80);
        Assert.True(bytes >= before + 80);
        owner.Dispose();
        before = bytes;
        callbacks[1](new byte[80], 80);
        Assert.Equal(before, bytes);
    }

    [Fact]
    public async Task StopWinsOverBlockedReplacementFactory()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var late = new FakeAudio();
        var owner = new ProcessAudioRebinder(new Target(), 0, (_, _) => { }, (pid, _, _) =>
        {
            if (pid == 42) return new FakeAudio();
            entered.Set();
            Assert.True(resume.Wait(5000));
            return late;
        });
        Task pending = owner.RebindAsync(43);
        try { Assert.True(entered.Wait(5000)); owner.Dispose(); }
        finally { resume.Set(); await pending; }
        Assert.True(SpinWait.SpinUntil(() => late.Disposals == 1, 5000));
    }

    [Fact]
    public async Task FailedRebindKeepsTheOldSourceAndLaterTargetCanSucceed()
    {
        Action<byte[], int>? original = null;
        int bytes = 0;
        using var owner = new ProcessAudioRebinder(new Target(), 0, (_, n) => bytes += n, (pid, _, emit) =>
        {
            if (pid == 43) throw new InvalidOperationException("synthetic failure");
            if (pid == 42) original = emit;
            return new FakeAudio();
        });
        await owner.RebindAsync(43);
        original!(new byte[80], 80);
        Assert.Equal(80, bytes);
        await owner.RebindAsync(44);
        original(new byte[80], 80);
        Assert.Equal(80, bytes);
    }

    [Fact]
    public void ReplacementPreservesSessionByteClockInsteadOfRepeatingSessionLeadIn()
    {
        long bytes = 0;
        long now = Stopwatch.Frequency * 60;
        var output = new AudioSegmentOutput(0, (_, length) => bytes += length, () => now);
        const int second = ProcessAudioCapture.SampleRate * ProcessAudioCapture.Channels * 4;
        for (int i = 0; i < 60; i++) output.Write(1, new byte[second], second);
        output.Begin(2);
        output.Write(2, new byte[second / 100], second / 100);
        Assert.Equal(60L * second + second / 100, bytes);
        now += Stopwatch.Frequency;
        output.Begin(3);
        output.Write(3, new byte[second / 100], second / 100);
        Assert.Equal(61L * second, bytes);
    }

    private sealed class Target : IWindowAudioTarget
    {
        public uint AudioProcessId => 42;
        public event Action<uint>? ProcessAttached { add { } remove { } }
    }
    private sealed class FakeAudio : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
