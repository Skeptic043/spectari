using System.Diagnostics;
using Spectari.Audio;
using Spectari.Capture;
using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class RawVideoPacerTests
{
    [Theory]
    [InlineData("read", 60)]
    [InlineData("buffer", 60)]
    [InlineData("queue", 60)]
    [InlineData("missing", 60)]
    [InlineData("repeated", 60)]
    [InlineData("read", 30)]
    [InlineData("repeated", 30)]
    public void SixSecondTimelineSurvivesRecoverableGaps(string gap, int fps)
    {
        var rig = new Rig(fps);
        rig.FirstFrame();
        long epoch = rig.Pacer.EpochTicks!.Value;
        var injected = new HashSet<long>();
        while (rig.Pacer.NextTicks < epoch + 6 * Rig.Frequency)
        {
            long frames = rig.Pacer.FramesSubmitted;
            if ((frames == fps || gap == "repeated" && (frames == 2 * fps || frames == 3 * fps)) && injected.Add(frames))
            {
                if (gap == "read") rig.Capture.NextReadDelay = 15000;
                else if (gap == "missing") rig.Capture.FailedReads = 15;
                else if (gap == "buffer") rig.Writer.BlockRentUntil = rig.Now + 15000;
                else if (gap == "queue") rig.Writer.BlockEnqueueUntil = rig.Now + 15000;
                else rig.Now += 15000;
            }
            rig.Now = Math.Max(rig.Now, rig.Pacer.NextTicks);
            RawVideoPacingResult result = rig.Pacer.Sample(default);
            Assert.NotEqual(RawVideoPacingResult.Overloaded, result);
            Assert.True(rig.Writer.Attempts < 1000);
        }
        Assert.Equal(6 * fps, rig.Pacer.FramesSubmitted);
        Assert.Equal(epoch + 6 * Rig.Frequency, rig.Pacer.NextTicks);
        Assert.Equal(6 * fps, rig.Writer.Frames.Count);
        Assert.Equal(0, rig.Writer.Rented);
        Assert.Equal(epoch, rig.Pacer.EpochTicks);
    }

    [Fact]
    public void CatchupUsesHeldPixelsThenResumesCurrentCapture()
    {
        var rig = new Rig();
        rig.FirstFrame();
        byte original = rig.Writer.Frames[0][0];
        rig.Now += 15000;
        Assert.Equal(RawVideoPacingResult.Duplicate, rig.Pacer.Sample(default));
        Assert.Equal(original, rig.Writer.Frames[1][0]);
        Assert.Equal(1, rig.Capture.Reads);
        while (rig.Pacer.NextTicks < rig.Now) rig.Pacer.Sample(default);
        rig.Now = rig.Pacer.NextTicks;
        Assert.Equal(RawVideoPacingResult.Fresh, rig.Pacer.Sample(default));
        Assert.NotEqual(original, rig.Writer.Frames[^1][0]);
    }

    [Fact]
    public void SlowSuccessfulReadsStillRefreshTheHeldImage()
    {
        var rig = new Rig();
        rig.FirstFrame();
        rig.Capture.ReadCost = 2000;
        while (rig.Pacer.FramesSubmitted < 60)
        {
            rig.Now = Math.Max(rig.Now, rig.Pacer.NextTicks);
            Assert.NotEqual(RawVideoPacingResult.Overloaded, rig.Pacer.Sample(default));
        }
        Assert.True(rig.Writer.Frames.Select(frame => frame[0]).Distinct().Count() >= 20);
        Assert.Equal(60, rig.Writer.Frames.Count);
    }

    [Fact]
    public void HalfSecondLagStopsWithoutSubmittingOrChangingEpoch()
    {
        var rig = new Rig();
        rig.FirstFrame();
        long epoch = rig.Pacer.EpochTicks!.Value;
        rig.Now = rig.Pacer.NextTicks + Rig.Frequency / 2;
        Assert.Equal(RawVideoPacingResult.Overloaded, rig.Pacer.Sample(default));
        Assert.Single(rig.Writer.Frames);
        Assert.Equal(epoch, rig.Pacer.EpochTicks);
        Assert.Null(StreamController.PlanCpuRecovery(RawVideoPacer.OverloadReason,
            new SessionConfig { Encoder = "auto" }, 1920, 1080, false, false));
    }

    [Fact]
    public void PersistentSmallBacklogStopsAtRecoveryDeadline()
    {
        var rig = new Rig();
        rig.FirstFrame();
        long began = rig.Pacer.NextTicks + 6000;
        rig.Now = began;
        rig.Writer.EnqueueCost = 1000;
        RawVideoPacingResult result;
        do { result = rig.Pacer.Sample(default); }
        while (result != RawVideoPacingResult.Overloaded && rig.Now < began + 3 * Rig.Frequency);
        Assert.Equal(RawVideoPacingResult.Overloaded, result);
        Assert.InRange(rig.Now - began, 2 * Rig.Frequency, 2 * Rig.Frequency + 1000);
        Assert.True(rig.Now - rig.Pacer.NextTicks < Rig.Frequency / 2);
        Assert.Equal(0, rig.Writer.Rented);
    }

    [Fact]
    public void SustainedWriterOverloadStopsRatherThanDroppingTime()
    {
        var rig = new Rig();
        rig.FirstFrame();
        rig.Writer.EnqueueCost = 2000;
        RawVideoPacingResult result;
        do
        {
            rig.Now = Math.Max(rig.Now, rig.Pacer.NextTicks);
            result = rig.Pacer.Sample(default);
        }
        while (result != RawVideoPacingResult.Overloaded && rig.Pacer.FramesSubmitted < 100);
        Assert.Equal(RawVideoPacingResult.Overloaded, result);
        Assert.InRange(rig.Pacer.FramesSubmitted, 29, 33);
    }

    [Theory]
    [InlineData("rent")]
    [InlineData("read")]
    [InlineData("queue")]
    [InlineData("closed-queue")]
    public void CancellationReturnsRentedBufferAndFencesLateResults(string boundary)
    {
        using var stop = new CancellationTokenSource();
        var rig = new Rig();
        rig.Now = rig.Pacer.NextTicks;
        if (boundary == "rent") rig.Writer.OnRent = stop.Cancel;
        if (boundary == "read") rig.Capture.OnRead = stop.Cancel;
        if (boundary == "queue") rig.Writer.OnEnqueue = stop.Cancel;
        if (boundary == "closed-queue") rig.Writer.OnEnqueue = () =>
        {
            stop.Cancel();
            throw new InvalidOperationException("synthetic downstream shutdown");
        };
        Assert.Equal(RawVideoPacingResult.Cancelled, rig.Pacer.Sample(stop.Token));
        Assert.Equal(0, rig.Writer.Rented);
        Assert.Empty(rig.Writer.Frames);
        Assert.Null(rig.Pacer.EpochTicks);
    }

    [Fact]
    public void EpochWaitsForSuccessfulEnqueueAndLateAudioUsesIt()
    {
        var rig = new Rig();
        rig.Capture.FailedReads = 1;
        rig.Now = rig.Pacer.NextTicks;
        Assert.Equal(RawVideoPacingResult.Waiting, rig.Pacer.Sample(default));
        Assert.Null(rig.Pacer.EpochTicks);
        rig.Writer.BlockEnqueueUntil = 20000;
        rig.Now = rig.Pacer.NextTicks;
        Assert.Equal(RawVideoPacingResult.Waiting, rig.Pacer.Sample(default));
        Assert.Null(rig.Pacer.EpochTicks);
        rig.Now = 20000;
        Assert.Equal(RawVideoPacingResult.Fresh, rig.Pacer.Sample(default));
        long epoch = rig.Pacer.EpochTicks!.Value;
        Assert.Equal(20000, epoch);
        long audioEpoch = epoch * Stopwatch.Frequency / Rig.Frequency;
        Assert.Equal(48000, ProcessAudioCapture.GetLeadInFrames(audioEpoch, audioEpoch + Stopwatch.Frequency));
        rig.Now = rig.Pacer.NextTicks;
        rig.Pacer.Sample(default);
        Assert.Equal(epoch, rig.Pacer.EpochTicks);
    }

    [Fact]
    public void ScheduleDoesNotAccumulateIntegerPeriodRounding()
    {
        var rig = new Rig(fps: 59);
        rig.FirstFrame();
        long epoch = rig.Pacer.EpochTicks!.Value;
        while (rig.Pacer.FramesSubmitted < 59 * 60)
        {
            rig.Now = rig.Pacer.NextTicks;
            rig.Pacer.Sample(default);
        }
        Assert.Equal(epoch + 60 * Rig.Frequency, rig.Pacer.NextTicks);
    }

    private sealed class Rig
    {
        internal const long Frequency = 60000;
        internal long Now;
        internal readonly Source Capture;
        internal readonly Writer Writer;
        internal readonly RawVideoPacer Pacer;
        internal Rig(int fps = 60)
        {
            Capture = new Source(this);
            Writer = new Writer(this);
            Pacer = new RawVideoPacer(Capture, Writer, fps, _ => { }, () => Now, Frequency);
        }
        internal void FirstFrame()
        {
            Now = Pacer.NextTicks;
            Assert.Equal(RawVideoPacingResult.Fresh, Pacer.Sample(default));
        }
    }

    private sealed class Writer(Rig rig) : IRawFrameWriter
    {
        internal readonly List<byte[]> Frames = [];
        internal long BlockRentUntil, BlockEnqueueUntil, EnqueueCost;
        internal int Rented, Attempts;
        internal Action? OnRent, OnEnqueue;
        public byte[]? TryRentBuffer(int timeoutMs, CancellationToken ct)
        {
            Assert.Equal(50, timeoutMs);
            Attempts++;
            if (rig.Now < BlockRentUntil) { rig.Now += 3000; return null; }
            Rented++;
            OnRent?.Invoke();
            return new byte[4];
        }
        public bool TryEnqueue(byte[] buffer, int timeoutMs, CancellationToken ct)
        {
            Assert.Equal(50, timeoutMs);
            OnEnqueue?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (rig.Now < BlockEnqueueUntil) { rig.Now += 3000; return false; }
            rig.Now += EnqueueCost;
            Frames.Add([.. buffer]);
            Rented--;
            return true;
        }
        public void ReturnUnused(byte[] buffer) => Rented--;
    }

    private sealed class Source(Rig rig) : ICaptureSource
    {
        internal int Reads, FailedReads;
        internal long NextReadDelay, ReadCost;
        internal Action? OnRead;
        public int Width => 1;
        public int Height => 1;
        public uint GpuVendorId => 0;
        public string AdapterName => "Synthetic";
        public string AdapterLuid => "";
        public string DriverVersion => "";
        public long FrameVersion { get; private set; }
        public long FramesArrived => FrameVersion;
        public Exception? CaptureError => null;
        public bool CursorEnabled { set { } }
        public bool WaitForFreshFrame(long version, int timeout) { FrameVersion++; return true; }
        public bool TryReadFrame(byte[] buffer)
        {
            Reads++;
            rig.Now += NextReadDelay + ReadCost;
            NextReadDelay = 0;
            OnRead?.Invoke();
            if (FailedReads > 0) { FailedReads--; return false; }
            Array.Fill(buffer, (byte)(Reads % 256));
            return true;
        }
        public void Dispose() { }
    }
}
