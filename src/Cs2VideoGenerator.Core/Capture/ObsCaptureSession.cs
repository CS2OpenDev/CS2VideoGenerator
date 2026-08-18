#region

using System.Diagnostics;
using System.Text.Json;
using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.ProcessManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Obs.NET;
using Obs.NET.Models;
using Obs.NET.Requests;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     OBS Studio capture for one CS2 run, over obs-websocket v5. Owns everything that exists once per
///     run: the WebSocket connection, the OBS process when CSVG launched it, the CSVG scene and its
///     CS2 game-capture source, and the audio-mute controller. Clips are recorded by
///     <see cref="ObsClipCapture" />.
/// </summary>
/// <remarks>
///     <see cref="InitializeAsync" /> deliberately runs before CS2 launches: OBS's game-capture source
///     has to exist and be hooked before there is a window to capture.
/// </remarks>
public sealed partial class ObsCaptureSession : ICaptureSession, ICaptureStateSink, IDisposable
{
    private const string CsvgSceneName = "Cs2VideoGenerator";

    private const string Cs2SourceName = "Counter-Strike 2";

    /// <summary>
    ///     OBS input kind for game capture on Windows.
    /// </summary>
    private const string GameCaptureInputKind = "game_capture";

    /// <summary>
    ///     obs-websocket RequestStatus code returned while OBS has accepted the WebSocket
    ///     connection but has not finished initializing and cannot yet service the request
    ///     (server comment: "OBS is not ready to perform the request").
    /// </summary>
    internal const int ObsNotReadyStatusCode = 207;

    /// <summary>
    ///     Maximum time the synchronous <see cref="Dispose" /> blocks on the async teardown: enough
    ///     to cover the 30-second graceful OBS shutdown plus the post-kill monitor wait.
    /// </summary>
    private const int SyncDisposeTimeoutSeconds = 40;

    private readonly ObsCaptureConfiguration _configuration;
    private readonly CaptureSessionContext _context;
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly CaptureMetricsAccessor _metricsAccessor;
    private readonly bool _ownsProcessManager;
    private readonly ObsProcessManager _processManager;

    private ObsAudioMuteController? _audioMuteController;
    private ObsClipCapture? _activeClip;
    private int _disposeStarted;
    private bool _initialized;
    private CaptureState _metricsState = CaptureState.NotInitialized;
    private ObsWsClient? _obsClient;
    private bool _weStartedObs;

    /// <summary>
    ///     Creates a session that owns its own <see cref="ObsProcessManager" />. OBS executable
    ///     discovery goes through <paramref name="pathDiscoveryService" /> when supplied, otherwise
    ///     through the default cached discovery service.
    /// </summary>
    /// <param name="configuration">OBS connection and launch configuration.</param>
    /// <param name="context">The engine session this capture session belongs to.</param>
    /// <param name="logger">
    ///     Logger for capture and process events; null is supported and falls back to a no-op logger.
    /// </param>
    /// <param name="pathDiscoveryService">
    ///     Optional path discovery override; null uses <see cref="CachedPathDiscoveryService.CreateDefault" />.
    /// </param>
    public ObsCaptureSession(
        ObsCaptureConfiguration configuration,
        CaptureSessionContext context,
        ILogger<ObsCaptureSession>? logger = null,
        IPathDiscoveryService? pathDiscoveryService = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _context = context ?? throw new ArgumentNullException(nameof(context));

        // A null logger is genuinely supported: fall back to no-op logger implementations so
        // source-generated log calls and the process manager never dereference a null logger.
        _logger = logger ?? NullLogger<ObsCaptureSession>.Instance;
        ILoggerFactory loggerFactory = logger != null ? new SingleLoggerFactory(logger) : NullLoggerFactory.Instance;

        // OBS discovery always goes through the single cached discovery implementation.
        _processManager = new ObsProcessManager(
            loggerFactory,
            pathDiscoveryService ?? CachedPathDiscoveryService.CreateDefault(loggerFactory));
        _ownsProcessManager = true;

        Finalizer = new OutputArtifactFinalizer(_logger);
        _metricsAccessor = CreateWeakMetricsAccessor(this);
        MetricsProviders.RegisterCaptureProvider(_metricsAccessor);
    }

