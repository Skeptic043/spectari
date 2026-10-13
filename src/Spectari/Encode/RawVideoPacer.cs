using System.Diagnostics;
using Spectari.Capture;

namespace Spectari.Encode;

internal interface IRawFrameWriter
{
    byte[]? TryRentBuffer(int timeoutMs, CancellationToken ct);
    bool TryEnqueue(byte[] buffer, int timeoutMs, CancellationToken ct);
    void ReturnUnused(byte[] buffer);
}

internal enum RawVideoPacingResult { Waiting, Fresh, Duplicate, Cancelled, Overloaded }

internal sealed class RawVideoPacer
{
    internal const string OverloadReason = "video timing overload; lower the frame rate or resolution";
    private const int WriterWaitMs = 50;
    private readonly ICaptureSource _capture;
    private readonly IRawFrameWriter _writer;
    private readonly Func<long> _clock;
    private readonly Action<string> _stage;
    private readonly long _frequency;
    private readonly int _fps;
    private readonly byte[] _heldFrame;
    private long _startupNext;
    private long _lastVersion;
    private long? _recoverySince;
    private bool _hasFrame;

    internal long? EpochTicks { get; private set; }
    internal long FramesSubmitted { get; private set; }
    internal long NextTicks => EpochTicks is { } epoch
        ? epoch + FramesSubmitted / _fps * _frequency + FramesSubmitted % _fps * _frequency / _fps
        : _startupNext;

    internal RawVideoPacer(ICaptureSource capture, IRawFrameWriter writer, int fps,
        Action<string> stage, Func<long>? clock = null, long? frequency = null)
    {
        _capture = capture;
        _writer = writer;
        _fps = fps > 0 ? fps : throw new ArgumentOutOfRangeException(nameof(fps));
        _clock = clock ?? Stopwatch.GetTimestamp;
        _frequency = frequency ?? Stopwatch.Frequency;
        if (_frequency < _fps) throw new ArgumentOutOfRangeException(nameof(frequency));
        _stage = stage;
        _heldFrame = new byte[checked(capture.Width * capture.Height * 4)];
        _startupNext = _clock() + _frequency / _fps;
    }

    internal RawVideoPacingResult Sample(CancellationToken ct)
    {
        byte[]? buffer = null;
        try
        {
            if (ct.IsCancellationRequested) return RawVideoPacingResult.Cancelled;
            if (IsOverloaded()) return RawVideoPacingResult.Overloaded;
            if (_clock() < NextTicks) return RawVideoPacingResult.Waiting;
            _stage("waiting-for-frame-buffer");
            buffer = _writer.TryRentBuffer(WriterWaitMs, ct);
            if (ct.IsCancellationRequested) return RawVideoPacingResult.Cancelled;
            if (IsOverloaded()) return RawVideoPacingResult.Overloaded;
            if (buffer is null) return RawVideoPacingResult.Waiting;

            bool fresh = false;
            bool ready = false;
            if (!ShouldHoldFrame())
            {
                _stage("waiting-for-capture");
                fresh = _capture.WaitForFreshFrame(_lastVersion, Math.Max(1000 / _fps * 2 / 5, 2));
                _lastVersion = _capture.FrameVersion;
                if (ct.IsCancellationRequested) return RawVideoPacingResult.Cancelled;
                _stage("gpu-readback");
                ready = _capture.TryReadFrame(buffer);
                // Native readback can outlive cancellation and downstream teardown.
                if (ct.IsCancellationRequested) return RawVideoPacingResult.Cancelled;
                if (IsOverloaded()) return RawVideoPacingResult.Overloaded;
            }

            if (!ready)
            {
                if (!_hasFrame)
                {
                    _startupNext = _clock() + _frequency / _fps;
                    return RawVideoPacingResult.Waiting;
                }
                Buffer.BlockCopy(_heldFrame, 0, buffer, 0, buffer.Length);
                fresh = false;
            }
            else
            {
                Buffer.BlockCopy(buffer, 0, _heldFrame, 0, buffer.Length);
                _hasFrame = true;
            }

            if (ct.IsCancellationRequested) return RawVideoPacingResult.Cancelled;
            if (IsOverloaded()) return RawVideoPacingResult.Overloaded;
            _stage("frame-enqueue");
            if (!_writer.TryEnqueue(buffer, WriterWaitMs, ct)) return RawVideoPacingResult.Waiting;
            buffer = null;
            EpochTicks ??= _clock();
            FramesSubmitted++;
            _stage("sample-complete");
            return fresh ? RawVideoPacingResult.Fresh : RawVideoPacingResult.Duplicate;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RawVideoPacingResult.Cancelled;
        }
        catch (InvalidOperationException) when (ct.IsCancellationRequested)
        {
            return RawVideoPacingResult.Cancelled;
        }
        finally
        {
            if (buffer is not null) _writer.ReturnUnused(buffer);
        }
    }

    private bool ShouldHoldFrame() => EpochTicks.HasValue && _hasFrame
        && _clock() - NextTicks >= (_frequency + _fps - 1) / _fps;

    private bool IsOverloaded()
    {
        if (!EpochTicks.HasValue) return false;
        long now = _clock();
        long lag = now - NextTicks;
        if (lag >= _frequency / 2) return true;
        if (lag <= (2 * _frequency + _fps - 1) / _fps)
        {
            _recoverySince = null;
            return false;
        }
        _recoverySince ??= now;
        return now - _recoverySince.Value >= 2 * _frequency;
    }
}
