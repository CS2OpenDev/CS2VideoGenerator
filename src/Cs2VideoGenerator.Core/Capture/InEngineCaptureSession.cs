#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     The in-engine capture backend for one CS2 run: owns the staging directory the engine dumps
///     frames into, the encoder choice, and one <see cref="InEngineClipCapture" /> at a time.
/// </summary>
/// <remarks>
///     <para>
///         Nothing here talks to the engine. Bring-up runs before CS2 launches, and everything this
///         backend asks the engine to do is declared as a <see cref="TickCommandPlan" /> on the clip
///         and armed by <see cref="ClipCaptureCoordinator" />.
///     </para>
///     <para>
///         This provider is validated against a real CS2 build (2000879). See
///         <see cref="InEngineEngineOutput" /> for the few facts about <c>startmovie</c> that are
///         still assumptions.
///     </para>
/// </remarks>
public sealed partial class InEngineCaptureSession : ICaptureSession
{
    private readonly InEngineCaptureConfiguration _configuration;
    private readonly CaptureSessionContext _context;
    private readonly IFfmpegService _ffmpeg;
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly Func<string> _resolveDefaultAudioDirectory;
    private readonly Func<string> _resolveDefaultStagingDirectory;

    private string? _audioDirectory;
    private InEngineClipCapture? _currentClip;
    private bool _disposed;
    private bool _initialized;
    private string? _stagingDirectory;

    /// <summary>
    ///     Creates a session. Nothing is resolved or created until
    ///     <see cref="InitializeAsync" />.
    /// </summary>
    /// <param name="configuration">The bound <c>Capture:InEngine</c> configuration.</param>
    /// <param name="context">The engine session this capture session belongs to.</param>
    /// <param name="ffmpeg">The ffmpeg service that encodes the frame pipe and muxes the audio.</param>
    /// <param name="resolveDefaultStagingDirectory">
    ///     Supplies the staging directory when the configuration does not name one. A function rather
    ///     than a path because working it out needs the CS2 installation, which throws when there is
    ///     none; a host that never captures anything must not be made to care.
    /// </param>
    /// <param name="resolveDefaultAudioDirectory">
    ///     Supplies the movie-audio directory when the configuration does not name one, on the same
    ///     terms.
    /// </param>
    /// <param name="logger">Logger; null falls back to a no-op logger.</param>
    /// <param name="frameSource">
    ///     Where clips get their frames: the engine's frame files (default) or the plugin's present
    ///     hook. Fixed per session by the factory.
    /// </param>
    /// <param name="providerName">
    ///     The provider name this session reports; null means the file-based provider's.
    /// </param>
    public InEngineCaptureSession(
        InEngineCaptureConfiguration configuration,
        CaptureSessionContext context,
        IFfmpegService ffmpeg,
        Func<string> resolveDefaultStagingDirectory,
        Func<string> resolveDefaultAudioDirectory,
        ILogger? logger = null,
        InEngineFrameSource frameSource = InEngineFrameSource.EngineFiles,
        string? providerName = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _resolveDefaultStagingDirectory = resolveDefaultStagingDirectory
                                          ?? throw new ArgumentNullException(nameof(resolveDefaultStagingDirectory));
        _resolveDefaultAudioDirectory = resolveDefaultAudioDirectory
                                        ?? throw new ArgumentNullException(nameof(resolveDefaultAudioDirectory));
        _logger = logger ?? NullLogger.Instance;
        FrameSource = frameSource;
        ProviderName = providerName ?? InEngineCaptureSessionFactory.ProviderKey;

        Finalizer = new OutputArtifactFinalizer(_logger);
        AudioLocator = new InEngineAudioLocator(_logger);
    }

    /// <summary>
    ///     Where this session's clips get their frames: the engine's frame files, or the plugin's
    ///     present hook. Fixed per session by the factory that created it.
    /// </summary>
    internal InEngineFrameSource FrameSource { get; }

    /// <summary>
    ///     Shared file-settling and rename helper, handed to clips and to the frame consumer.
    /// </summary>
    internal OutputArtifactFinalizer Finalizer { get; }

    /// <summary>
    ///     Shared wav discovery and duration guard.
    /// </summary>
    internal InEngineAudioLocator AudioLocator { get; }

    internal InEngineCaptureConfiguration Configuration => _configuration;