    /// <summary>
    ///     Creates a session that uses a caller-supplied <see cref="ObsProcessManager" /> (the
    ///     dependency-injected singleton). The manager is not disposed with the session: sessions come
    ///     and go with CS2 runs, so a shared manager has to survive them and stay usable for the next
    ///     run. The OBS process it started is still stopped in <see cref="ShutdownAsync" />.
    /// </summary>
    /// <param name="configuration">OBS connection and launch configuration.</param>
    /// <param name="context">The engine session this capture session belongs to.</param>
    /// <param name="processManager">The OBS process manager to launch/stop OBS with.</param>
    /// <param name="logger">
    ///     Logger for capture events; null is supported and falls back to a no-op logger.
    /// </param>
    public ObsCaptureSession(
        ObsCaptureConfiguration configuration,
        CaptureSessionContext context,
        ObsProcessManager processManager,
        ILogger<ObsCaptureSession>? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _ownsProcessManager = false;
        _logger = logger ?? NullLogger<ObsCaptureSession>.Instance;

        Finalizer = new OutputArtifactFinalizer(_logger);
        _metricsAccessor = CreateWeakMetricsAccessor(this);
        MetricsProviders.RegisterCaptureProvider(_metricsAccessor);
    }

    /// <summary>
    ///     Shared output-artifact handling (settle wait plus rename with backoff) for the clips this
    ///     session hands out.
    /// </summary>
    internal OutputArtifactFinalizer Finalizer { get; }

    /// <summary>
    ///     The logger clips log through, so OBS capture events share one category.
    /// </summary>
    internal ILogger Logger => _logger;

    /// <inheritdoc />
    public string ProviderName => ObsCaptureSessionFactory.ProviderKey;

    /// <inheritdoc />
    /// <remarks>
    ///     OBS records the CS2 window in real time, so a clip is as long as playback took on the wall
    ///     clock and the frame rate comes from the user's OBS profile; the metadata's frame rate,
    ///     resolution and encoder fields are reporting-only here. Pause/resume is supported and exposed
    ///     on <see cref="ObsClipCapture" />.
    /// </remarks>
    public CaptureTraits Traits { get; } = new()
    {
        TimingModel = CaptureTimingModel.WallClock,
        UsesEngine = false,
        HonorsFrameRate = false,
        SupportsPauseResume = true
    };

    /// <inheritdoc />
    /// <remarks>Raised roughly once per second while recording, with elapsed time, bytes written, and bitrate.</remarks>
    public event EventHandler<CaptureProgressEventArgs>? ProgressUpdated;

    /// <inheritdoc />
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    /// <remarks>
    ///     Connects to a running OBS instance over WebSocket, or - when
    ///     <see cref="CaptureConfiguration.AutoStart" /> is enabled and no instance is reachable -
    ///     launches OBS and retries the connection. Once connected it waits out the obs-websocket
    ///     "not ready" window, creates (or reuses) the "Cs2VideoGenerator" scene with a CS2
    ///     game-capture source, switches to it, and verifies OBS is ready to record. Per-clip settings
    ///     (record directory, audio policy) are applied in <see cref="BeginClipAsync" />.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     No connection to OBS could be established, or OBS never became ready (also raised through
    ///     <see cref="ErrorOccurred" />).
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.Initialize");
        activity?.SetTag("obs.host", _configuration.Host);
        activity?.SetTag("obs.port", _configuration.Port);
        activity?.SetTag("obs.autoStart", _configuration.AutoStart);
        activity?.SetTag("obs.sessionId", _context.EngineSessionId);

        if (_initialized && _obsClient != null)
        {
            activity?.SetTag("obs.alreadyConnected", true);
            LogObsAlreadyConnected();
            return;
        }

        ReportMetricsState(CaptureState.PreparingEnvironment);

        _obsClient ??= new ObsWsClient(new ObsWsClientOptions
        {
            Host = _configuration.Host,
            Port = _configuration.Port,
            Password = _configuration.Password,
            MaxReconnectAttempts = _configuration.MaxRetryAttempts,
            ReconnectDelay = TimeSpan.FromMilliseconds(2000)
        });

        bool connected = false;
        try
        {
            await _obsClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
            connected = true;
            LogConnectedExistingObs();
        }
        catch (Exception ex)
        {
            LogCouldNotConnectExistingObs(ex.Message);
        }

