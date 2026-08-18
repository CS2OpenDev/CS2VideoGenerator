#region

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Streams a growing sequence of numbered frame images into an encoder and deletes each one as
///     it goes. An offline render writes roughly 8 MB per frame at 60 frames per second of game
///     time, so the frames cannot simply be left on disk until the clip ends; the only bounded
///     arrangement is to consume them while they are still being written.
///     Frame N is treated as complete once a higher-numbered frame exists, because the engine writes
///     the sequence in order, which is also why a missing number is a dropped frame rather than a
///     reason to wait. Backpressure needs no mechanism: writing to a busy encoder blocks, the poller
///     falls behind, and the frames pile up in the staging directory. That pile is the buffer.
/// </summary>
internal sealed partial class FrameSequenceConsumer : IFrameStreamConsumer
{
    /// <summary>
    ///     Default cadence for looking at the staging directory. Polling rather than
    ///     <see cref="FileSystemWatcher" /> is deliberate: watchers drop events under exactly the
    ///     heavy write load this runs under, and differently on each platform.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Bound on the tail drain so a file the writer never lets go of cannot wedge shutdown. The
    ///     drain only starts once recording has been stopped, so a well-behaved tail settles in a
    ///     handful of rounds.
    /// </summary>
    private const int MaxDrainRounds = 100;

    /// <summary>
    ///     Shape of the per-movie subdirectory CS2 creates under the staging directory
    ///     (<c>2026_07_30_15_44_29</c>; the engine's format string is
    ///     <c>movie/%04d_%02d_%02d_%02d_%02d_%02d/%s</c>). One <c>?</c> per digit, so a directory
    ///     someone made by hand is only scanned if it has exactly this shape.
    /// </summary>
    private const string TimestampedSubdirectoryGlob = "????_??_??_??_??_??";

    private const int CopyBufferSize = 128 * 1024;

    private readonly TaskCompletionSource<FrameSequenceConsumeResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _cts = new();
    private readonly bool _deleteAfterPipe;
    private readonly string _directory;
    private readonly long? _expectedFrameSizeBytes;
    private readonly OutputArtifactFinalizer _finalizer;
    private readonly ILogger _logger;
    private readonly FrameSequencePattern _pattern;
    private readonly TimeSpan _pollInterval;
    private readonly bool _scanTimestampedSubdirectories;
    private readonly Stream _sink;

    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _backlog;
    private long _bytesConsumed;

    private bool _cursorAdopted;

    private int _disposed;
    private long _droppedFrames;
    private long _framesConsumed;

    /// <summary>
    ///     The frame number expected next. Everything below it has been consumed or written off.
    /// </summary>
    private int _nextExpected;

    private int _started;

