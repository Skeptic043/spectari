using System.Collections.Concurrent;

namespace Spectari.Util;

/// <summary>
/// Writes log lines to the log file: consecutive repeats collapse into one
/// count line, and the file stops growing at its size cap. Not thread-safe;
/// <see cref="BackgroundLogWriter"/> is its only caller.
/// </summary>
internal sealed class LogFileSink(TextWriter writer, long maxBytes)
{
    internal const long DefaultMaxBytes = 20 * 1024 * 1024;

    private string? _lastLine;
    private int _repeats;
    private long _bytes;
    private bool _full;

    internal void Write(string time, string line)
    {
        if (line == _lastLine)
        {
            _repeats++;
            return;
        }
        WriteRepeats(time);
        _lastLine = line;
        Emit(time, line);
    }

    internal void Flush(string time)
    {
        WriteRepeats(time);
        writer.Flush();
    }

    private void WriteRepeats(string time)
    {
        if (_repeats == 0) return;
        string times = _repeats == 1 ? "1 more time" : $"{_repeats} more times";
        _repeats = 0;
        Emit(time, $"[log] previous line repeated {times}");
    }

    private void Emit(string time, string line)
    {
        if (_full) return;
        string text = $"{time} {line}";
        if (_bytes + text.Length > maxBytes)
        {
            _full = true;
            text = $"{time} [log] log file size limit reached; later lines are not saved";
        }
        writer.WriteLine(text);
        _bytes += text.Length + 1;
    }
}

/// <summary>
/// Moves log file writes off the calling thread, so a slow disk never stalls
/// capture or streaming. If the queue fills, lines are dropped and counted.
/// </summary>
internal sealed class BackgroundLogWriter
{
    private const int QueueLimit = 10_000;
    private readonly BlockingCollection<(string Time, string Line)> _queue = new(QueueLimit);
    private readonly LogFileSink _sink;
    private readonly ManualResetEventSlim _idle = new(true);
    private int _dropped;

    internal BackgroundLogWriter(LogFileSink sink)
    {
        _sink = sink;
        new Thread(Run) { IsBackground = true, Name = "Spectari log writer" }.Start();
    }

    internal void Enqueue(string line)
    {
        _idle.Reset();
        if (!_queue.TryAdd((DateTime.Now.ToString("HH:mm:ss"), line))) Interlocked.Increment(ref _dropped);
    }

    /// <summary>Waits briefly for queued lines to reach the file (exit and crash paths).</summary>
    internal void Drain(TimeSpan timeout) => _idle.Wait(timeout);

    private void Run()
    {
        foreach ((string time, string line) in _queue.GetConsumingEnumerable())
        {
            try
            {
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) _sink.Write(time, $"[log] {dropped} lines were dropped because logging fell behind");
                _sink.Write(time, line);
                if (_queue.Count == 0)
                {
                    _sink.Flush(time);
                    _idle.Set();
                }
            }
            catch
            {
                // The log file is best-effort diagnostics; a failed write must not stop the app.
            }
        }
    }
}