    internal IFfmpegService Ffmpeg => _ffmpeg;

    internal ILogger Logger => _logger;

    /// <summary>
    ///     Cancelled when the owning engine session goes away.
    /// </summary>
    internal CancellationToken SessionToken => _context.SessionToken;

    /// <summary>
    ///     The least-privilege engine view. Held for diagnostics: the recording brackets travel as a
    ///     <see cref="TickCommandPlan" />, so this backend never issues a command itself.
    /// </summary>
    internal IEngineConsole Engine => _context.Engine;

    /// <summary>
    ///     Where the engine's frames are expected, once <see cref="InitializeAsync" /> has resolved it.
    /// </summary>
    internal string StagingDirectory => _stagingDirectory
                                        ?? throw new InvalidOperationException(
                                            "The in-engine capture session has not been initialized");

    /// <summary>
    ///     Where the engine's movie audio is expected, once <see cref="InitializeAsync" /> has resolved
    ///     it.
    /// </summary>
    internal string AudioDirectory => _audioDirectory
                                      ?? throw new InvalidOperationException(
                                          "The in-engine capture session has not been initialized");

    /// <inheritdoc />
    public string ProviderName { get; }

    /// <inheritdoc />
    public CaptureTraits Traits { get; } = new()
    {
        TimingModel = CaptureTimingModel.TickExact,
        UsesEngine = true,
        HonorsFrameRate = true,
        SupportsPauseResume = false
    };

    /// <inheritdoc />
    public event EventHandler<CaptureProgressEventArgs>? ProgressUpdated;

