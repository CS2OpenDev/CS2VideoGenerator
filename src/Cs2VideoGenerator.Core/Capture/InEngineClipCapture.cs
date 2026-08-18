#region

using System.Diagnostics;
using System.Globalization;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     One clip rendered offline by the engine: <c>startmovie</c> dumps a numbered image per simulated
///     frame, those are streamed into ffmpeg and deleted as they go, and the engine's own wav is muxed
///     onto the result.
/// </summary>
/// <remarks>
///     <para>
///         Because <c>host_framerate</c> advances game time by exactly one frame per rendered frame,
///         the output length is a function of the tick range and the requested frame rate rather than
///         of how long the render took. That is what <see cref="CaptureResult.Duration" /> reports;
///         wall clock and achieved frame rate go in <see cref="CaptureResult.Statistics" />.
///     </para>
///     <para>
///         The engine work is declared, not performed: <see cref="TickPlan" /> says what has to run and
///         when, and <see cref="ClipCaptureCoordinator" /> issues the setup and arms the batches before
///         playback, so the batches land ahead of the plugin's end-of-range auto-pause.
///     </para>
/// </remarks>
public sealed partial class InEngineClipCapture : IClipCapture
{
    /// <summary>
    ///     Demo ticks per second of game time. CS2 demos are recorded at 64.
    /// </summary>
    private const int DemoTicksPerSecond = 64;

    /// <summary>
    ///     Slack allowed when deciding whether a wav is older than the clip looking for it. File
    ///     timestamps are coarse on some file systems, so a strict comparison would occasionally reject
    ///     a wav the engine really did just write.
    /// </summary>
    private const int AudioTimestampGraceSeconds = 5;

    /// <summary>
    ///     Floor for any elapsed-time divisor, in seconds. The system clock behind
    ///     <see cref="DateTime.UtcNow" /> ticks about every 15.6 ms on Windows, so a short render can
    ///     complete between two reads and measure as zero. One tick of that granularity is the
    ///     smallest duration the clock can honestly claim to have observed.
    /// </summary>
    private const double ClockResolutionSeconds = 0.0156;

