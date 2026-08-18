#region

using System.Buffers.Binary;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Receives the plugin's present-hook frame stream for one clip and forwards it into the
///     encoder's stdin. The consumer is the pipe server: it listens before playback starts, and the
///     plugin connects at the moment its <c>startmovie</c> executes, which is also how the feature
///     stays inert for every other provider (no listener, no connection, no capture).
/// </summary>
/// <remarks>
///     <para>
///         Wire protocol (little-endian, 16-byte records, constants shared with the plugin's
///         <c>present_capture.cpp</c>): one stream header <c>CFRS {width, height, dxgiFormat}</c>,
///         then per frame <c>CFRM {sequence, payloadBytes, 0}</c> followed by exactly
///         <c>payloadBytes</c> of pitch-stripped RGBA rows, then one trailer
///         <c>CFRE {frameCount, 0, 0}</c>. Sequences must be contiguous from zero: the plugin
///         counts movie frames, so a gap here is a dropped frame and fails the clip loudly.
///     </para>
///     <para>
///         Backpressure is structural: writes into a busy encoder block, reads stall, the pipe
///         fills, and the plugin's blocking write stalls the engine's present - slowdown, never
///         frame loss.
///     </para>
/// </remarks>
internal sealed partial class PresentHookFrameConsumer : IFrameStreamConsumer
{
    internal const uint StreamHeaderMagic = 0x53524643; // "CFRS"
    internal const uint FrameMagic = 0x4D524643;        // "CFRM"
    internal const uint TrailerMagic = 0x45524643;      // "CFRE"
    internal const int RecordBytes = 16;

    /// <summary>
    ///     Server-side pipe buffer. Half a frame at 1080p: big enough to decouple bursts, small
    ///     enough that backpressure reaches the plugin within a frame.
    /// </summary>
    private const int PipeBufferBytes = 4 * 1024 * 1024;

    private readonly TaskCompletionSource<FrameSequenceConsumeResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _drainTimeout;
    private readonly int _expectedHeight;
    private readonly int _expectedWidth;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly Stream _sink;
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _bytesConsumed;
    private int _disposed;
    private long _framesConsumed;
    private int _started;

    /// <summary>
    ///     Creates a consumer. Nothing listens until <see cref="Start" />.
    /// </summary>
    /// <param name="pipeName">Pipe name, without the <c>\\.\pipe\</c> prefix.</param>
    /// <param name="expectedWidth">Clip width; the stream header must agree.</param>
    /// <param name="expectedHeight">Clip height; the stream header must agree.</param>
    /// <param name="sink">The encoder's stdin. Closed when the stream ends, ending the encode.</param>
    /// <param name="drainTimeout">How long to wait for the trailer after playback ends.</param>
    /// <param name="logger">Logger; null falls back to a no-op logger.</param>
    public PresentHookFrameConsumer(
        string pipeName,
        int expectedWidth,
        int expectedHeight,
        Stream sink,
        TimeSpan drainTimeout,
        ILogger? logger = null)
    {
        _pipeName = pipeName;
        _expectedWidth = expectedWidth;
        _expectedHeight = expectedHeight;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _drainTimeout = drainTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public long FramesConsumed => Interlocked.Read(ref _framesConsumed);

    /// <inheritdoc />
    public DateTime? FirstFrameConsumedUtc { get; private set; }

    /// <inheritdoc />
    public long BytesConsumed => Interlocked.Read(ref _bytesConsumed);

    /// <inheritdoc />
    /// <remarks>Always 0: frames never wait anywhere this side can see.</remarks>
    public int Backlog => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     Always 0 while running: a sequence gap is a protocol failure, not a tolerated drop, so it
    ///     fails the clip instead of incrementing this.
    /// </remarks>
    public long DroppedFrames => 0;

    /// <inheritdoc />
    public Task<FrameSequenceConsumeResult> Completion => _completion.Task;

    /// <inheritdoc />
    public void Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        CancellationToken linked = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, _cts.Token).Token;

