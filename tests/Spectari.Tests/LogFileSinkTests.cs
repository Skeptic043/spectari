using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class LogFileSinkTests
{
    private static (LogFileSink Sink, StringWriter Output) NewSink(long maxBytes = 1_000_000)
    {
        var output = new StringWriter { NewLine = "\n" };
        return (new LogFileSink(output, maxBytes), output);
    }

    private static string[] Lines(StringWriter output) =>
        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void RepeatedLinesAreCollapsedIntoACount()
    {
        var (sink, output) = NewSink();

        sink.Write("10:00:00", "[ws] viewer 1 fell behind");
        sink.Write("10:00:01", "[ws] viewer 1 fell behind");
        sink.Write("10:00:02", "[ws] viewer 1 fell behind");
        sink.Write("10:00:03", "[ws] viewer connected");

        Assert.Equal(
            ["10:00:00 [ws] viewer 1 fell behind", "10:00:03 [log] previous line repeated 2 more times",
             "10:00:03 [ws] viewer connected"],
            Lines(output));
    }

    [Fact]
    public void PendingRepeatsAreWrittenOnFlush()
    {
        var (sink, output) = NewSink();

        sink.Write("10:00:00", "same");
        sink.Write("10:00:01", "same");
        sink.Flush("10:00:02");

        Assert.Equal(["10:00:00 same", "10:00:02 [log] previous line repeated 1 more time"], Lines(output));
    }

    [Fact]
    public void FileStopsAtItsSizeCapWithOneNotice()
    {
        var (sink, output) = NewSink(maxBytes: 60);

        for (int i = 0; i < 10; i++) sink.Write("10:00:00", $"line {i} with some text");

        string[] lines = Lines(output);
        Assert.Equal("10:00:00 [log] log file size limit reached; later lines are not saved", lines[^1]);
        Assert.Single(lines, line => line.Contains("size limit"));
        Assert.True(lines.Length < 10);
    }
}