    /// <summary>
    ///     Output containers built on the ISO base media format, which carry H.264 and little else that
    ///     matters here, notably not utvideo.
    /// </summary>
    private static readonly HashSet<string> Mp4FamilyContainers =
        new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "mov" };

    private readonly InEngineCaptureConfiguration _configuration;
    private readonly ClipCaptureContext _context;
    private readonly int _endTick;
    private readonly double _frameRate;
    private readonly double _captureFrameRate;
    private readonly double _playbackSpeed;
    private readonly string _intermediatePath;
    private readonly ILogger _logger;
    private readonly string _movieName;
    private readonly int _recordTick;
    private readonly InEngineCaptureSession _session;
    private readonly List<string> _warnings = [];

    private IFrameStreamConsumer? _consumer;
    private Task<FrameSequenceConsumeResult>? _drain;
    private IImagePipeEncodeSession? _encode;
    private DateTime? _endedUtc;
    private int _finished;
    private CaptureProgressPump? _progressPump;
    private DateTime? _startedUtc;

    /// <summary>
    ///     Creates a clip bound to an in-engine capture session. Clips are handed out by
    ///     <see cref="InEngineCaptureSession.BeginClipAsync" />; this constructor exists for that call
    ///     and for tests.
    /// </summary>
    internal InEngineClipCapture(InEngineCaptureSession session, ClipCaptureContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = session.Logger;
        _configuration = session.Configuration;

        _frameRate = context.Metadata.FrameRate;
        double playbackSpeed = context.Metadata.PlaybackSpeed;
        if (playbackSpeed <= 0 || !double.IsFinite(playbackSpeed))
        {
            throw new ArgumentException(
                $"PlaybackSpeed must be a positive number (got {playbackSpeed}); 1 is real time, " +
                "0.5 is 2x slow motion.", nameof(context));
        }

        // The rate split behind slow motion: the engine renders enough real frames for the slowed
        // playback (host_framerate = FrameRate / speed) while ffmpeg plays them out at FrameRate.
        _playbackSpeed = playbackSpeed;
        _captureFrameRate = _frameRate / playbackSpeed;
        _movieName = InEngineCaptureConfiguration.MovieName(context.ClipIndex);
        _intermediatePath = Path.Combine(session.StagingDirectory,
            $"{_movieName}.{_configuration.IntermediateContainer}");

        int clamp = _configuration.StartTickClamp;
        _recordTick = Math.Max(clamp, context.StartTick);
        _endTick = Math.Max(_recordTick, context.EndTick);

        if (context.StartTick < clamp)
        {
            _warnings.Add(
                $"The clip starts at tick {context.StartTick}, but the engine swallows commands scheduled " +
                $"before tick {clamp}, so recording starts at tick {clamp} instead and the first " +
                $"{clamp - context.StartTick} tick(s) of the clip are not in the output.");
            LogStartTickClamped(context.StartTick, clamp);
        }

        TickPlan = BuildTickPlan();
    }

    /// <summary>
    ///     The movie name this clip asked the engine for, which is also the prefix of every file the
    ///     engine writes for it.
    /// </summary>
    internal string MovieName => _movieName;

    /// <summary>
    ///     Where the frame pipe writes before audio is muxed on.
    /// </summary>
    internal string IntermediatePath => _intermediatePath;

    /// <inheritdoc />
    /// <remarks>
    ///     The cvars are immediate setup rather than a batch: they only have to be in place before
    ///     <c>startmovie</c>, not at any particular tick, and a tick would only expose them to being
    ///     stranded behind the playhead of the previous clip. <c>startmovie</c> is the one thing here
    ///     that genuinely needs a tick. The end batch carries the cvar restores along with
    ///     <c>endmovie</c>, because a restore scheduled after the end tick would wait for a tick that
    ///     playback never reaches.
    /// </remarks>
    public TickCommandPlan TickPlan { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Starts ffmpeg and the frame consumer before playback, so the first frame the engine writes
    ///     has somewhere to go. The engine has not been told to record yet - that happens at the
    ///     recording tick - so an empty staging directory here is expected.
    /// </remarks>
    public async Task OnPlaybackStartingAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("InEngineClipCapture.StartEncode");
        activity?.SetTag("csvg.inengine.movieName", _movieName);
        activity?.SetTag("csvg.inengine.frameRate", _frameRate);

        ThrowIfOutputCannotHoldTheIntermediate();
        ThrowIfPresentCaptureUnavailable();

        // Anything under this movie's name that predates the clip is a previous run's. Sweeping it now
        // costs nothing and the written-after check in ResolveAudioAsync catches whatever would not
        // delete - between them, a clip cannot inherit an earlier take's audio.
        _session.SweepFiles(_session.AudioDirectory, AudioSearchPattern(),
            _configuration.EngineOutput.AudioSearchRecursive);

        long? recordLength = ResolveTgaRecordLength();

        ImagePipeEncodeRequest request = new()
        {
            OutputPath = _intermediatePath,
            FrameRate = _frameRate,
            SourceFormat = _configuration.SourceFormat(),
            FrameSizeBytes = recordLength.HasValue ? checked((int)recordLength.Value) : null,
            RawVideo = _session.FrameSource == InEngineFrameSource.PresentHook
                ? new RawVideoInput
                {
                    PixelFormat = InEngineCaptureConfiguration.PresentHookPixelFormat,
                    Width = RequiredDimension(_context.Metadata.Width, "Width"),
                    Height = RequiredDimension(_context.Metadata.Height, "Height")
                }
                : null,
            Encoder = _configuration.IntermediateEncoder,
            Quality = _configuration.IntermediateQuality,
            EncoderPreset = _configuration.IntermediateEncoderPreset
        };

        _encode = await _session.Ffmpeg.StartImagePipeEncodeAsync(request, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("csvg.inengine.encoder", _encode.Encoder.ToString());
        activity?.SetTag("csvg.inengine.encoderPreset", _encode.Preset);

        if (_session.FrameSource == InEngineFrameSource.PresentHook)
        {
            _consumer = new PresentHookFrameConsumer(
                _configuration.FramePipeName,
                request.RawVideo!.Width,
                request.RawVideo.Height,
                _encode.Input,
                TimeSpan.FromMilliseconds(_configuration.HookedDrainTimeoutMs),
                _logger);
        }
        else
        {
            FrameSequencePattern pattern = new()
            {
                Prefix = _movieName,
                DigitCount = _configuration.EngineOutput.FrameNumberDigits,
                Extension = _configuration.FrameExtension(),
                FirstFrameNumber = _configuration.EngineOutput.FirstFrameNumber
            };

            _consumer = new FrameSequenceConsumer(
                _session.StagingDirectory,
                pattern,
                _encode.Input,
                TimeSpan.FromMilliseconds(_configuration.FrameConsumerPollMs),
                _session.Finalizer,
                _logger,
                recordLength,
                _configuration.DeleteFramesAfterPipe,
                _configuration.EngineOutput.FramesInTimestampedSubdirectory);
        }

        _startedUtc = DateTime.UtcNow;
        _consumer.Start(_session.SessionToken);

        WatchForEarlyEncodeFailure(_encode);
        WatchForEarlyFrameFailure(_consumer);
        StartProgressMonitoring();

        CsvgMetrics.CaptureSessionCount.Add(1,
            new KeyValuePair<string, object?>("provider", _session.ProviderName),
            new KeyValuePair<string, object?>("result", "start"));

        LogEncodeStarted(_movieName, _encode.Encoder.ToString(), _frameRate);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Signals the tail drain. <c>endmovie</c> has already fired at the end tick, so the engine has
    ///     stopped writing; what is left is whatever the consumer has not caught up with.
    /// </remarks>
    public Task OnPlaybackEndedAsync(CancellationToken cancellationToken = default)
    {
        _endedUtc = DateTime.UtcNow;
        StopProgressMonitoring();

        if (_consumer != null)
        {
            StartDrain(_consumer);
            LogDrainSignalled(_consumer.Backlog);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Waits out the drain, closes the pipe, waits for ffmpeg, finds and checks the engine's wav,
    ///     muxes it on and moves the result into place. Never throws for capture-level problems.
    /// </remarks>
    public async Task<CaptureResult> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("InEngineClipCapture.Finalize");
        activity?.SetTag("csvg.inengine.movieName", _movieName);

        string desiredOutputPath = _context.Metadata.GetFullOutputPath();

        try
        {
            if (_encode is null || _consumer is null)
            {
                return Failed(desiredOutputPath,
                    "The clip was finalized without ever starting: no encoder was running to receive frames.");
            }

            FrameSequenceConsumeResult frames = await StartDrain(_consumer).ConfigureAwait(false);
            activity?.SetTag("csvg.inengine.frames", frames.FramesConsumed);
            activity?.SetTag("csvg.inengine.droppedFrames", frames.DroppedFrames);

            // The consumer closes the pipe as it finishes, which is what tells ffmpeg the sequence has
            // ended; waiting for that before waiting on ffmpeg is the whole ordering.
            FfmpegEncodeResult encoded = await _encode.Completion.ConfigureAwait(false);

            if (!frames.Success)
            {
                return Failed(desiredOutputPath,
                    $"The frame sequence could not be consumed: {frames.ErrorMessage}");
            }

            if (!encoded.Success)
            {
                return Failed(desiredOutputPath,
                    $"The intermediate encode failed: {encoded.ErrorMessage}");
            }

            if (frames.FramesConsumed == 0)
            {
                return Failed(desiredOutputPath, NoFramesMessage());
            }

            // Two durations once slow motion exists: the output plays frames / FrameRate; the
            // engine's wav covers the game time those frames sample, frames / captureFrameRate.
            // At speed 1 they are the same number.
            TimeSpan gameDuration = TimeSpan.FromSeconds(frames.FramesConsumed / _frameRate);
            TimeSpan sourceGameTime = TimeSpan.FromSeconds(frames.FramesConsumed / _captureFrameRate);
            activity?.SetTag("csvg.inengine.gameDurationMs", gameDuration.TotalMilliseconds);

            string? audioPath = await ResolveAudioAsync(sourceGameTime, frames.DroppedFrames, cancellationToken)
                .ConfigureAwait(false);

            if (_context.Metadata.CaptureAudio && audioPath is null &&
                _configuration.MissingAudioBehavior == MissingAudioBehavior.Fail)
            {
                return Failed(desiredOutputPath, MissingAudioMessage());
            }

            Directory.CreateDirectory(_context.Metadata.OutputDirectory);

            // The mux lands next to its destination rather than in staging: staging is swept by movie
            // name once the clip is done, and a rename that failed would leave the finished clip
            // sitting in the path of that sweep. The container extension stays last, because it is
            // what ffmpeg picks the muxer from.
            string muxedPath = Path.Combine(_context.Metadata.OutputDirectory,
                $"{_context.Metadata.OutputFileName}.part.{_context.Metadata.ContainerFormat}");

            // At a playback speed other than 1, the wav (game time) and the video (game time /
            // speed) differ in length by construction; stretching the audio by the speed factor
            // makes the tracks the same length again, with real slowed audio rather than silence.
            double? audioTempo = Math.Abs(_playbackSpeed - 1.0) > 0.0001 ? _playbackSpeed : null;

            FfmpegEncodeResult muxed = await _session.Ffmpeg.MuxAsync(
                    _intermediatePath,
                    audioPath,
                    muxedPath,
                    _configuration.AudioCodec,
                    _context.Metadata.AudioBitrate,
                    audioTempo,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!muxed.Success)
            {
                return Failed(desiredOutputPath, $"Muxing the clip failed: {muxed.ErrorMessage}");
            }

            string finalPath = desiredOutputPath;
            if (!await _session.Finalizer
                    .TryRenameWithRetryAsync(muxedPath, desiredOutputPath, cancellationToken)
                    .ConfigureAwait(false))
            {
                finalPath = muxedPath;
                _warnings.Add(
                    $"Could not move the finished clip to '{desiredOutputPath}'; it was left at '{muxedPath}'.");
            }

            List<string> additionalOutputs = [];
            await ProduceNormalizedCopyAsync(audioPath, additionalOutputs, cancellationToken)
                .ConfigureAwait(false);

            long fileSize = new FileInfo(finalPath).Length;
            CsvgMetrics.CaptureOutputSize.Record(fileSize,
                new KeyValuePair<string, object?>("provider", _session.ProviderName));
            CsvgMetrics.CaptureSessionCount.Add(1,
                new KeyValuePair<string, object?>("provider", _session.ProviderName),
                new KeyValuePair<string, object?>("result", "completed"));

            CleanUpAfterSuccess(audioPath, additionalOutputs);

            activity?.SetTag("csvg.inengine.output", finalPath);
            activity?.SetTag("csvg.inengine.outputSize", fileSize);
            LogClipFinished(_movieName, frames.FramesConsumed, gameDuration.TotalSeconds, finalPath);

            return new CaptureResult
            {
                Success = true,
                OutputFilePath = finalPath,
                Duration = gameDuration,
                FileSizeBytes = fileSize,
                TotalFrames = frames.FramesConsumed,
                DroppedFrames = frames.DroppedFrames,
                StartTime = _startedUtc,
                EndTime = _endedUtc ?? DateTime.UtcNow,
                Warnings = [.. _warnings],
                AdditionalOutputs = additionalOutputs,
                Statistics = BuildStatistics(frames, gameDuration, audioPath != null)
            };
        }
        catch (Exception ex)
        {
            LogFinalizeFailed(ex);
            _session.RaiseError(new CaptureErrorEventArgs($"Error finalizing an in-engine clip: {ex.Message}", ex));
            CsvgMetrics.CaptureSessionCount.Add(1,
                new KeyValuePair<string, object?>("provider", _session.ProviderName),
                new KeyValuePair<string, object?>("result", "error"));

            return Failed(desiredOutputPath, ex.Message);
        }
        finally
        {
            MarkFinished();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Kills the encoder and abandons whatever is left in staging. The engine's own recording is
    ///     stopped by <see cref="TickCommandPlan.AbortCommands" />, which the coordinator runs before
    ///     this: a tick-scheduled <c>endmovie</c> would be waiting for a tick that never arrives.
    /// </remarks>
    public async Task AbortAsync(CaptureAbortReason reason, CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("InEngineClipCapture.Abort");
        activity?.SetTag("csvg.inengine.abortReason", reason.ToString());

        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        StopProgressMonitoring();

        IFrameStreamConsumer? consumer = Interlocked.Exchange(ref _consumer, null);
        if (consumer != null)
        {
            try
            {
                await consumer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogAbortCleanupFailed(ex);
            }
        }

        IImagePipeEncodeSession? encode = Interlocked.Exchange(ref _encode, null);
        if (encode != null)
        {
            try
            {
                await encode.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogAbortCleanupFailed(ex);
            }
        }

        if (!_configuration.KeepStagingOnFailure)
        {
            _session.SweepFiles(_session.StagingDirectory, _movieName + "*");
        }
        else
        {
            LogStagingKept(_session.StagingDirectory, _movieName);
        }

        _session.ClipFinished(this);
        LogClipAborted(_movieName, reason.ToString());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _finished) == 0)
        {
            await AbortAsync(CaptureAbortReason.Cancelled, CancellationToken.None).ConfigureAwait(false);
        }

        StopProgressMonitoring();
        _session.ClipFinished(this);
    }

    /// <summary>
    ///     Signals the tail drain, once. Published so the fault watches can tell "still recording" from
    ///     "playback is over and finalize owns the outcome".
    /// </summary>
    private Task<FrameSequenceConsumeResult> StartDrain(IFrameStreamConsumer consumer)
    {
        Task<FrameSequenceConsumeResult>? existing = Volatile.Read(ref _drain);
        if (existing != null)
        {
            return existing;
        }

        Task<FrameSequenceConsumeResult> started = consumer.StopAsync();
        return Interlocked.CompareExchange(ref _drain, started, null) ?? started;
    }

    /// <summary>
    ///     Rejects the one encoder-and-container combination that cannot work, before the render rather
    ///     than after it. The intermediate is muxed with <c>-c:v copy</c>, so a lossless intermediate
    ///     stream has to be one the requested output container can actually carry, and an MP4-family
    ///     container cannot carry utvideo. Finding that out after a twenty-minute render would be the
    ///     expensive way to learn it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The output container cannot hold the intermediate.</exception>
    private void ThrowIfOutputCannotHoldTheIntermediate()
    {
        if (_configuration.IntermediateEncoder != ImagePipeEncoder.Utvideo)
        {
            return;
        }

        string container = _context.Metadata.ContainerFormat;
        if (!Mp4FamilyContainers.Contains(container))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Capture:InEngine:IntermediateEncoder is Utvideo, but this clip's output container is " +
            $"'{container}', which cannot carry a utvideo stream - and the audio mux copies the video " +
            "stream rather than re-encoding it. Use an output container that can (mkv), or a different " +
            "intermediate encoder.");
    }

    /// <summary>
    ///     The wav glob for this clip, with the movie name substituted in.
    /// </summary>
    private string AudioSearchPattern() =>
        string.Format(CultureInfo.InvariantCulture, _configuration.EngineOutput.AudioFileSearchPattern,
            _movieName);

    /// <summary>
    ///     Fails a present-hook clip before playback when the connected plugin cannot feed it. The
    ///     capability is only knowable once the plugin has handshaken, which is why this lives here
    ///     rather than in configuration validation.
    /// </summary>
    private void ThrowIfPresentCaptureUnavailable()
    {
        if (_session.FrameSource != InEngineFrameSource.PresentHook)
        {
            return;
        }

        if (!_session.Engine.Supports(CsvgCapabilities.PresentCapture))
        {
            throw new InvalidOperationException(
                "The InEngineHooked provider needs a CS2 plugin advertising the 'present-capture' " +
                "capability, and the connected plugin does not. Rebuild/update the plugin (Windows " +
                "server.dll), or use --capture-provider InEngine, which works with this plugin.");
        }
    }

    /// <summary>
    ///     A clip dimension the present hook cannot proceed without: ffmpeg must be told the raw
    ///     stream's geometry before the first byte, and the stream header check needs a number to
    ///     hold the plugin to.
    /// </summary>
    private static int RequiredDimension(int? value, string name)
    {
        if (value is > 0)
        {
            return value.Value;
        }

        throw new InvalidOperationException(
            $"A present-hook capture needs the clip {name} - raw frames have no header, so ffmpeg must " +
            "be told the geometry up front. Pass the resolution on the clip metadata (the CLI's " +
            "--width/--height do this).");
    }

    /// <summary>
    ///     The fixed byte length of one Targa frame, or null for self-delimiting formats. An explicit
    ///     <see cref="InEngineEngineOutput.TgaFrameSizeBytes" /> wins; otherwise it is computed from
    ///     the clip's width and height with the observed 32-bit arithmetic
    ///     (<see cref="InEngineCaptureConfiguration.UncompressedTgaFrameSize" />). A Targa clip that
    ///     supplies neither cannot be split by ffmpeg at all, and says so here rather than failing
    ///     later with zero frames.
    /// </summary>
    private long? ResolveTgaRecordLength()
    {
        if (_session.FrameSource == InEngineFrameSource.PresentHook ||
            _configuration.FrameFormat != InEngineFrameFormat.Tga)
        {
            return null;
        }

        if (_configuration.EngineOutput.TgaFrameSizeBytes is { } configured)
        {
            return configured;
        }

        if (_context.Metadata is { Width: > 0, Height: > 0 })
        {
            return InEngineCaptureConfiguration.UncompressedTgaFrameSize(
                _context.Metadata.Width.Value, _context.Metadata.Height.Value);
        }

        throw new InvalidOperationException(
            "A Targa capture needs a frame record length - ffmpeg has no Targa parser and can only split " +
            "the pipe at a fixed byte size - but the clip metadata carries no width and height to compute " +
            "it from and Capture:InEngine:EngineOutput:TgaFrameSizeBytes is not set. Provide the clip " +
            "resolution, set TgaFrameSizeBytes (18 + width x height x 4), or use FrameFormat Jpg.");
    }

    /// <summary>
    ///     The engine-side recipe: cvars now, then <c>startmovie</c> at the recording tick, then
    ///     <c>endmovie</c> and the restores at the end tick.
    /// </summary>
    private TickCommandPlan BuildTickPlan()
    {
        List<string> setup =
        [
            $"host_framerate {_captureFrameRate.ToString("0.####", CultureInfo.InvariantCulture)}",
            "engine_no_focus_sleep 0"
        ];

        setup.AddRange(_configuration.AdditionalSetupCommands.Where(c => !string.IsNullOrWhiteSpace(c)));

        // Everything about the recording itself travels on the startmovie line: format, audio,
        // jpeg quality are all startmovie arguments, not cvars (observed; the engine's usage string
        // is "startmovie testmovie jpg wav jpeg_qality 75 framerate 30").
        List<string> movieArguments = [_configuration.EngineOutput.MovieNamePrefix + _movieName];

        if (_session.FrameSource == InEngineFrameSource.PresentHook)
        {
            // Audio-only mode: `wav` as the leading token suppresses frame files entirely while
            // keeping the fixed timestep and the game-time wav (observed). The wav is recorded even
            // for a no-audio clip - it is the mode selector - and discarded at finalize. Frames come
            // from the plugin's present hook, which arms itself when it sees this command execute.
            movieArguments.AddRange(_configuration.EngineOutput.AudioOnlyTokens.Split(' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else
        {
            // The format token is mandatory either way: a token-less startmovie is the engine's
            // AVI/video path (observed - it reports no format and writes no image files), not a Targa
            // frame dump.
            movieArguments.Add(_configuration.FrameFormat == InEngineFrameFormat.Jpg
                ? _configuration.EngineOutput.JpgFormatToken
                : _configuration.EngineOutput.TgaFormatToken);

            // Audio is opt-in on the engine side: without the token no wav is written at all.
            if (_context.Metadata.CaptureAudio && !string.IsNullOrWhiteSpace(_configuration.EngineOutput.AudioToken))
            {
                movieArguments.Add(_configuration.EngineOutput.AudioToken);
            }

            if (_configuration.FrameFormat == InEngineFrameFormat.Jpg &&
                _configuration.JpegQuality is { } quality &&
                !string.IsNullOrWhiteSpace(_configuration.EngineOutput.JpegQualityArgument))
            {
                movieArguments.Add(_configuration.EngineOutput.JpegQualityArgument);
                movieArguments.Add(quality.ToString(CultureInfo.InvariantCulture));
            }
        }

        string startMovie = "startmovie " + string.Join(' ', movieArguments);

        string[] teardown =
        [
            "endmovie",
            "host_framerate 0",
            $"engine_no_focus_sleep {_configuration.EngineOutput.FocusSleepRestoreValue.ToString(CultureInfo.InvariantCulture)}"
        ];

        return new TickCommandPlan
        {
            SetupCommands = setup,
            Batches =
            [
                new TickCommandBatch(_recordTick, [startMovie]),
                new TickCommandBatch(_endTick, teardown)
            ],
            AbortCommands = teardown
        };
    }

    /// <summary>
    ///     Finds the engine's wav and decides what to do with it: mux it, delete it, or report that it
    ///     is not there. Returns the path to mux, or null when the clip carries no audio.
    /// </summary>
    /// <remarks>
    ///     Audio is opt-in on the engine side (the <c>wav</c> startmovie token, sent only when the
    ///     clip asks for audio), but a stale wav from an earlier take may still exist, so the lookup
    ///     runs for every clip and a no-audio clip deletes what it finds.
    /// </remarks>
    private async Task<string?> ResolveAudioAsync(
        TimeSpan gameDuration,
        long droppedFrames,
        CancellationToken cancellationToken)
    {
        // Only a wav written since this clip armed can be this clip's. Without this a clip whose
        // recording produced none would quietly inherit the previous run's audio - right name, right
        // length, wrong take, and nothing to notice.
        string? wavPath = _session.AudioLocator.FindWav(_session.AudioDirectory, _movieName,
            _configuration.EngineOutput, _startedUtc?.AddSeconds(-AudioTimestampGraceSeconds));

        if (!_context.Metadata.CaptureAudio)
        {
            if (wavPath != null)
            {
                DeleteBestEffort(wavPath);
                LogDiscardedAudio(wavPath);
            }

            return null;
        }

        if (wavPath is null)
        {
            if (_configuration.MissingAudioBehavior == MissingAudioBehavior.WarnAndContinue)
            {
                _warnings.Add(MissingAudioMessage());
                _session.RaiseError(new CaptureErrorEventArgs(MissingAudioMessage(), severity:
                    CaptureErrorSeverity.Warning));
            }

            return null;
        }

        WavHeader? header = await _session.AudioLocator.WaitForFinalizedHeaderAsync(
                wavPath,
                TimeSpan.FromMilliseconds(_configuration.AudioFinalizationTimeoutMs),
                TimeSpan.FromMilliseconds(_configuration.AudioFinalizationPollMs),
                cancellationToken)
            .ConfigureAwait(false);

        if (header is null)
        {
            _warnings.Add(
                $"The engine's movie audio at '{wavPath}' could not be read as a wav, so its length could not " +
                "be checked against the video. It is muxed on unchecked.");
        }
        else if (!header.IsFinalized)
        {
            _warnings.Add(
                $"The engine had not finished writing '{wavPath}' after " +
                $"{_configuration.AudioFinalizationTimeoutMs} ms - its header still declares " +
                $"{header.DeclaredDataBytes} bytes of audio against {header.AvailableDataBytes} bytes actually " +
                "present - so its length could not be checked against the video. It is muxed on unchecked.");
        }
        else
        {
            CheckAudioDuration(header, gameDuration, droppedFrames, wavPath);
        }

        return wavPath;
    }

    /// <summary>
    ///     Compares the wav against the length the frames imply, and says which of the two known causes
    ///     a mismatch fits. Dropped frames make the video genuinely shorter than the audio, which is a
    ///     different defect from the engine writing the audio at the wrong length, and a message that
    ///     did not separate them would send the reader after the wrong one.
    /// </summary>
    private void CheckAudioDuration(WavHeader header, TimeSpan gameDuration, long droppedFrames, string wavPath)
    {
        AudioDurationCheck check = InEngineAudioLocator.CheckDuration(
            header.Duration, gameDuration.TotalSeconds, _configuration.AudioDurationTolerancePercent);

        if (check.Verdict == AudioDurationVerdict.Matches)
        {
            return;
        }

        string measurement =
            $"the engine's audio is {check.WavSeconds.ToString("F3", CultureInfo.InvariantCulture)} s against " +
            $"{check.ExpectedSeconds.ToString("F3", CultureInfo.InvariantCulture)} s of video " +
            $"({(check.RelativeError * 100).ToString("F1", CultureInfo.InvariantCulture)} %)";

        string warning = droppedFrames > 0
            ? $"Audio and video lengths disagree for '{wavPath}': {measurement}. " +
              $"{droppedFrames} frame(s) were dropped from the sequence, which is enough to explain it - the " +
              "video really is short of the game time it covers. Fix the dropped frames first."
            : $"Audio and video lengths disagree for '{wavPath}': {measurement}. No frames were dropped, so " +
              "the engine wrote audio of the wrong length. This is the shape of the CSGO regression that " +
              "produced realtime-length audio for movie-length video between 2017 and 2020: the clip plays " +
              "but drifts out of sync as it runs. Re-check the game build before trusting this output.";

        _warnings.Add(warning);
        _session.RaiseError(new CaptureErrorEventArgs(warning, severity: CaptureErrorSeverity.Warning));
        LogAudioDurationMismatch(wavPath, check.WavSeconds, check.ExpectedSeconds, droppedFrames);
    }

    private Dictionary<string, object> BuildStatistics(
        FrameSequenceConsumeResult frames,
        TimeSpan gameDuration,
        bool audioMuxed)
    {
        // The render clock starts at the first consumed frame, not at playback start: the window
        // between them is the demo seek and the tick-scheduled arm, which for a short clip can be
        // longer than the render itself and would report a misleading realtime factor (observed:
        // 0.39x "render" for a render segment actually running above realtime). The preamble is
        // reported separately so nothing is hidden.
        DateTime? renderStartUtc = frames.FirstFrameConsumedUtc ?? _startedUtc;
        double wallClockSeconds = renderStartUtc.HasValue
            ? ((_endedUtc ?? DateTime.UtcNow) - renderStartUtc.Value).TotalSeconds
            : 0;
        double setupSeconds = _startedUtc.HasValue && renderStartUtc.HasValue
            ? (renderStartUtc.Value - _startedUtc.Value).TotalSeconds
            : 0;

        Dictionary<string, object> statistics = new(StringComparer.Ordinal)
        {
            ["movieName"] = _movieName,
            ["frameSource"] = _session.FrameSource.ToString(),
            ["frameFormat"] = _session.FrameSource == InEngineFrameSource.PresentHook
                ? "RawRgba"
                : _configuration.FrameFormat.ToString(),
            ["frameRate"] = _frameRate,
            ["captureFrameRate"] = _captureFrameRate,
            ["playbackSpeed"] = _playbackSpeed,
            ["recordTick"] = _recordTick,
            ["endTick"] = _endTick,
            ["framesConsumed"] = frames.FramesConsumed,
            ["frameBytes"] = frames.BytesConsumed,
            ["gameSeconds"] = gameDuration.TotalSeconds,
            ["wallClockSeconds"] = wallClockSeconds,
            ["setupSeconds"] = setupSeconds,
            ["audioMuxed"] = audioMuxed
        };

        // What actually encoded the frames, rather than what was configured: Auto has been resolved
        // against the local ffmpeg build by now, and the preset may be a default this clip never named.
        if (_encode is { Encoder: not ImagePipeEncoder.Auto } encode)
        {
            statistics["encoder"] = FfmpegService.EncoderName(encode.Encoder);

            if (encode.Preset is { Length: > 0 } preset)
            {
                statistics["encoderPreset"] = preset;
            }
        }

        // A render that finishes inside a single clock tick measures as zero elapsed: DateTime.UtcNow
        // is ~15.6 ms granular on Windows, which a short clip out of a fast encoder beats outright.
        // That is a resolution floor, not an instantaneous render, so clamp the divisor to one tick
        // instead of dropping the two rates: a fast clip reporting no capture rate at all is worse
        // than one reporting a rate that bottoms out at the clock's precision.
        if (renderStartUtc.HasValue)
        {
            double elapsed = Math.Max(wallClockSeconds, ClockResolutionSeconds);
            statistics["captureFps"] = frames.FramesConsumed / elapsed;
            statistics["realtimeFactor"] = gameDuration.TotalSeconds / elapsed;
        }

        return statistics;
    }

    /// <summary>
    ///     Vetoes the clip when ffmpeg exits before the frames have been consumed. Without this the
    ///     coordinator would sit out the whole playback timeout writing frames into a dead pipe.
    /// </summary>
    private void WatchForEarlyEncodeFailure(IImagePipeEncodeSession encode) =>
        _ = encode.Completion.ContinueWith(completed =>
        {
            FfmpegEncodeResult result = completed.Result;
            if (result.Success)
            {
                return;
            }

            LogEncodeDiedEarly(result.ErrorMessage ?? "no error reported");
            VetoClip(new InvalidOperationException(
                $"The offline-capture encoder stopped while the clip was still recording: {result.ErrorMessage}"));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    ///     Vetoes the clip when the frame sequence itself fails mid-render - a frame that disagrees with
    ///     the configured record length, above all.
    /// </summary>
    /// <remarks>
    ///     The encoder cannot report this: the consumer closes the pipe on its way out, so ffmpeg sees a
    ///     clean end of stream and exits successfully. Without this watch the render would run to the
    ///     end tick before anyone found out, which is exactly the wait the fault veto exists to avoid.
    /// </remarks>
    private void WatchForEarlyFrameFailure(IFrameStreamConsumer consumer) =>
        _ = consumer.Completion.ContinueWith(completed =>
        {
            FrameSequenceConsumeResult result = completed.Result;
            if (result.Success)
            {
                return;
            }

            LogFrameSequenceDiedEarly(result.ErrorMessage ?? "no error reported");
            VetoClip(new InvalidOperationException(
                $"The offline-capture frame sequence failed while the clip was still recording: " +
                result.ErrorMessage));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    ///     Fails the clip through the coordinator's fault veto, unless the clip is already past the
    ///     point where a veto would mean anything: once the drain has been signalled, playback is over
    ///     and <see cref="FinalizeAsync" /> is the one reporting the outcome.
    /// </summary>
    private void VetoClip(Exception fault)
    {
        if (Volatile.Read(ref _finished) != 0 || Volatile.Read(ref _drain) != null)
        {
            return;
        }

        _context.FailClip(fault);
    }

    private void StartProgressMonitoring()
    {
        double expectedFrames =
            Math.Max(0, (_endTick - _recordTick) / (double)DemoTicksPerSecond * _captureFrameRate);

        _progressPump = new CaptureProgressPump(
            _ =>
            {
                IFrameStreamConsumer? consumer = _consumer;
                if (consumer is null)
                {
                    return Task.FromResult<CaptureProgressEventArgs?>(null);
                }

                long consumed = consumer.FramesConsumed;
                double wallClockSeconds = _startedUtc.HasValue
                    ? (DateTime.UtcNow - _startedUtc.Value).TotalSeconds
                    : 0;

                Dictionary<string, object> extras = new(StringComparer.Ordinal)
                {
                    ["stagingBacklog"] = consumer.Backlog,
                    ["wallClockSeconds"] = wallClockSeconds,
                    ["expectedFrames"] = expectedFrames
                };

                // Elapsed time is game time, not wall clock: at a fixed timestep the two are unrelated,
                // and the one the caller is waiting on is how much of the clip exists.
                return Task.FromResult<CaptureProgressEventArgs?>(new CaptureProgressEventArgs(
                    TimeSpan.FromSeconds(consumed / _frameRate),
                    consumer.BytesConsumed,
                    consumed,
                    droppedFrames: consumer.DroppedFrames,
                    additionalMetrics: extras));
            },
            progress => _session.RaiseProgress(progress),
            ex => _session.RaiseError(new CaptureErrorEventArgs(
                $"Error reading in-engine capture progress: {ex.Message}", ex, CaptureErrorSeverity.Warning)),
            _logger);

        _progressPump.Start();
    }

    private void StopProgressMonitoring()
    {
        CaptureProgressPump? pump = Interlocked.Exchange(ref _progressPump, null);
        pump?.Dispose();
    }

    private void MarkFinished()
    {
        Interlocked.Exchange(ref _finished, 1);
        StopProgressMonitoring();
        _session.ClipFinished(this);
    }

    /// <summary>
    ///     The normalized real-time companion of a speed clip
    ///     (<see cref="CaptureMetadata.ProduceNormalizedCopy" />): the intermediate's timestamps
    ///     scaled by the playback speed with the video stream copied, and the engine's original
    ///     game-time wav muxed on untouched. Zero tempo-processing passes on either stream, which
    ///     is the entire reason this happens here (while the intermediate and the pristine wav
    ///     still exist) rather than as a post-step; measured, each atempo pass costs 24-35 % of
    ///     transient attack. Failure warns rather than failing the clip; the primary output is
    ///     already safe.
    /// </summary>
    private async Task ProduceNormalizedCopyAsync(
        string? audioPath, List<string> additionalOutputs, CancellationToken cancellationToken)
    {
        if (!_context.Metadata.ProduceNormalizedCopy || Math.Abs(_playbackSpeed - 1.0) <= 0.0001)
        {
            return;
        }

        string normalizedPath = Path.Combine(_context.Metadata.OutputDirectory,
            $"{_context.Metadata.OutputFileName}.normalized.{_context.Metadata.ContainerFormat}");

        FfmpegEncodeResult retimed = await _session.Ffmpeg.RemuxRetimedAsync(
                _intermediatePath,
                _playbackSpeed,
                audioPath,
                normalizedPath,
                _configuration.AudioCodec,
                _context.Metadata.AudioBitrate,
                cancellationToken)
            .ConfigureAwait(false);

        if (retimed.Success)
        {
            additionalOutputs.Add(normalizedPath);
            LogNormalizedCopyProduced(normalizedPath);
        }
        else
        {
            _warnings.Add(
                $"The normalized real-time copy could not be produced: {retimed.ErrorMessage}. " +
                "The speed clip itself is unaffected.");
        }
    }

    /// <summary>
    ///     Removes what the clip no longer needs once it has succeeded: the intermediate video, the
    ///     wav that has been muxed on (unless <c>KeepEngineAudio</c> moves it next to the output
    ///     instead), and any frame the consumer could not unlink.
    /// </summary>
    private void CleanUpAfterSuccess(string? audioPath, List<string> additionalOutputs)
    {
        DeleteBestEffort(_intermediatePath);

        if (audioPath != null)
        {
            if (_configuration.KeepEngineAudio)
            {
                string keptPath = Path.Combine(_context.Metadata.OutputDirectory,
                    $"{_context.Metadata.OutputFileName}.engine-audio.wav");
                try
                {
                    File.Copy(audioPath, keptPath, overwrite: true);
                    File.Delete(audioPath);
                    additionalOutputs.Add(keptPath);
                    LogEngineAudioKept(keptPath);
                }
                catch (Exception ex)
                {
                    _warnings.Add($"Could not keep the engine audio at '{keptPath}': {ex.Message}");
                    DeleteBestEffort(audioPath);
                }
            }
            else
            {
                DeleteBestEffort(audioPath);
            }
        }

        _session.SweepFiles(_session.StagingDirectory, _movieName + "*");
    }

    private void DeleteBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogDeleteFailed(path, ex.Message);
        }
    }

    private string NoFramesMessage() => _session.FrameSource == InEngineFrameSource.PresentHook
        ? $"No frames arrived over the present-hook pipe for movie '{_movieName}'. The plugin connected " +
          "but streamed nothing - startmovie may have been rejected (the hook arms only when it executes), " +
          "or the engine presented no frames during the armed window. csvg.log next to cs2.exe has the " +
          "plugin's own account."
        : $"The engine wrote no frames for movie '{_movieName}'. Nothing matching " +
        $"'{_movieName}{new string('?', _configuration.EngineOutput.FrameNumberDigits)}." +
        $"{_configuration.FrameExtension()}' appeared in '{_session.StagingDirectory}' or its timestamped " +
        "subdirectories. Either startmovie was rejected, or the engine writes its frames somewhere other " +
        "than where this provider looks - the Capture:InEngine:EngineOutput settings describe the layout " +
        "observed on CS2 build 2000879, so on a newer build start by re-checking those.";

    private string MissingAudioMessage() =>
        $"The clip asked for audio, but no file matching '{AudioSearchPattern()}' written since the clip " +
        $"started was found under '{_session.AudioDirectory}'. On CS2 build 2000879 the engine writes it " +
        "to <csgo>/movie/<timestamp>/<name>.wav - note that root differs from the frames'. Set " +
        "Capture:InEngine:AudioDirectory and Capture:InEngine:EngineOutput:AudioFileSearchPattern if a " +
        "newer build moved it, or set Capture:InEngine:MissingAudioBehavior to WarnAndContinue to accept " +
        "silent clips.";

    private CaptureResult Failed(string outputPath, string error) => new()
    {
        Success = false,
        OutputFilePath = outputPath,
        Duration = TimeSpan.Zero,
        ErrorMessage = error,
        StartTime = _startedUtc,
        EndTime = _endedUtc,
        Warnings = [.. _warnings]
    };

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message =
            "Clip starts at tick {StartTick}, before the engine's command clamp at tick {Clamp}; recording " +
            "from the clamp instead")]
    partial void LogStartTickClamped(int startTick, int clamp);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Offline capture armed for '{MovieName}' ({Encoder} at {FrameRate} fps)")]
    partial void LogEncodeStarted(string movieName, string encoder, double frameRate);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Frame drain signalled with {Backlog} frame(s) still in staging")]
    partial void LogDrainSignalled(int backlog);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Offline capture finished '{MovieName}': {Frames} frames, {Seconds:F2} s of game time -> {Path}")]
    partial void LogClipFinished(string movieName, long frames, double seconds, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Error finalizing an in-engine clip")]
    partial void LogFinalizeFailed(Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Error cleaning up an aborted in-engine clip")]
    partial void LogAbortCleanupFailed(Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Aborted in-engine clip '{MovieName}' ({Reason})")]
    partial void LogClipAborted(string movieName, string reason);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Kept the staging files for '{MovieName}' in {Directory} after a failure")]
    partial void LogStagingKept(string directory, string movieName);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "Deleted the engine's movie audio for an audio-disabled clip: {Path}")]
    partial void LogDiscardedAudio(string path);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message =
            "Movie audio {Path} is {WavSeconds:F3} s against {VideoSeconds:F3} s of video ({Dropped} frame(s) " +
            "dropped)")]
    partial void LogAudioDurationMismatch(string path, double wavSeconds, double videoSeconds, long dropped);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "The offline-capture encoder exited while the clip was still recording: {Error}")]
    partial void LogEncodeDiedEarly(string error);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "The offline-capture frame sequence failed while the clip was still recording: {Error}")]
    partial void LogFrameSequenceDiedEarly(string error);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Could not delete {Path}: {Error}")]
    partial void LogDeleteFailed(string path, string error);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information,
        Message = "Produced the normalized real-time copy at {Path} (video retimed by stream copy, original audio)")]
    partial void LogNormalizedCopyProduced(string path);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information,
        Message = "Kept the engine's original game-time audio at {Path}")]
    partial void LogEngineAudioKept(string path);

    #endregion
}