        if (!connected && _configuration.AutoStart)
        {
            await _processManager.LaunchAsync(_configuration, cancellationToken).ConfigureAwait(false);
            _weStartedObs = true;

            int maxAttempts = _configuration.MaxRetryAttempts;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                activity?.SetTag("obs.connect.attempt", attempt);
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

                try
                {
                    await _obsClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    connected = true;
                    LogConnectedAfterStart(attempt);
                    break;
                }
                catch (Exception ex)
                {
                    LogConnectionAttemptFailed(attempt, ex.Message);
                    activity?.SetTag("obs.connect.error", ex.Message);
                }
            }
        }

        if (!connected)
        {
            activity?.SetTag("obs.connected", false);
            ReportMetricsState(CaptureState.Error);
            InvalidOperationException connectException = new("Could not establish connection to OBS");
            RaiseError(new CaptureErrorEventArgs(
                connectException.Message, connectException, CaptureErrorSeverity.Critical));
            throw connectException;
        }

        activity?.SetTag("obs.connected", true);

        // The obs-websocket server accepts connections before OBS finishes initializing its
        // request subsystems, so a freshly-connected client can still get RequestStatus 207
        // ("not ready") on the first real request. Gate on OBS actually being ready before
        // touching capture configuration.
        await WaitForObsReadyAsync(cancellationToken).ConfigureAwait(false);

        await EnsureCsvgSceneAndSourceAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _obsClient
                .SetCurrentProgramSceneAsync(CsvgSceneName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            LogSwitchedScene(CsvgSceneName);
        }
        catch (Exception ex)
        {
            LogCouldNotSwitchScene(ex, CsvgSceneName);
        }

        // OBS WebSocket calls can return before the operation completes inside OBS, so verify the
        // scene/source landed and give the game-capture source a moment to initialize.
        await VerifyObsReadyAsync(cancellationToken).ConfigureAwait(false);

        _initialized = true;
        ReportMetricsState(CaptureState.Ready);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Points OBS's recording output at this clip's directory and applies the clip's audio policy,
    ///     then hands back the clip that brackets the recording.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The session has not been initialized (or has been shut down).
    /// </exception>
    public async Task<IClipCapture> BeginClipAsync(ClipCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.BeginClip");
        activity?.SetTag("obs.clipIndex", context.ClipIndex);
        activity?.SetTag("obs.outputDirectory", context.Metadata.OutputDirectory);

        if (!_initialized || _obsClient == null)
        {
            throw new InvalidOperationException(
                "Cannot begin a clip: the OBS capture session is not initialized. Call InitializeAsync first.");
        }

        if (!Directory.Exists(context.Metadata.OutputDirectory))
        {
            Directory.CreateDirectory(context.Metadata.OutputDirectory);
            LogCreatedOutputDirectory(context.Metadata.OutputDirectory);
        }

        await _obsClient.SetRecordDirectoryAsync(context.Metadata.OutputDirectory, cancellationToken)
            .ConfigureAwait(false);
        LogSetRecordDirectory(context.Metadata.OutputDirectory);

        await ApplyAudioCapturePolicyAsync(context.Metadata, cancellationToken).ConfigureAwait(false);

        ObsClipCapture clip = new(this, context);
        lock (_gate)
        {
            _activeClip = clip;
        }

        return clip;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Aborts a clip still recording, restores any OBS audio-input mutes, disconnects the
    ///     WebSocket client, and shuts down the OBS process when this session started it (and
    ///     <see cref="ObsCaptureConfiguration.ShutdownOnDispose" /> is enabled - an OBS instance the
    ///     user already had running is never closed). The process manager survives, so the session can
    ///     be initialized again.
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.Shutdown");
        await TearDownAsync(activity).ConfigureAwait(false);
        ReportMetricsState(CaptureState.NotInitialized);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Runs the same teardown as <see cref="ShutdownAsync" /> and additionally releases the
    ///     capture metrics registration and (when this session created it) the process manager.
    ///     Idempotent.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: the container may dispose this instance both as IAsyncDisposable and via the
        // sync Dispose() bridge, and hosts may dispose it alongside the client that owns it.
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.DisposeAsync");

        // Release the static metrics registration so this instance is not sampled after disposal.
        MetricsProviders.UnregisterCaptureProvider(_metricsAccessor);

        await TearDownAsync(activity).ConfigureAwait(false);

        if (_ownsProcessManager)
        {
            await _processManager.DisposeAsync().ConfigureAwait(false);
        }

        ReportMetricsState(CaptureState.Disposed);

        CsvgMetrics.CaptureSessionCount.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("result", "disposed"));
    }

    /// <summary>
    ///     Synchronous disposal bridge. Microsoft.Extensions.DependencyInjection throws when a service
    ///     provider is disposed synchronously while holding a service that only implements
    ///     <see cref="IAsyncDisposable" /> - so a plain <c>using var provider = ...</c> in a consumer
    ///     app would crash without this. Runs <see cref="DisposeAsync" /> on the thread pool (off any
    ///     ambient SynchronizationContext) bounded to 40 seconds - enough to cover the 30-second
    ///     graceful OBS shutdown (which lets OBS finalize an in-progress recording) plus the post-kill
    ///     monitor wait. Prefer <see cref="DisposeAsync" />, which awaits the full teardown without a
    ///     time bound.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        try
        {
            if (!Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(SyncDisposeTimeoutSeconds)))
            {
                LogSyncDisposeTimeout(SyncDisposeTimeoutSeconds);
            }
        }
        catch (AggregateException ex)
        {
            LogSyncDisposeError(ex.GetBaseException());
        }
    }

    /// <summary>
    ///     Latches the coordinator's capture state so the capture metrics gauges report what capture is
    ///     currently doing. The state machine itself belongs to <see cref="ClipCaptureCoordinator" />;
    ///     this session only records the last value.
    /// </summary>
    void ICaptureStateSink.OnCaptureStateChanged(CaptureState state) => ReportMetricsState(state);

    /// <summary>
    ///     Determines whether an <see cref="ObsRequestException" /> represents the transient
    ///     "OBS not ready" condition (RequestStatus <see cref="ObsNotReadyStatusCode" />). Any other
    ///     status code, or an exception raised without status details (a null
    ///     <see cref="ObsRequestException.Code" />, e.g. a transport/deserialization failure), is
    ///     treated as a genuine failure and is not retried.
    /// </summary>
    internal static bool IsObsNotReady(ObsRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Code == ObsNotReadyStatusCode;
    }

    /// <summary>
    ///     Creates a metrics accessor that references the session weakly, so the static metrics
    ///     registry never keeps one alive after it becomes unreferenced.
    /// </summary>
    private static CaptureMetricsAccessor CreateWeakMetricsAccessor(ObsCaptureSession instance)
    {
        WeakReference<ObsCaptureSession> weakRef = new(instance);
        return new CaptureMetricsAccessor(
            () => weakRef.TryGetTarget(out ObsCaptureSession? named) ? named.ProviderName : null,
            () => weakRef.TryGetTarget(out ObsCaptureSession? stated) ? (long)stated.MetricsState : null);
    }

    /// <summary>
    ///     The capture state reported to the metrics gauges: this session's own bring-up progress,
    ///     overwritten by the coordinator's per-clip transitions while a clip runs.
    /// </summary>
    private CaptureState MetricsState
    {
        get
        {
            lock (_gate)
            {
                return _metricsState;
            }
        }
    }

    private void ReportMetricsState(CaptureState state)
    {
        lock (_gate)
        {
            _metricsState = state;
        }
    }

    /// <summary>
    ///     The shared teardown behind <see cref="ShutdownAsync" /> and <see cref="DisposeAsync" />:
    ///     stop whatever is recording, unmute, disconnect, and stop the OBS process CSVG launched.
    ///     Every step is best-effort; teardown runs on failure paths too.
    /// </summary>
    private async Task TearDownAsync(Activity? activity)
    {
        ObsClipCapture? activeClip;
        lock (_gate)
        {
            activeClip = _activeClip;
            _activeClip = null;
        }

        // A clip still recording would leave OBS writing to disk after the session is gone (and, on
        // the CS2-kill path, recording a window that is about to vanish).
        if (activeClip != null)
        {
            LogAbortingActiveClipOnTeardown();
            await activeClip.AbortAsync(CaptureAbortReason.SessionShutdown, CancellationToken.None)
                .ConfigureAwait(false);
        }

        bool shouldShutdownObs = _weStartedObs && _configuration.ShutdownOnDispose;
        activity?.SetTag("obs.shouldShutdown", shouldShutdownObs);
        activity?.SetTag("obs.weStartedObs", _weStartedObs);

        // Backstop for failure paths where a clip never finalized (e.g. a mid-capture error):
        // restore any OBS audio input mute states before the connection goes away. The controller
        // wraps the client disposed below, so it must not survive teardown - a re-initialized session
        // builds a new one against the new connection.
        await RestoreAudioInputMutesAsync().ConfigureAwait(false);
        _audioMuteController = null;

        if (_obsClient != null)
        {
            try
            {
                await _obsClient.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorDisposingObsClient(ex);
            }

            _obsClient = null;
        }

        if (shouldShutdownObs)
        {
            LogShuttingDownObs();
            try
            {
                await _processManager.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorStoppingObsProcess(ex);
            }
        }

        _weStartedObs = false;
        _initialized = false;
    }

    /// <summary>
    ///     The connected OBS client, for the clips this session owns.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has no live connection.</exception>
    internal ObsWsClient RequireClient() =>
        _obsClient ?? throw new InvalidOperationException(
            "The OBS capture session has no live WebSocket connection.");

    internal void RaiseProgress(CaptureProgressEventArgs args) => ProgressUpdated?.Invoke(this, args);

    internal void RaiseError(CaptureErrorEventArgs args) => ErrorOccurred?.Invoke(this, args);

    /// <summary>
    ///     Forgets a clip that has finished, so teardown does not try to abort it.
    /// </summary>
    internal void ClipFinished(ObsClipCapture clip)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeClip, clip))
            {
                _activeClip = null;
            }
        }
    }

    /// <summary>
    ///     Applies the audio-capture policy for one clip: when
    ///     <see cref="CaptureMetadata.CaptureAudio" /> is false, mutes the OBS audio inputs (recording
    ///     each input's prior mute state); when true (the default), restores any mutes still applied
    ///     from a previous clip (e.g. after a mid-capture failure) so an audio-enabled clip is never
    ///     silently muted. Muting is runtime-only; the user's OBS profile is never permanently
    ///     modified.
    /// </summary>
    internal async Task ApplyAudioCapturePolicyAsync(CaptureMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.CaptureAudio)
        {
            if (_audioMuteController != null)
            {
                await _audioMuteController.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        _audioMuteController ??= new ObsAudioMuteController(new ObsWsAudioClient(RequireClient()), _logger);

        LogDisablingAudioCapture();
        await _audioMuteController.MuteAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Best-effort restore of the OBS audio input mute states recorded when audio capture was
    ///     disabled. Safe to call from any terminal path (clip finalize, shutdown, dispose);
    ///     idempotent. Uses no cancellation token so cancellation can never leak muted inputs.
    /// </summary>
    internal async Task RestoreAudioInputMutesAsync()
    {
        if (_audioMuteController == null)
        {
            return;
        }

        try
        {
            await _audioMuteController.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorRestoringAudioMutes(ex);
            RaiseError(new CaptureErrorEventArgs(
                $"Error restoring OBS audio input mute states: {ex.Message}", ex, CaptureErrorSeverity.Warning));
        }
    }

    /// <summary>
    ///     Waits until OBS is ready to service requests after a WebSocket connection is established.
    ///     The obs-websocket server accepts connections before OBS finishes initializing, so early
    ///     requests can fail with RequestStatus <see cref="ObsNotReadyStatusCode" /> ("not ready").
    ///     Probes with a lightweight <c>GetVersion</c> request and, on a not-ready response, retries
    ///     every <see cref="ObsCaptureConfiguration.ReadinessRetryDelayMs" /> until OBS is ready or
    ///     <see cref="ObsCaptureConfiguration.ReadinessTimeoutMs" /> has elapsed. Any other request
    ///     failure is surfaced immediately rather than retried.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     OBS did not become ready before the configured timeout.
    /// </exception>
    private async Task WaitForObsReadyAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.WaitForObsReady");
        ObsWsClient client = RequireClient();

        TimeSpan retryDelay = TimeSpan.FromMilliseconds(Math.Max(0, _configuration.ReadinessRetryDelayMs));
        TimeSpan timeout = TimeSpan.FromMilliseconds(Math.Max(0, _configuration.ReadinessTimeoutMs));
        activity?.SetTag("obs.ready.retryDelayMs", _configuration.ReadinessRetryDelayMs);
        activity?.SetTag("obs.ready.timeoutMs", _configuration.ReadinessTimeoutMs);

        Stopwatch stopwatch = Stopwatch.StartNew();
        int attempt = 0;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // GetVersion is a general request OBS answers only once it is ready to service
                // requests; a successful response means capture configuration can proceed safely.
                await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                activity?.SetTag("obs.ready.attempts", attempt);
                LogObsReady(attempt, stopwatch.Elapsed.TotalSeconds);
                return;
            }
            catch (ObsRequestException ex) when (IsObsNotReady(ex))
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    activity?.SetTag("obs.ready.timedOut", true);
                    InvalidOperationException timeoutException = new(
                        "OBS accepted the WebSocket connection but did not become ready to service " +
                        $"requests within {timeout.TotalSeconds:F0}s (obs-websocket RequestStatus " +
                        $"{ObsNotReadyStatusCode}). Increase Capture:OBS:ReadinessTimeoutMs if OBS " +
                        "needs longer to start.", ex);
                    RaiseError(new CaptureErrorEventArgs(
                        timeoutException.Message, timeoutException, CaptureErrorSeverity.Critical));
                    throw timeoutException;
                }

                LogObsNotReadyRetrying(attempt, retryDelay.TotalSeconds, ex.Message);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Verifies that OBS is ready for capture by checking that:
    ///     - The CSVG scene exists and is active
    ///     - The game capture source exists in the scene
    ///     - OBS is not currently recording (ready to start)
    /// </summary>
    private async Task VerifyObsReadyAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.VerifyObsReady");
        ObsWsClient client = RequireClient();

        const int maxRetries = 5;
        const int retryDelayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            activity?.SetTag("obs.verify.attempt", attempt);

            try
            {
                bool sceneExists = await SceneExistsAsync(CsvgSceneName, cancellationToken).ConfigureAwait(false);
                if (!sceneExists)
                {
                    LogVerificationFailed("Scene does not exist", attempt);
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool sourceExists = await SourceExistsInSceneAsync(CsvgSceneName, Cs2SourceName, cancellationToken)
                    .ConfigureAwait(false);
                if (!sourceExists)
                {
                    LogVerificationFailed("Source does not exist in scene", attempt);
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                GetRecordStatusResponse? recordStatus = await client.GetRecordStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (recordStatus?.OutputActive == true)
                {
                    LogVerificationFailed("OBS is currently recording", attempt);
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // All checks passed. Brief stabilization delay so OBS can finish initializing the
                // game capture source.
                LogObsVerified();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("obs.verify.success", true);
                return;
            }
            catch (Exception ex)
            {
                LogVerificationError(ex, attempt);
                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Verification failed after every retry: warn but proceed; capture may still work.
        LogVerificationFailedAfterRetries(maxRetries);
        activity?.SetTag("obs.verify.success", false);
    }

    private async Task EnsureCsvgSceneAndSourceAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.EnsureCsvgSceneAndSource");
        ObsWsClient client = RequireClient();

        bool sceneExists = await SceneExistsAsync(CsvgSceneName, cancellationToken).ConfigureAwait(false);

        if (!sceneExists)
        {
            LogCreatingScene(CsvgSceneName);
            await client.CreateSceneAsync(CsvgSceneName, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("obs.scene.created", true);
        }
        else
        {
            activity?.SetTag("obs.scene.existed", true);
        }

        bool sourceExistsInScene = await SourceExistsInSceneAsync(CsvgSceneName, Cs2SourceName, cancellationToken)
            .ConfigureAwait(false);

        if (!sourceExistsInScene)
        {
            // The input may exist globally but live in another scene.
            bool inputExistsGlobally = await InputExistsAsync(Cs2SourceName, cancellationToken).ConfigureAwait(false);

            if (inputExistsGlobally)
            {
                LogAddingExistingSourceToScene(Cs2SourceName, CsvgSceneName);
                await AddExistingSourceToSceneAsync(CsvgSceneName, Cs2SourceName, cancellationToken)
                    .ConfigureAwait(false);
                activity?.SetTag("obs.source.addedToScene", true);
            }
            else
            {
                await CreateCs2GameCaptureSourceAsync(CsvgSceneName, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("obs.source.created", true);
            }
        }
        else
        {
            activity?.SetTag("obs.source.existed", true);
        }

        // Always apply the "Fit to screen" transform.
        await ApplyFitToScreenTransformAsync(CsvgSceneName, Cs2SourceName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SceneExistsAsync(string sceneName, CancellationToken cancellationToken)
    {
        try
        {
            GetSceneListResponse? response = await RequireClient().GetSceneListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response?.Scenes == null)
            {
                return false;
            }

            return response.Scenes.Any(scene =>
                string.Equals(scene.SceneName, sceneName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LogErrorCheckingSceneExists(ex, sceneName);
            return false;
        }
    }

    private async Task<bool> SourceExistsInSceneAsync(string sceneName, string sourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            GetSceneItemListResponse? response = await RequireClient()
                .GetSceneItemListAsync(sceneName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response?.SceneItems == null)
            {
                return false;
            }

            return response.SceneItems.Any(item =>
                string.Equals(item.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LogErrorCheckingSourceExists(ex, sourceName, sceneName);
            return false;
        }
    }

    /// <summary>
    ///     Checks if an input with the given name exists globally in OBS.
    /// </summary>
    private async Task<bool> InputExistsAsync(string inputName, CancellationToken cancellationToken)
    {
        try
        {
            GetInputListResponse? response = await RequireClient()
                .GetInputListAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response?.Inputs == null)
            {
                return false;
            }

            return response.Inputs.Any(input =>
                string.Equals(input.InputName, inputName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LogErrorCheckingInputExists(ex, inputName);
            return false;
        }
    }

    private async Task AddExistingSourceToSceneAsync(string sceneName, string sourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            await RequireClient().CreateSceneItemAsync(
                sceneName: sceneName,
                sourceName: sourceName,
                sceneItemEnabled: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            LogAddedSourceToScene(sourceName, sceneName);
        }
        catch (Exception ex)
        {
            LogErrorAddingSourceToScene(ex, sourceName, sceneName);
            throw;
        }
    }

    private async Task CreateCs2GameCaptureSourceAsync(string sceneName, CancellationToken cancellationToken)
    {
        LogCreatingGameCaptureSource(Cs2SourceName, sceneName);

        // Game capture settings for CS2:
        // - capture_mode: "window" (Capture specific window)
        // - window: Window identifier in OBS format "Title:Class:Executable"
        // - priority: 1 (Match title, otherwise find window of same type)
        // - capture_audio: true (Capture Audio BETA)
        Dictionary<string, object> gameCaptureSettings = new()
        {
            ["capture_mode"] = "window",
            ["window"] = "Counter-Strike 2:SDL_app:cs2.exe",
            ["priority"] = 1,
            ["capture_audio"] = true
        };

        string settingsJson = JsonSerializer.Serialize(gameCaptureSettings);
        JsonElement settingsElement = JsonDocument.Parse(settingsJson).RootElement.Clone();

        try
        {
            await RequireClient().CreateInputAsync(
                inputName: Cs2SourceName,
                inputKind: GameCaptureInputKind,
                sceneName: sceneName,
                inputSettings: settingsElement,
                sceneItemEnabled: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            LogCreatedGameCaptureSource(Cs2SourceName);
        }
        catch (Exception ex)
        {
            LogErrorCreatingGameCaptureSource(ex, Cs2SourceName);
            throw;
        }
    }

    /// <summary>
    ///     Sets the source's bounds type to scale inner (fit) with the canvas dimensions.
    /// </summary>
    private async Task ApplyFitToScreenTransformAsync(string sceneName, string sourceName,
        CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSession.ApplyFitToScreenTransform");
        ObsWsClient client = RequireClient();

        try
        {
            GetSceneItemIdResponse? sceneItemIdResponse = await client
                .GetSceneItemIdAsync(sourceName, sceneName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (sceneItemIdResponse == null)
            {
                LogCouldNotGetSceneItemId(sourceName, sceneName);
                return;
            }

            double sceneItemId = sceneItemIdResponse.SceneItemId;
            activity?.SetTag("obs.sceneItemId", sceneItemId);

            GetVideoSettingsResponse? videoSettings = await client
                .GetVideoSettingsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (videoSettings == null)
            {
                LogCouldNotGetVideoSettings();
                return;
            }

            double canvasWidth = videoSettings.BaseWidth;
            double canvasHeight = videoSettings.BaseHeight;
            activity?.SetTag("obs.canvas.width", canvasWidth);
            activity?.SetTag("obs.canvas.height", canvasHeight);

            // boundsType: OBS_BOUNDS_SCALE_INNER = scale to fit within bounds, keeping aspect ratio.
            ObsSceneItemTransform transform = new()
            {
                BoundsType = "OBS_BOUNDS_SCALE_INNER",
                BoundsAlignment = 0, // Center alignment
                PositionX = Math.Floor(canvasWidth / 2.0),
                PositionY = Math.Floor(canvasHeight / 2.0)
            };

            await client.SetSceneItemTransformAsync(
                sceneItemId,
                transform,
                sceneName,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            LogAppliedFitToScreenTransform(sourceName, canvasWidth, canvasHeight);
            activity?.SetTag("obs.transform.applied", true);
        }
        catch (Exception ex)
        {
            LogErrorApplyingTransform(ex, sourceName);
            // Don't throw: a failed transform shouldn't stop capture.
        }
    }

    /// <summary>
    ///     Logger factory that wraps a single logger for use with the process manager.
    /// </summary>
    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Successfully connected to existing OBS instance (version: {Version})")]
    partial void LogConnectedExistingObs(string? version = null);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Could not connect to existing OBS instance: {Message}")]
    partial void LogCouldNotConnectExistingObs(string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Created output directory: {Directory}")]
    partial void LogCreatedOutputDirectory(string directory);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Connected to OBS after starting process (attempt {Attempt})")]
    partial void LogConnectedAfterStart(int attempt);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Connection attempt {Attempt} failed: {Message}")]
    partial void LogConnectionAttemptFailed(int attempt, string message);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug, Message = "Set OBS record directory: {Directory}")]
    partial void LogSetRecordDirectory(string directory);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Switched to scene: {Scene}")]
    partial void LogSwitchedScene(string scene);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Could not switch to initial scene: {Scene}")]
    partial void LogCouldNotSwitchScene(Exception exception, string scene);

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug,
        Message = "OBS capture session already initialized, reusing existing connection")]
    partial void LogObsAlreadyConnected();

    [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "Error disposing OBS client")]
    partial void LogErrorDisposingObsClient(Exception exception);

    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "Shutting down OBS...")]
    partial void LogShuttingDownObs();

    [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Error stopping OBS process")]
    partial void LogErrorStoppingObsProcess(Exception exception);

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Creating OBS scene: {SceneName}")]
    partial void LogCreatingScene(string sceneName);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information,
        Message = "Creating game capture source '{SourceName}' in scene '{SceneName}'")]
    partial void LogCreatingGameCaptureSource(string sourceName, string sceneName);

    [LoggerMessage(EventId = 32, Level = LogLevel.Information, Message = "Created game capture source: {SourceName}")]
    partial void LogCreatedGameCaptureSource(string sourceName);

    [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "Error checking if scene exists: {SceneName}")]
    partial void LogErrorCheckingSceneExists(Exception exception, string sceneName);

    [LoggerMessage(EventId = 34, Level = LogLevel.Warning,
        Message = "Error checking if source '{SourceName}' exists in scene '{SceneName}'")]
    partial void LogErrorCheckingSourceExists(Exception exception, string sourceName, string sceneName);

    [LoggerMessage(EventId = 35, Level = LogLevel.Error, Message = "Error creating game capture source: {SourceName}")]
    partial void LogErrorCreatingGameCaptureSource(Exception exception, string sourceName);

    [LoggerMessage(EventId = 36, Level = LogLevel.Information,
        Message = "Applied 'Fit to screen' transform to '{SourceName}' ({Width}x{Height})")]
    partial void LogAppliedFitToScreenTransform(string sourceName, double width, double height);

    [LoggerMessage(EventId = 37, Level = LogLevel.Warning,
        Message = "Could not get scene item ID for source '{SourceName}' in scene '{SceneName}'")]
    partial void LogCouldNotGetSceneItemId(string sourceName, string sceneName);

    [LoggerMessage(EventId = 38, Level = LogLevel.Warning, Message = "Could not get video settings from OBS")]
    partial void LogCouldNotGetVideoSettings();

    [LoggerMessage(EventId = 39, Level = LogLevel.Warning, Message = "Error applying transform to source: {SourceName}")]
    partial void LogErrorApplyingTransform(Exception exception, string sourceName);

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Error checking if input exists: {InputName}")]
    partial void LogErrorCheckingInputExists(Exception exception, string inputName);

    [LoggerMessage(EventId = 41, Level = LogLevel.Information,
        Message = "Adding existing source '{SourceName}' to scene '{SceneName}'")]
    partial void LogAddingExistingSourceToScene(string sourceName, string sceneName);

    [LoggerMessage(EventId = 42, Level = LogLevel.Information,
        Message = "Added source '{SourceName}' to scene '{SceneName}'")]
    partial void LogAddedSourceToScene(string sourceName, string sceneName);

    [LoggerMessage(EventId = 43, Level = LogLevel.Error,
        Message = "Error adding source '{SourceName}' to scene '{SceneName}'")]
    partial void LogErrorAddingSourceToScene(Exception exception, string sourceName, string sceneName);

    [LoggerMessage(EventId = 44, Level = LogLevel.Debug,
        Message = "OBS verification check failed (attempt {Attempt}): {Reason}")]
    partial void LogVerificationFailed(string reason, int attempt);

    [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "OBS verification error on attempt {Attempt}")]
    partial void LogVerificationError(Exception exception, int attempt);

    [LoggerMessage(EventId = 46, Level = LogLevel.Information, Message = "OBS verified and ready for capture")]
    partial void LogObsVerified();

    [LoggerMessage(EventId = 47, Level = LogLevel.Warning,
        Message = "OBS verification failed after {MaxRetries} attempts, proceeding anyway")]
    partial void LogVerificationFailedAfterRetries(int maxRetries);

    [LoggerMessage(EventId = 51, Level = LogLevel.Information,
        Message = "Audio capture disabled for this clip - muting OBS audio inputs")]
    partial void LogDisablingAudioCapture();

    [LoggerMessage(EventId = 52, Level = LogLevel.Warning, Message = "Error restoring OBS audio input mute states")]
    partial void LogErrorRestoringAudioMutes(Exception exception);

    [LoggerMessage(EventId = 53, Level = LogLevel.Warning,
        Message =
            "Synchronous Dispose timed out after {TimeoutSeconds}s with OBS teardown still in flight; " +
            "OBS may be left running or a recording unfinalized. Prefer DisposeAsync for a complete teardown.")]
    partial void LogSyncDisposeTimeout(int timeoutSeconds);

    [LoggerMessage(EventId = 54, Level = LogLevel.Warning, Message = "Error during synchronous Dispose teardown")]
    partial void LogSyncDisposeError(Exception exception);

    [LoggerMessage(EventId = 55, Level = LogLevel.Information,
        Message = "OBS ready to service requests (attempt {Attempt}, {ElapsedSeconds:F1}s after connect)")]
    partial void LogObsReady(int attempt, double elapsedSeconds);

    [LoggerMessage(EventId = 56, Level = LogLevel.Debug,
        Message = "OBS not ready yet (attempt {Attempt}), retrying in {DelaySeconds:F0}s: {Message}")]
    partial void LogObsNotReadyRetrying(int attempt, double delaySeconds, string message);

    [LoggerMessage(EventId = 57, Level = LogLevel.Warning,
        Message = "Aborting the clip still recording while the OBS capture session tears down")]
    partial void LogAbortingActiveClipOnTeardown();

    #endregion
}
