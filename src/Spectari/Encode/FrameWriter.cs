using System.Collections.Concurrent;
using System.Diagnostics;

namespace Spectari.Encode;

internal readonly record struct FrameWriterProgress(
    long FramesEnqueued,
    long WritesStarted,
    long WritesCompleted,
    long LastEnqueueTicks,
    long LastWriteStartedTicks,
    long LastWriteCompletedTicks,
    bool WriteInProgress,
    bool Failed);

/// <summary>
/// The fixed buffer pool bounds pending rawvideo memory. Pipe writes belong
/// to a separate thread so the sampler can supervise back-pressure.
/// </summary>
public sealed class FrameWriter : IDisposable, IRawFrameWriter
{
    private readonly BlockingCollection<byte[]> _free = new();
    private readonly BlockingCollection<byte[]> _pending = new(boundedCapacity: 3);
    private readonly Thread _thread;
    private readonly FfmpegEncoder _encoder;
    private volatile bool _failed;
    private long _framesEnqueued;
    private long _writesStarted;
    private long _writesCompleted;
    private long _lastEnqueueTicks;
    private long _lastWriteStartedTicks;
    private long _lastWriteCompletedTicks;
    private int _writeInProgress;

    public bool Failed => _failed;

    public FrameWriter(FfmpegEncoder encoder, int frameBytes, int bufferCount = 4)
    {
        _encoder = encoder;
        for (int i = 0; i < bufferCount; i++) _free.Add(new byte[frameBytes]);
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "ffmpeg-writer" };
        _thread.Start();
    }

    public byte[]? TryRentBuffer(int timeoutMs, CancellationToken ct) =>
        _free.TryTake(out var buffer, timeoutMs, ct) ? buffer : null;

    public bool TryEnqueue(byte[] buffer, int timeoutMs, CancellationToken ct)
    {
        if (!_pending.TryAdd(buffer, timeoutMs, ct)) return false;
        Interlocked.Increment(ref _framesEnqueued);
        Interlocked.Exchange(ref _lastEnqueueTicks, Stopwatch.GetTimestamp());
        return true;
    }

    public void ReturnUnused(byte[] buffer) => _free.Add(buffer);

    internal FrameWriterProgress GetProgressSnapshot() => new(
        Interlocked.Read(ref _framesEnqueued),
        Interlocked.Read(ref _writesStarted),
        Interlocked.Read(ref _writesCompleted),
        Interlocked.Read(ref _lastEnqueueTicks),
        Interlocked.Read(ref _lastWriteStartedTicks),
        Interlocked.Read(ref _lastWriteCompletedTicks),
        Volatile.Read(ref _writeInProgress) != 0,
        _failed);

    private void WriteLoop()
    {
        try
        {
            foreach (var buffer in _pending.GetConsumingEnumerable())
            {
                Interlocked.Increment(ref _writesStarted);
                Interlocked.Exchange(ref _lastWriteStartedTicks, Stopwatch.GetTimestamp());
                Volatile.Write(ref _writeInProgress, 1);
                try
                {
                    _encoder.WriteFrame(buffer, buffer.Length);
                    Interlocked.Increment(ref _writesCompleted);
                    Interlocked.Exchange(ref _lastWriteCompletedTicks, Stopwatch.GetTimestamp());
                }
                finally
                {
                    Volatile.Write(ref _writeInProgress, 0);
                }
                _free.Add(buffer);
            }
        }
        catch (Exception)
        {
            _failed = true; // ffmpeg died or pipe closed; Program notices via Failed/HasExited
        }
    }

    public void Dispose()
    {
        _pending.CompleteAdding();
        _thread.Join(2000);
    }
}