    /// <summary>
    ///     Creates a consumer. Nothing is read until <see cref="Start" /> is called.
    /// </summary>
    /// <param name="stagingDirectory">Directory the engine writes frames into. Need not exist yet.</param>
    /// <param name="pattern">How the frames are named (see <see cref="FrameSequencePattern" />).</param>
    /// <param name="sink">
    ///     Where frames are written, in order: the encoder's standard input. The consumer closes it
    ///     when the sequence ends, which is what tells the encoder there are no more frames.
    /// </param>
    /// <param name="pollInterval">Directory poll cadence; defaults to <see cref="DefaultPollInterval" />.</param>
    /// <param name="finalizer">
    ///     Supplies the file-settled check used to decide the last frame is finished. Defaults to a
    ///     new one; injectable so callers can share theirs.
    /// </param>
    /// <param name="logger">Logger; null falls back to a no-op logger.</param>
    /// <param name="expectedFrameSizeBytes">
    ///     The byte size every frame must have, or null to accept whatever the writer produced.
    ///     Set for formats the encoder can only split at a fixed record length: a frame of a
    ///     different size means the record length is wrong, and piping it anyway produces a video
    ///     that decodes into garbage instead of failing. The first disagreement fails the sequence
    ///     and reports both sizes.
    /// </param>
    /// <param name="deleteAfterPipe">
    ///     Whether each frame is unlinked once it has been piped. False keeps the frames, which is
    ///     only viable for short captures: the whole point of consuming while the writer works is
    ///     that the full sequence does not fit on disk.
    /// </param>
    /// <param name="scanTimestampedSubdirectories">
    ///     Whether timestamped subdirectories of the staging directory are scanned as well as the
    ///     directory itself. CS2 creates one per <c>startmovie</c> and writes every frame there, so
    ///     this is on for real captures; the flat layout keeps working either way because the root
    ///     is always scanned.
    /// </param>
    public FrameSequenceConsumer(
        string stagingDirectory,
        FrameSequencePattern pattern,
        Stream sink,
        TimeSpan? pollInterval = null,
        OutputArtifactFinalizer? finalizer = null,
        ILogger? logger = null,
        long? expectedFrameSizeBytes = null,
        bool deleteAfterPipe = true,
        bool scanTimestampedSubdirectories = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(pattern.DigitCount, 1);

        if (expectedFrameSizeBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedFrameSizeBytes),
                "A frame record length must be greater than zero");
        }

        if (!sink.CanWrite)
        {
            throw new ArgumentException("Sink must be writable", nameof(sink));
        }

        _directory = stagingDirectory;
        _scanTimestampedSubdirectories = scanTimestampedSubdirectories;
        _pattern = pattern;
        _sink = sink;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _finalizer = finalizer ?? new OutputArtifactFinalizer(logger);
        _logger = logger ?? NullLogger.Instance;
        _expectedFrameSizeBytes = expectedFrameSizeBytes;
        _deleteAfterPipe = deleteAfterPipe;
        _nextExpected = pattern.FirstFrameNumber;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_pollInterval, TimeSpan.Zero);
    }

    /// <summary>
    ///     Frames written to the sink so far.
    /// </summary>
    public long FramesConsumed => Interlocked.Read(ref _framesConsumed);

    /// <summary>
    ///     When the first frame was piped, or null while none has been. This is the moment the
    ///     engine demonstrably started rendering, which is what separates render time from the
    ///     seek-and-arm preamble in the capture statistics.
    /// </summary>
    public DateTime? FirstFrameConsumedUtc { get; private set; }

    /// <summary>
    ///     Bytes written to the sink so far.
    /// </summary>
    public long BytesConsumed => Interlocked.Read(ref _bytesConsumed);

    /// <summary>
    ///     Frames sitting in the staging directory that have not been piped yet. A backlog that
    ///     keeps growing means the encoder is not keeping up with the render.
    /// </summary>
    public int Backlog => Volatile.Read(ref _backlog);

    /// <summary>
    ///     Sequence numbers the engine skipped, plus any frame that could not be read. The engine is
    ///     not expected to skip any, so a non-zero count here is a regression signal.
    /// </summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    ///     Completes when the sequence has been fully consumed, cancelled, or has failed. Never
    ///     faults: failures come back as a failed <see cref="FrameSequenceConsumeResult" />.
    /// </summary>
    public Task<FrameSequenceConsumeResult> Completion => _completion.Task;

    /// <summary>
    ///     Stops consuming and waits for the loop to finish. Cancels rather than draining, so this
    ///     is the abandon path; <see cref="StopAsync" /> is the orderly one.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (Volatile.Read(ref _started) != 0)
        {
            await Completion.ConfigureAwait(false);
        }
        else
        {
            CloseSink();
        }

        _cts.Dispose();
    }

    /// <summary>
    ///     Starts consuming. Returns immediately; progress is visible through the counters and the
    ///     outcome through <see cref="Completion" />.
    /// </summary>
    /// <param name="cancellationToken">
    ///     Cancelling abandons the sequence: the loop stops at the next frame boundary, the sink is
    ///     closed and a failed result is reported. No file handles are left open.
    /// </param>
    public void Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The frame sequence consumer has already been started");
        }

        CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                linked.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Signals that the engine has stopped rendering, then waits for the tail drain: everything
    ///     still on disk is piped, the last frame once it has settled, and the sink is closed.
    /// </summary>
    public Task<FrameSequenceConsumeResult> StopAsync()
    {
        _stopRequested.TrySetResult();
        return Completion;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_stopRequested.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ConsumeCompletedFramesAsync(cancellationToken).ConfigureAwait(false);
                await WaitForNextPollAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await DrainAsync(cancellationToken).ConfigureAwait(false);

            LogSequenceComplete(FramesConsumed, BytesConsumed, DroppedFrames);
            _completion.TrySetResult(BuildResult(success: true));
        }
        catch (OperationCanceledException)
        {
            LogSequenceCancelled(FramesConsumed, Backlog);
            _completion.TrySetResult(BuildResult(success: false,
                "Frame consumption was cancelled; the remaining frames were left in the staging directory"));
        }
        catch (Exception ex)
        {
            LogSequenceFailed(ex, FramesConsumed);
            _completion.TrySetResult(BuildResult(success: false, ex.Message));
        }
        finally
        {
            CloseSink();
        }
    }

    /// <summary>
    ///     Pipes every frame the engine has finished with. Everything but the highest-numbered file
    ///     qualifies: a higher number on disk means the writer has moved on from this one.
    /// </summary>
    private async Task ConsumeCompletedFramesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StagedFrame> frames = ScanFrames();
        Volatile.Write(ref _backlog, frames.Count);

        for (int i = 0; i < frames.Count - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await TryPipeFrameAsync(frames[i], cancellationToken).ConfigureAwait(false))
            {
                // Still held by the writer: leave it and try again on the next poll.
                return;
            }
        }
    }

    /// <summary>
    ///     Consumes what is left after the stop signal. The last frame has no successor to prove it
    ///     is finished, so quiescence stands in for one: the file has to settle (checked by
    ///     <see cref="OutputArtifactFinalizer.WaitForFileReadyAsync" />, the same stability rule used
    ///     for finished recordings) and no newer frame may have appeared while waiting.
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        LogDrainStarted(Backlog);

        for (int round = 0; round < MaxDrainRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<StagedFrame> frames = ScanFrames();
            Volatile.Write(ref _backlog, frames.Count);

            if (frames.Count == 0)
            {
                return;
            }

            bool piped = true;
            for (int i = 0; i < frames.Count - 1 && piped; i++)
            {
                piped = await TryPipeFrameAsync(frames[i], cancellationToken).ConfigureAwait(false);
            }

            if (!piped)
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            StagedFrame last = frames[^1];
            await _finalizer.WaitForFileReadyAsync(last.Path, cancellationToken).ConfigureAwait(false);

            if (ScanFrames().Any(frame => frame.Number > last.Number))
            {
                // The engine was still going: what looked like the last frame has a successor, so it
                // is provably complete and the next round pipes it as an ordinary frame.
                continue;
            }

            if (await TryPipeFrameAsync(last, cancellationToken).ConfigureAwait(false))
            {
                Volatile.Write(ref _backlog, 0);
                return;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        int abandoned = ScanFrames().Count;
        if (abandoned > 0)
        {
            Interlocked.Add(ref _droppedFrames, abandoned);
            LogDrainDidNotSettle(abandoned, MaxDrainRounds);
        }
    }

    /// <summary>
    ///     Writes one frame to the sink and unlinks it. Returns false when the file could not be
    ///     opened, which means the writer still holds it and the caller should come back to it.
    ///     A failure of the sink itself is not recoverable and propagates.
    /// </summary>
    private async Task<bool> TryPipeFrameAsync(StagedFrame frame, CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = new FileStream(frame.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            // Deleted underneath us - nothing to pipe and nothing to clean up.
            return true;
        }
        catch (IOException ex)
        {
            LogFrameNotReadable(frame.Number, ex.Message);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogFrameNotReadable(frame.Number, ex.Message);
            return false;
        }

        // Only once the frame is definitely in hand: the sequence cursor is what stops a frame being
        // piped twice, so it must not move past one that is going to be retried.
        AccountForSequencePosition(frame.Number);

        long length;
        await using (source.ConfigureAwait(false))
        {
            length = source.Length;

            // An empty frame is piped nowhere - see below - but it is still deleted, and that has to
            // wait until this block has closed the handle.
            if (length != 0)
            {
                // A record length that does not match the frames on disk is the one failure mode that
                // would otherwise be silent: the encoder would keep splitting the pipe at the wrong
                // offset and write a video of noise. Stop at the first frame that proves it wrong, and
                // report the size that was actually observed so the setting can be corrected.
                if (_expectedFrameSizeBytes is { } expected && length != expected)
                {
                    throw new InvalidDataException(
                        $"Frame {frame.Number} is {length} bytes, but the frame sequence was configured with a " +
                        $"record length of {expected} bytes. A fixed record length is what lets the encoder " +
                        "split a stream of frames it cannot parse, so a frame of any other size means either " +
                        "the configured length is wrong or the frames are not all the same size (compressed " +
                        "output). Set the record length to the observed size, or use a self-delimiting frame " +
                        "format.");
                }

                await source.CopyToAsync(_sink, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            }
        }

        // Both deletes below run with the read handle closed. Windows refuses File.Delete on an open
        // file, and DeleteFrame swallows that failure by design, so a delete issued from inside the
        // block above would leave the frame on disk for the rest of the run without saying so - and
        // every later scan would rediscover it and count it against the backlog.
        if (length == 0)
        {
            // A frame with a successor is finished by definition, so an empty one is a frame the
            // engine never actually wrote. Piping it would corrupt the stream.
            Interlocked.Increment(ref _droppedFrames);
            LogEmptyFrame(frame.Number);
            DeleteFrame(frame);
            return true;
        }

        FirstFrameConsumedUtc ??= DateTime.UtcNow;
        Interlocked.Increment(ref _framesConsumed);
        Interlocked.Add(ref _bytesConsumed, length);
        DeleteFrame(frame);
        return true;
    }

    /// <summary>
    ///     Tracks where the sequence has got to and reports skipped numbers. The first frame seen
    ///     sets the starting point: if the engine numbers from somewhere other than
    ///     <see cref="FrameSequencePattern.FirstFrameNumber" /> that is a fact about the engine, not
    ///     a run of dropped frames.
    /// </summary>
    private void AccountForSequencePosition(int frameNumber)
    {
        if (!_cursorAdopted)
        {
            _cursorAdopted = true;

            if (frameNumber != _pattern.FirstFrameNumber)
            {
                // The configured numbering base disagrees with what the engine wrote. Whether
                // startmovie numbers from 0 or 1 is one of the facts still to be confirmed on a
                // machine with CS2; until then the observed value wins and this note records it.
                LogSequenceBaseDiffers(frameNumber, _pattern.FirstFrameNumber);
            }

            _nextExpected = frameNumber;
        }
        else if (frameNumber > _nextExpected)
        {
            int missing = frameNumber - _nextExpected;
            Interlocked.Add(ref _droppedFrames, missing);
            LogFramesDropped(missing, _nextExpected, frameNumber);
        }

        _nextExpected = frameNumber + 1;
    }

    /// <summary>
    ///     Lists the frames currently in the staging directory, in sequence order, ignoring anything
    ///     that does not match the pattern exactly and anything already consumed.
    /// </summary>
    private IReadOnlyList<StagedFrame> ScanFrames()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        List<StagedFrame> frames = [];
        CollectFrames(_directory, frames);

        if (_scanTimestampedSubdirectories)
        {
            // The engine creates the subdirectory when startmovie fires, which is after this
            // consumer started - so it is re-discovered on every scan rather than resolved once.
            // Scanning every matching subdirectory instead of guessing "the right one" costs a few
            // directory enumerations and removes the need to pick: frame names carry the movie name,
            // so another movie's leftovers do not match this pattern.
            try
            {
                foreach (string subdirectory in
                         Directory.EnumerateDirectories(_directory, TimestampedSubdirectoryGlob))
                {
                    if (IsTimestampShaped(Path.GetFileName(subdirectory)))
                    {
                        CollectFrames(subdirectory, frames);
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The staging directory disappeared between the check above and the enumeration;
                // the next poll re-evaluates from the top.
            }
        }

        frames.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        return frames;
    }

    private void CollectFrames(string directory, List<StagedFrame> frames)
    {
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, _pattern.SearchPattern);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (string path in candidates)
        {
            if (!_pattern.TryGetFrameNumber(Path.GetFileName(path), out int number))
            {
                continue;
            }

            // A frame that could not be unlinked would otherwise be piped twice.
            if (_cursorAdopted && number < _nextExpected)
            {
                continue;
            }

            frames.Add(new StagedFrame(number, path));
        }
    }

    /// <summary>
    ///     Whether a directory name has exactly the engine's timestamp shape: digits everywhere the
    ///     glob's <c>?</c> sits, underscores between the groups.
    /// </summary>
    private static bool IsTimestampShaped(string name)
    {
        if (name.Length != TimestampedSubdirectoryGlob.Length)
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            bool expectDigit = TimestampedSubdirectoryGlob[i] == '?';
            if (expectDigit ? !char.IsAsciiDigit(name[i]) : name[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private void DeleteFrame(StagedFrame frame)
    {
        if (!_deleteAfterPipe)
        {
            return;
        }

        try
        {
            File.Delete(frame.Path);
            Volatile.Write(ref _backlog, Math.Max(0, Volatile.Read(ref _backlog) - 1));
        }
        catch (Exception ex)
        {
            // Leaving the file behind costs disk, not correctness: the sequence cursor keeps it from
            // being piped a second time.
            LogFrameDeleteFailed(frame.Number, ex.Message);
        }
    }

    /// <summary>
    ///     Waits out the poll interval, returning early when the stop signal arrives so the drain
    ///     does not sit through a full interval it does not need.
    /// </summary>
    private async Task WaitForNextPollAsync(CancellationToken cancellationToken)
    {
        Task delay = Task.Delay(_pollInterval, cancellationToken);
        await Task.WhenAny(delay, _stopRequested.Task).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void CloseSink()
    {
        try
        {
            _sink.Flush();
            _sink.Dispose();
        }
        catch (Exception ex)
        {
            // The encoder may already be gone, which is exactly the case where closing throws.
            LogSinkCloseFailed(ex.Message);
        }
    }

    private FrameSequenceConsumeResult BuildResult(bool success, string? error = null) => new()
    {
        Success = success,
        FramesConsumed = FramesConsumed,
        BytesConsumed = BytesConsumed,
        DroppedFrames = DroppedFrames,
        FirstFrameConsumedUtc = FirstFrameConsumedUtc,
        ErrorMessage = error
    };

    private readonly record struct StagedFrame(int Number, string Path);

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Frame sequence consumed: {Frames} frames, {Bytes} bytes, {Dropped} dropped")]
    partial void LogSequenceComplete(long frames, long bytes, long dropped);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Frame consumption cancelled after {Frames} frames with {Backlog} left in staging")]
    partial void LogSequenceCancelled(long frames, int backlog);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Frame consumption failed after {Frames} frames")]
    partial void LogSequenceFailed(Exception exception, long frames);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Frame sequence skipped {Count} frame(s): expected {Expected}, found {Found}")]
    partial void LogFramesDropped(int count, int expected, int found);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Frame sequence starts at {Observed}, not the configured {Configured}")]
    partial void LogSequenceBaseDiffers(int observed, int configured);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Frame {Number} is not readable yet: {Error}")]
    partial void LogFrameNotReadable(int number, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Frame {Number} is empty and was dropped")]
    partial void LogEmptyFrame(int number);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Could not delete frame {Number}: {Error}")]
    partial void LogFrameDeleteFailed(int number, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Draining frame sequence ({Backlog} in staging)")]
    partial void LogDrainStarted(int backlog);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "Frame sequence did not settle after {Rounds} drain rounds; abandoned {Count} frame(s)")]
    partial void LogDrainDidNotSettle(int count, int rounds);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Closing the frame sink failed: {Error}")]
    partial void LogSinkCloseFailed(string error);

    #endregion
}

/// <summary>
///     Outcome of consuming a frame sequence.
/// </summary>
internal sealed record FrameSequenceConsumeResult
{
    /// <summary>
    ///     Whether the sequence was consumed to its end. False means cancelled or failed; the
    ///     frames piped before that point are still in the encoder's input.
    /// </summary>
    public required bool Success { get; init; }

    public required long FramesConsumed { get; init; }

    public required long BytesConsumed { get; init; }

    /// <summary>
    ///     Frames the engine skipped or that could not be read.
    /// </summary>
    public required long DroppedFrames { get; init; }

    /// <summary>
    ///     Why consumption did not finish, when it did not.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     When the first frame was piped, or null if none ever was. Marks where the render began,
    ///     as opposed to the seek-and-arm preamble before it.
    /// </summary>
    public DateTime? FirstFrameConsumedUtc { get; init; }
}