        _ = Task.Run(() => RunAsync(linked), CancellationToken.None);
    }

    /// <inheritdoc />
    public Task<FrameSequenceConsumeResult> StopAsync()
    {
        _stopRequested.TrySetResult();
        return Completion;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _stopRequested.TrySetResult();

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Completion never faults; belt and braces.
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream? server = null;

        try
        {
            server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: PipeBufferBytes,
                outBufferSize: 0);

            // The plugin connects when its startmovie executes, which is mid-playback - after the
            // seek preamble. The wait is bounded by the clip's own lifetime (session token /
            // dispose), plus the drain deadline once playback has ended without a connection.
            Task connect = server.WaitForConnectionAsync(cancellationToken);
            Task stopThenDeadline = StopThenDrainDeadlineAsync(cancellationToken);

            Task first = await Task.WhenAny(connect, stopThenDeadline).ConfigureAwait(false);
            if (first != connect)
            {
                Complete(Failure(
                    "The plugin never connected to the frame pipe. Either the CS2 plugin predates " +
                    "present capture (it must advertise the 'present-capture' capability), its DXGI hook " +
                    "failed to install (see csvg.log next to cs2.exe), or startmovie never executed."));
                return;
            }

            await connect.ConfigureAwait(false);
            LogConnected(_pipeName);

            await ConsumeStreamAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Complete(Failure("The frame pipe consumer was cancelled before the stream ended."));
        }
        catch (Exception ex)
        {
            LogPipeFailed(ex);
            Complete(Failure($"Reading the present-hook frame pipe failed: {ex.Message}"));
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }

            CloseSink();
        }
    }

    private async Task ConsumeStreamAsync(Stream pipe, CancellationToken cancellationToken)
    {
        byte[] record = new byte[RecordBytes];

        // Stream header first: geometry must match the clip, because ffmpeg has already been told
        // the clip's numbers and raw frames of any other size would decode into garbage.
        await ReadExactlyOrFailAsync(pipe, record, "the stream header", cancellationToken).ConfigureAwait(false);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (magic != StreamHeaderMagic)
        {
            Complete(Failure($"The frame pipe opened with 0x{magic:X8} instead of the CFRS stream header."));
            return;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8));
        int dxgiFormat = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12));

        if (width != _expectedWidth || height != _expectedHeight)
        {
            Complete(Failure(
                $"The engine's backbuffer is {width}x{height}, but the clip was armed for " +
                $"{_expectedWidth}x{_expectedHeight}. The capture resolution must match the game window - " +
                "check the session's Width/Height against the clip metadata."));
            return;
        }

        LogStreamHeader(width, height, dxgiFormat);

        int frameBytes = checked(width * height * 4);
        byte[] payload = new byte[frameBytes];
        long expectedSeq = 0;

        while (true)
        {
            await ReadExactlyOrFailAsync(pipe, record, "a frame record", cancellationToken).ConfigureAwait(false);

            magic = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));

            if (magic == TrailerMagic)
            {
                if (a != expectedSeq)
                {
                    Complete(Failure(
                        $"The plugin's trailer declares {a} frames but {expectedSeq} arrived - the stream " +
                        "lost frames between the hook and the pipe."));
                    return;
                }

                LogStreamEnded(expectedSeq);
                Complete(new FrameSequenceConsumeResult
                {
                    Success = true,
                    FramesConsumed = FramesConsumed,
                    BytesConsumed = BytesConsumed,
                    DroppedFrames = 0,
                    FirstFrameConsumedUtc = FirstFrameConsumedUtc
                });
                return;
            }

            if (magic != FrameMagic)
            {
                Complete(Failure($"The frame pipe produced record magic 0x{magic:X8}; the stream is corrupt."));
                return;
            }

            if (a != expectedSeq)
            {
                Complete(Failure(
                    $"Frame sequence broke: expected frame {expectedSeq}, received {a}. The present hook " +
                    "never drops frames by design, so a gap means the capture is not trustworthy."));
                return;
            }

            if (b != (uint)frameBytes)
            {
                Complete(Failure(
                    $"Frame {a} declares {b} bytes against the expected {frameBytes} " +
                    $"({width}x{height}x4) - the backbuffer changed shape mid-clip."));
                return;
            }

            await ReadExactlyOrFailAsync(pipe, payload, $"frame {a}", cancellationToken).ConfigureAwait(false);
            await _sink.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

            FirstFrameConsumedUtc ??= DateTime.UtcNow;
            Interlocked.Increment(ref _framesConsumed);
            Interlocked.Add(ref _bytesConsumed, frameBytes);
            expectedSeq++;
        }
    }

    /// <summary>
    ///     Reads exactly the buffer's length, translating a mid-record end of stream into the failure
    ///     it is: the plugin closes the pipe only after its trailer, so a short read means it died or
    ///     the stream never finished. Thrown exceptions surface through <see cref="RunAsync" />.
    /// </summary>
    private async Task ReadExactlyOrFailAsync(
        Stream pipe, byte[] buffer, string what, CancellationToken cancellationToken)
    {
        // After playback ends the trailer must arrive within the drain timeout; before that, the
        // clip's own lifetime bounds the wait.
        using CancellationTokenSource? drainCts = _stopRequested.Task.IsCompleted
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        drainCts?.CancelAfter(_drainTimeout);

        try
        {
            await pipe.ReadExactlyAsync(buffer, drainCts?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new IOException($"The frame pipe ended while reading {what} - the plugin closed it early.");
        }
        catch (OperationCanceledException) when (drainCts?.IsCancellationRequested == true &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"The plugin did not finish the frame stream within {_drainTimeout.TotalSeconds:F0} s of " +
                $"playback ending (waiting for {what}). endmovie should have drained the ring and written " +
                "the trailer - see csvg.log next to cs2.exe.");
        }
    }

    /// <summary>
    ///     Completes when playback has ended and the drain deadline has then passed: the outer bound
    ///     on waiting for a connection that is evidently not coming.
    /// </summary>
    private async Task StopThenDrainDeadlineAsync(CancellationToken cancellationToken)
    {
        await _stopRequested.Task.ConfigureAwait(false);
        await Task.Delay(_drainTimeout, CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private FrameSequenceConsumeResult Failure(string message) => new()
    {
        Success = false,
        ErrorMessage = message,
        FramesConsumed = FramesConsumed,
        BytesConsumed = BytesConsumed,
        DroppedFrames = 0,
        FirstFrameConsumedUtc = FirstFrameConsumedUtc
    };

    private void Complete(FrameSequenceConsumeResult result) => _completion.TrySetResult(result);

    /// <summary>
    ///     Closing the encoder's stdin is what tells ffmpeg the stream ended; done in the finally so
    ///     an abandoned or failed stream still releases the encoder.
    /// </summary>
    private void CloseSink()
    {
        try
        {
            _sink.Close();
        }
        catch
        {
            // The encoder may already be gone; its own completion reports that.
        }
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Present-hook frame pipe connected ({PipeName})")]
    partial void LogConnected(string pipeName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Present-hook stream: {Width}x{Height}, DXGI format {Format}")]
    partial void LogStreamHeader(int width, int height, int format);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Present-hook stream ended cleanly after {Frames} frame(s)")]
    partial void LogStreamEnded(long frames);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Present-hook frame pipe failed")]
    partial void LogPipeFailed(Exception exception);

    #endregion
}