    /// <inheritdoc />
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    /// <remarks>
    ///     Validates the configuration, resolves and creates the staging directory, sweeps whatever a
    ///     previous run left in it, and probes the local ffmpeg build for its encoders. Runs before CS2
    ///     is launched, so none of it may depend on the engine.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The configuration is inconsistent, or the staging directory cannot be created.
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("InEngineCaptureSession.Initialize");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_initialized)
            {
                return;
            }
        }

        IReadOnlyList<string> issues = _configuration.Validate();
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(
                "The in-engine capture configuration cannot be used: " + string.Join(" ", issues));
        }

        string staging = ResolveDirectory(_configuration.StagingDirectory, _resolveDefaultStagingDirectory,
            "staging");
        string audio = ResolveDirectory(_configuration.AudioDirectory, _resolveDefaultAudioDirectory, "movie audio");

        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not create the in-engine capture staging directory '{staging}': {ex.Message}", ex);
        }

        activity?.SetTag("csvg.inengine.staging", staging);
        activity?.SetTag("csvg.inengine.audio", audio);

        // Recursive: the engine writes each movie into a timestamped subdirectory of the staging
        // directory, so a previous run's leftovers live one level down.
        int swept = SweepFiles(staging, "csvg_*", recursive: true);
        if (swept > 0)
        {
            LogSweptStaleFiles(swept, staging);
        }

        FfmpegEncoderProbeResult probe = await _ffmpeg.ProbeEncodersAsync(cancellationToken).ConfigureAwait(false);
        activity?.SetTag("csvg.inengine.encoderProbe", probe.Success);
        WarnAboutEncoderFallback(probe);

        lock (_gate)
        {
            _stagingDirectory = staging;
            _audioDirectory = audio;
            _initialized = true;
        }

        LogSessionInitialized(staging, audio, _configuration.FrameFormat.ToString());
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Sweeps anything left over under this clip's movie name and hands out the clip. One clip at a
    ///     time: the engine records one movie at a time, so a second concurrent clip would have its
    ///     frames folded into the first one's sequence.
    /// </remarks>
    public Task<IClipCapture> BeginClipAsync(ClipCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using Activity? activity = CsvgTracing.StartActivity("InEngineCaptureSession.BeginClip");
        activity?.SetTag("csvg.clip.index", context.ClipIndex);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "The in-engine capture session has not been initialized");
            }

            if (_currentClip != null)
            {
                throw new InvalidOperationException(
                    "A clip is already recording on this in-engine capture session. The engine records one " +
                    "movie at a time, so a second clip's frames would be folded into the first one's sequence.");
            }
        }

        string movieName = InEngineCaptureConfiguration.MovieName(context.ClipIndex);
        SweepFiles(StagingDirectory, movieName + "*", recursive: true);

        InEngineClipCapture clip = new(this, context);

        lock (_gate)
        {
            _currentClip = clip;
        }

        activity?.SetTag("csvg.inengine.movieName", movieName);
        return Task.FromResult<IClipCapture>(clip);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Aborts a clip still in flight. There is no external process or connection to close: the
    ///     engine's own recording is stopped by the clip's abort commands, which the coordinator runs.
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        InEngineClipCapture? clip;
        lock (_gate)
        {
            clip = _currentClip;
        }

        if (clip != null)
        {
            await clip.AbortAsync(CaptureAbortReason.SessionShutdown, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _initialized = false;
        }

        LogSessionShutDown();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogShutdownFailed(ex);
        }

        if (_stagingDirectory != null && !_configuration.KeepStagingOnFailure)
        {
            SweepFiles(_stagingDirectory, "csvg_*");
        }
    }

    /// <summary>
    ///     Deletes everything matching <paramref name="pattern" /> in a directory, and reports how many
    ///     files went. Best effort: a file that will not delete costs disk, not correctness. For the
    ///     audio directory, where a leftover would be worse than that, the clip's own written-after
    ///     check is what actually rejects it.
    /// </summary>
    internal int SweepFiles(string directory, string pattern, bool recursive = false)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, pattern, option))
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    LogSweepFileFailed(path, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSweepFailed(directory, ex.Message);
        }

        return deleted;
    }

    internal void RaiseProgress(CaptureProgressEventArgs args) => ProgressUpdated?.Invoke(this, args);

    internal void RaiseError(CaptureErrorEventArgs args) => ErrorOccurred?.Invoke(this, args);

    /// <summary>
    ///     Releases the current-clip slot, so the next clip can be started.
    /// </summary>
    internal void ClipFinished(InEngineClipCapture clip)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_currentClip, clip))
            {
                _currentClip = null;
            }
        }
    }

    private string ResolveDirectory(string? configured, Func<string> fallback, string what)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        try
        {
            return Path.GetFullPath(fallback());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not work out the in-engine capture {what} directory from the CS2 installation " +
                $"({ex.Message}). Set it explicitly with \"Capture:InEngine:" +
                $"{(what == "staging" ? "StagingDirectory" : "AudioDirectory")}\".", ex);
        }
    }

    /// <summary>
    ///     Reports the case worth knowing about before a capture starts: <c>Auto</c> resolving to the
    ///     software encoder, which turns the intermediate encode from a background cost into the thing
    ///     the render waits on.
    /// </summary>
    private void WarnAboutEncoderFallback(FfmpegEncoderProbeResult probe)
    {
        if (_configuration.IntermediateEncoder != ImagePipeEncoder.Auto)
        {
            return;
        }

        if (!probe.Success)
        {
            LogEncoderProbeFailed(probe.ErrorMessage ?? "unknown error");
            return;
        }

        ImagePipeEncoder resolved = FfmpegService.ResolveEncoder(ImagePipeEncoder.Auto, probe.Encoders);
        if (resolved == ImagePipeEncoder.X264)
        {
            LogEncoderFallback(FfmpegService.EncoderName(resolved));
        }
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "In-engine capture ready: staging {Staging}, audio {Audio}, frame format {Format}")]
    partial void LogSessionInitialized(string staging, string audio, string format);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Swept {Count} stale file(s) from the in-engine staging directory {Directory}")]
    partial void LogSweptStaleFiles(int count, string directory);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Could not delete {Path} from the in-engine staging directory: {Error}")]
    partial void LogSweepFileFailed(string path, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Could not sweep the in-engine staging directory {Directory}: {Error}")]
    partial void LogSweepFailed(string directory, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message =
            "Could not probe the local ffmpeg build for its encoders ({Error}); the intermediate encode will " +
            "fall back to software H.264")]
    partial void LogEncoderProbeFailed(string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message =
            "No hardware H.264 encoder in this ffmpeg build, so offline capture will encode with {Encoder}. " +
            "The render waits on the encoder, so expect it to be the bottleneck.")]
    partial void LogEncoderFallback(string encoder);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "In-engine capture session shut down")]
    partial void LogSessionShutDown();

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Error shutting down the in-engine capture session")]
    partial void LogShutdownFailed(Exception exception);

    #endregion
}
