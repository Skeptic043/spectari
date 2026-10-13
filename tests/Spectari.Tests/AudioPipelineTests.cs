using System.Diagnostics;
using Spectari.Audio;
using Xunit;

namespace Spectari.Tests;

public sealed class AudioPipelineTests
{
    [Theory]
    [InlineData(42u, false, null, "process:42")]
    [InlineData(42u, false, "", "process:42")]
    [InlineData(0u, true, null, "desktop")]
    [InlineData(0u, true, "", "desktop")]
    [InlineData(0u, false, "capture-input-id", "input:capture-input-id")]
    public void StartupCreatesOnlyTheSelectedCapture(
        uint pid, bool desktop, string? inputId, string expected)
    {
        using AudioPipeline pipeline = Assert.IsType<AudioPipeline>(
            AudioPipeline.Create(pid, desktop, inputId, 8093));
        var calls = new List<string>();
        using var capture = new FakeCapture();

        IDisposable Record(string selection)
        {
            calls.Add(selection);
            return capture;
        }

        IDisposable selected = pipeline.CreateCapture(
            () => Record("desktop"),
            id => Record($"input:{id}"),
            processId => Record($"process:{processId}"));

        Assert.Same(capture, selected);
        Assert.Equal(expected, Assert.Single(calls));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AbsentInputAndNoOtherModeProducesNoPipeline(string? inputId)
    {
        Assert.Null(AudioPipeline.Create(0, false, inputId, 8093));
    }

    [Fact]
    public void ZeroAudioPidProducesNoPipeline()
    {
        Assert.Null(AudioPipeline.Create(0, false, 8093));
    }

    [Fact]
    public void DesktopModeProducesPipelineWithoutProcessId()
    {
        using AudioPipeline pipeline = Assert.IsType<AudioPipeline>(
            AudioPipeline.Create(0, true, 8093));

        Assert.Equal("spectari_audio_8093", pipeline.PipeName);
    }

    [Fact]
    public void CaptureInputModeProducesPipelineWithoutProcessId()
    {
        using AudioPipeline pipeline = Assert.IsType<AudioPipeline>(
            AudioPipeline.Create(0, false, "capture-input-id", 8093));

        Assert.Equal("spectari_audio_8093", pipeline.PipeName);
    }

    [Fact]
    public void MultipleAudioModesAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(42, false, "capture-input-id", 8093));
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(0, true, "capture-input-id", 8093));
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(42, true, null, 8093));
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(42, true, "", 8093));
        Assert.Throws<ArgumentException>(() =>
            new AudioPipeline(42, true, "capture-input-id", 8093));
    }

    [Fact]
    public void PipeNameIncludesTheConfiguredPort()
    {
        Assert.Equal("spectari_audio_8093", AudioPipeline.FormatPipeName(8093));
    }

    [Fact]
    public void LeadInFramesMatchOneSecondBetweenEpochAndFallback()
    {
        const long videoEpochTicks = 1234;
        long fallbackStartTicks = videoEpochTicks + Stopwatch.Frequency;

        long frames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, fallbackStartTicks);

        Assert.Equal(ProcessAudioCapture.SampleRate, frames);
    }

    private sealed class FakeCapture : IDisposable
    {
        public void Dispose() { }
    }
}
