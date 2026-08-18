#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     The everyday entry point: composes the object graph for one CS2 run and hands the pieces to
///     the components that own the actual behavior.
/// </summary>
/// <remarks>
///     <para>
///         Each start creates a fresh <see cref="ICs2EngineSession" /> (via <see cref="ICs2EngineHost" />)
///         and a fresh <see cref="ICaptureSession" /> (via the selected
///         <see cref="ICaptureSessionFactory" />), brings capture up before CS2 launches, and tears
///         capture down strictly before CS2 is killed. That ordering, the current session objects, the
///         client-facing <see cref="CsvgSessionState" /> machine and the events consumers subscribe to
///         are the whole of what this type owns. The rest is delegation.
///     </para>
///     <para>
///         Demo control is not re-wrapped here. Reach it through <see cref="Engine" />, the real engine
///         session for the current run: <c>await session.Engine.PauseDemoAsync()</c>.
///         <see cref="LoadDemoAsync" /> is the exception; it loads through the engine and also tells the
///         capture backend about the demo, which makes it a composition step rather than a forwarder.
///         The events below are re-exposed: they get re-pointed at each new engine session, so a
///         subscription taken once keeps firing across a Start→Stop→Start cycle.
///     </para>
/// </remarks>
public sealed partial class CsvgVideoSession : IAsyncDisposable, IDisposable
{
    // one ICaptureSession per CS2 run. null means no capture provider, and availability is checked
    // through it before CS2 launches.
    private readonly ICaptureSessionFactory? _captureSessionFactory;

    // The single per-clip recipe: arm the backend's engine brackets, drive playback, finalize. Owns
    // the CaptureState machine this type reports and forwards.
    private readonly ClipCaptureCoordinator _captureCoordinator;

    // stateless, so it is shared like the coordinator and receives this session's current objects
    // per run.
    private readonly Cs2CompilationRunner _compilationRunner;

    // creates one engine session per start and owns the singleton dependencies every session needs
    // (process manager, gRPC endpoint probe). shared with the gRPC layer, which routes the plugin's
    // event stream into the host's current session.
    private readonly ICs2EngineHost _engineHost;

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly IOptions<Cs2VideoGeneratorOptions> _options;

    // Capture for the current engine session, up before CS2 launches and torn down when the session
    // stops. Replaced with a fresh instance on every start, so a stopped session's disposed backend
    // can never be reused.
    private ICaptureSession? _captureSession;

    // cancelled when _captureSession goes away, and handed to the capture session as
    // CaptureSessionContext.SessionToken. backends observe it for background work that must not
    // outlive the CS2 run. deliberately not the caller's startup token, which is scoped to bring-up
    // and may be cancelled or disposed while the session is still alive.
    private CancellationTokenSource? _captureSessionCts;

    // the current engine session: plugin command channel, latched engine truth,
    // capability/compatibility evaluation, all demo playback control, session bring-up/teardown.
    // replaced wholesale on every start so per-session state can never leak across
    // Start→Stop→Start. always read it via CurrentSession.
    private Cs2EngineSession _session;

    // Latched here so they survive the fresh-session-per-start replacement: a seam set before
    // StartAsync must still apply to the session that start creates.
    private TimeSpan? _seekAckTimeoutOverride;
    private TimeSpan? _demoStateQueryTimeoutOverride;
    private TimeSpan? _commandAckTimeoutOverride;

    private bool _captureEventsSubscribed;
    private bool _captureInitialized;
    private CaptureResult? _lastCaptureResult;
    private CsvgSessionState _state = CsvgSessionState.Disconnected;

    /// <summary>
    ///     Composes a video session. Prefer <see cref="CsvgVideoSessionBuilder" /> or dependency
    ///     injection over calling this directly.
    /// </summary>
    /// <param name="logger">Logger for the composition-level events (state transitions, bring-up, teardown).</param>
    /// <param name="engineHost">
    ///     The engine host this session creates its engine sessions from. Must be the same instance the
    ///     gRPC layer resolves, or the plugin's events never reach the session.
    /// </param>
    /// <param name="options">The bound CSVG options.</param>
    /// <param name="captureSessionFactory">
    ///     The selected capture backend, or null for a playback-only session (no capture entry point
    ///     works, and <see cref="HasCaptureProvider" /> is false).
    /// </param>
    /// <param name="captureCoordinator">
    ///     The shared per-clip recipe. A fresh one is created when null; it must be the same instance
    ///     the compilation runner uses, or compilation clips report capture state through a different
    ///     state machine than this session forwards.
    /// </param>
    /// <param name="compilationRunner">The shared compilation pipeline. Built over the coordinator when null.</param>
    /// <param name="ffmpegService">
    ///     Only used to build the fallback <paramref name="compilationRunner" />; an injected runner
    ///     brings its own.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     <paramref name="compilationRunner" /> reports capture state through a different
    ///     <see cref="ClipCaptureCoordinator" /> than <paramref name="captureCoordinator" />.
    /// </exception>
    /// <remarks>
    ///     At most one session may exist per <paramref name="engineHost" />. Construction creates an
    ///     engine session, and <see cref="ICs2EngineHost.CreateSession" /> publishes what it creates as
    ///     <see cref="ICs2EngineHost.Current" />. That is deliberate, so a plugin connecting while CS2
    ///     is still coming up lands on the session that is starting. A second session built over the
    ///     same host therefore takes the plugin's events away from the first, which is why dependency
    ///     injection registers this type as a singleton.
    /// </remarks>
    public CsvgVideoSession(
        ILogger<CsvgVideoSession> logger,
        ICs2EngineHost engineHost,
        IOptions<Cs2VideoGeneratorOptions> options,
        ICaptureSessionFactory? captureSessionFactory = null,
        ClipCaptureCoordinator? captureCoordinator = null,
        Cs2CompilationRunner? compilationRunner = null,
        IFfmpegService? ffmpegService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _captureSessionFactory = captureSessionFactory;
        _captureCoordinator = captureCoordinator ?? new ClipCaptureCoordinator();

        // A runner reporting through a different coordinator is a silent split brain: its clips drive
        // one state machine while CaptureState and CaptureStateChanged read another, so the caller sees
        // a capture pipeline that never leaves Idle. Cheaper to refuse the pair than to debug it.
        if (compilationRunner != null && !ReferenceEquals(compilationRunner.Coordinator, _captureCoordinator))
        {
            throw new ArgumentException(
                $"The supplied {nameof(Cs2CompilationRunner)} was built over a different " +
                $"{nameof(ClipCaptureCoordinator)} than this session forwards, so a compilation's clips " +
                "would report capture state nobody is listening to. Build both over one coordinator.",
                nameof(compilationRunner));
        }

        _compilationRunner = compilationRunner ?? new Cs2CompilationRunner(_captureCoordinator, ffmpegService);

        // An initial session exists so wire events and commands have a target before the first start
        // (commands fail fast with "no active session"); every start replaces it with a fresh session
        // carrying that start's launch shape.
        _session = RequireEngineSession(_engineHost.CreateSession(new EngineSessionOptions()));
        AttachSessionForwarders(_session);

        // The capture state machine lives on the coordinator, which outlives capture sessions, so the
        // CaptureStateChanged subscription is attached once here rather than per session.
        SubscribeToCaptureCoordinatorEvents();
    }

    /// <summary>
    ///     The engine session for the current run: demo control, latched engine truth, capabilities,
    ///     tick scheduling and raw playback all live here rather than being re-wrapped on this type.
    ///     A fresh instance per start, so hold it only for as long as one run.
    /// </summary>
    public ICs2EngineSession Engine => CurrentSession;

    /// <summary>
    ///     The engine host this session creates its engine sessions from. Exposed because the gRPC
    ///     service that receives the plugin's connections has to be built over the same host. Under
    ///     dependency injection that happens automatically; a host without a container passes this to
    ///     <c>Cs2GameService</c> itself.
    /// </summary>
    public ICs2EngineHost EngineHost => _engineHost;

    /// <summary>
    ///     The current state of the session.
    /// </summary>
    public CsvgSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
        private set
        {
            CsvgSessionState oldState;
            Func<CsvgSessionState, CsvgSessionState, Task>? handler;
            lock (_gate)
            {
                if (_state == value)
                {
                    return;
                }

                oldState = _state;
                _state = value;
                handler = StateChanged;
            }

            AnnounceStateChanged(oldState, value, handler);
        }
    }

    /// <summary>
    ///     Whether a capture provider is configured. False means every capture entry point on this
    ///     type is unusable and only playback works.
    /// </summary>
    public bool HasCaptureProvider => _captureSessionFactory != null;

    /// <summary>
    ///     The selected capture provider's name (see <see cref="CaptureProviderNames" />), or null
    ///     when no provider is configured.
    /// </summary>
    public string? CaptureProviderName => _captureSessionFactory?.ProviderName;

    /// <summary>
    ///     The current state of the capture pipeline, or null when no provider is configured. Reported
    ///     by <see cref="ClipCaptureCoordinator" />, which owns the capture state machine.
    /// </summary>
    public CaptureState? CaptureState => _captureSessionFactory == null ? null : _captureCoordinator.State;

    /// <summary>
    ///     The result of the last clip captured through this session, or null if none has been.
    /// </summary>
    public CaptureResult? LastCaptureResult
    {
        get
        {
            lock (_gate)
            {
                return _lastCaptureResult;
            }
        }
        private set
        {
            lock (_gate)
            {
                _lastCaptureResult = value;
            }
        }
    }

    private Cs2EngineSession CurrentSession
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    // the three timeouts below are settable only as a test seam. production code never touches them.
    // how long to wait for a seek's CommandAckEvent.
    internal TimeSpan SeekAckTimeout
    {
        get => CurrentSession.SeekAckTimeout;
        set
        {
            lock (_gate)
            {
                _seekAckTimeoutOverride = value;
                _session.SeekAckTimeout = value;
            }
        }
    }

    // How long to wait for the DemoStateEvent reply to a demo state query.
    internal TimeSpan DemoStateQueryTimeout
    {
        get => CurrentSession.DemoStateQueryTimeout;
        set
        {
            lock (_gate)
            {
                _demoStateQueryTimeoutOverride = value;
                _session.DemoStateQueryTimeout = value;
            }
        }
    }

    internal TimeSpan CommandAckTimeout
    {
        get => CurrentSession.CommandAckTimeout;
        set
        {
            lock (_gate)
            {
                _commandAckTimeoutOverride = value;
                _session.CommandAckTimeout = value;
            }
        }
    }

    #region Events

    /// <summary>
    ///     Raised when the session state changes.
    ///     Parameters: oldState, newState
    /// </summary>
    public event Func<CsvgSessionState, CsvgSessionState, Task>? StateChanged;

    /// <summary>
    ///     Raised when the plugin establishes a session.
    /// </summary>
    public event Func<string, Task>? SessionStarted;

    /// <summary>
    ///     Raised when the session ends (disconnect or shutdown).
    ///     Parameters: sessionId, lastTick, lastSeenUtc
    /// </summary>
    public event Func<string, long?, DateTimeOffset, Task>? SessionEnded;

    /// <summary>
    ///     Raised when the plugin reports a tick update.
    ///     By default, updates are sent every 500 ticks (~7.8 seconds at 64 ticks/sec).
    ///     Updates are always sent at key moments (pause, resume, seek) regardless of frequency.
    ///     Use <see cref="ICs2EngineSession.SetTickUpdateFrequencyAsync" /> to change the frequency
    ///     (0 = every tick).
    /// </summary>
    /// <remarks>
    ///     Synchronous <see cref="Action" /> rather than async, for throughput. A slow handler stalls
    ///     the event pipeline behind it.
    /// </remarks>
    public event Action<string, long>? TickUpdated;

    /// <summary>
    ///     Raised when demo playback status changes.
    /// </summary>
    public event Func<string, DemoPlaybackStatusChange, Task>? DemoPlaybackStatusChanged;

    /// <summary>
    ///     Raised when the plugin reports an engine-truth demo state snapshot. Requires a plugin
    ///     advertising <see cref="CsvgCapabilities.DemoStateEvents" /> ("demo-state-events"); never
    ///     raised against older plugins. Subscriber exceptions are isolated per-subscriber (logged and
    ///     swallowed), like the other async events.
    /// </summary>
    public event Func<string, DemoState, Task>? DemoStateChanged;

    /// <summary>
    ///     Raised for each client-side game event the plugin forwards while a subscription started by
    ///     <see cref="ICs2EngineSession.SetGameEventSubscriptionAsync" /> is active. Requires
    ///     <see cref="CsvgCapabilities.GameEventTelemetry" />; never raised otherwise (telemetry is off
    ///     until explicitly enabled).
    /// </summary>
    public event Func<string, GameEventTelemetry, Task>? GameEventReceived;

    /// <summary>
    ///     Raised when CS2 process status updates are reported.
    /// </summary>
    public event Func<string, Cs2ProcessStatus, Task>? ProcessStatusChanged;

    /// <summary>
    ///     Raised when a command is rejected because no active session exists.
    /// </summary>
    public event Func<string, Task>? CommandRejected;

    /// <summary>
    ///     Raised when the capture pipeline's state changes.
    ///     Parameters: oldState, newState
    /// </summary>
    public event Func<CaptureState, CaptureState, Task>? CaptureStateChanged;

    /// <summary>
    ///     Raised when capture progress is updated during an active capture.
    /// </summary>
    /// <remarks>
    ///     Synchronous, like <see cref="TickUpdated" />, and it can fire often. Keep handlers cheap.
    /// </remarks>
    public event Action<CaptureProgressEventArgs>? CaptureProgressUpdated;

    /// <summary>
    ///     Raised when a capture error occurs.
    /// </summary>
    public event Func<CaptureErrorEventArgs, Task>? CaptureErrorOccurred;

    /// <summary>
    ///     Raised when a compilation clip starts processing.
    ///     Parameters: clipIndex, clip
    /// </summary>
    public event Func<int, Cs2CompilationClip, Task>? CompilationClipStarted;

    /// <summary>
    ///     Raised when a compilation clip completes (success or failure).
    ///     Parameters: clipIndex, clipResult
    /// </summary>
    public event Func<int, Cs2ClipResult, Task>? CompilationClipCompleted;

    #endregion

    #region Engine Session Forwarding

    // The engine-truth events are owned by this type. Subscribers register here, and a per-session
    // forwarding subscription (attached when a session is created, detached when it is replaced)
    // feeds them from whichever engine session is current. That is what lets a subscriber added
    // while session A was current keep firing when session B takes over after a Start→Stop→Start
    // cycle. Async dispatch keeps per-subscriber exception isolation via InvokeAsyncHandler; the
    // sync TickUpdated forwarder invokes the multicast directly, like the session's own dispatch.

    private void AttachSessionForwarders(Cs2EngineSession session)
    {
        session.Started += ForwardSessionStartedAsync;
        session.Ended += ForwardSessionEndedAsync;
        session.TickUpdated += ForwardTickUpdated;
        session.DemoPlaybackStatusChanged += ForwardDemoPlaybackStatusChangedAsync;
        session.DemoStateChanged += ForwardDemoStateChangedAsync;
        session.GameEventReceived += ForwardGameEventReceivedAsync;
        session.ProcessStatusChanged += ForwardProcessStatusChangedAsync;
        session.CommandRejected += ForwardCommandRejectedAsync;

        // The state machine reacts to an unexpected session end before the session releases its
        // ended-waiters, so a woken waiter never observes a stale Connected state.
        session.EndedSynchronously = FaultOnUnexpectedSessionEnd;
    }

    private void DetachSessionForwarders(Cs2EngineSession session)
    {
        session.Started -= ForwardSessionStartedAsync;
        session.Ended -= ForwardSessionEndedAsync;
        session.TickUpdated -= ForwardTickUpdated;
        session.DemoPlaybackStatusChanged -= ForwardDemoPlaybackStatusChangedAsync;
        session.DemoStateChanged -= ForwardDemoStateChangedAsync;
        session.GameEventReceived -= ForwardGameEventReceivedAsync;
        session.ProcessStatusChanged -= ForwardProcessStatusChangedAsync;
        session.CommandRejected -= ForwardCommandRejectedAsync;
        session.EndedSynchronously = null;
    }

    // Only an unexpected end faults the session. A graceful StopAsync has already moved to
    // Disconnecting and finishes in Disconnected.
    private void FaultOnUnexpectedSessionEnd()
    {
        if (State == CsvgSessionState.Connected)
        {
            State = CsvgSessionState.Faulted;
        }
    }

    private Task ForwardSessionStartedAsync(string sessionId) => InvokeAsyncHandler(SessionStarted, sessionId);

    private Task ForwardSessionEndedAsync(string sessionId, long? lastTick, DateTimeOffset lastSeenUtc) =>
        InvokeAsyncHandler(SessionEnded, sessionId, lastTick, lastSeenUtc);

    private void ForwardTickUpdated(string sessionId, long currentTick) =>
        TickUpdated?.Invoke(sessionId, currentTick);

    private Task ForwardDemoPlaybackStatusChangedAsync(string sessionId, DemoPlaybackStatusChange statusChange) =>
        InvokeAsyncHandler(DemoPlaybackStatusChanged, sessionId, statusChange);

    private Task ForwardDemoStateChangedAsync(string sessionId, DemoState state) =>
        InvokeAsyncHandler(DemoStateChanged, sessionId, state);

    private Task ForwardGameEventReceivedAsync(string sessionId, GameEventTelemetry telemetry) =>
        InvokeAsyncHandler(GameEventReceived, sessionId, telemetry);

    private Task ForwardProcessStatusChangedAsync(string sessionId, Cs2ProcessStatus status) =>
        InvokeAsyncHandler(ProcessStatusChanged, sessionId, status);

    private Task ForwardCommandRejectedAsync(string reason) => InvokeAsyncHandler(CommandRejected, reason);

    // one session object per CS2 run: create it, re-target the event forwarders and the timeout
    // seams at it, publish it as CurrentSession. returns the session it replaced, which the caller
    // shuts down outside the lock.
    private Cs2EngineSession ReplaceSessionCore(EngineSessionOptions engineOptions)
    {
        Cs2EngineSession fresh = RequireEngineSession(_engineHost.CreateSession(engineOptions));
        if (_seekAckTimeoutOverride is { } seekAckTimeout)
        {
            fresh.SeekAckTimeout = seekAckTimeout;
        }

        if (_demoStateQueryTimeoutOverride is { } demoStateQueryTimeout)
        {
            fresh.DemoStateQueryTimeout = demoStateQueryTimeout;
        }

        if (_commandAckTimeoutOverride is { } commandAckTimeout)
        {
            fresh.CommandAckTimeout = commandAckTimeout;
        }

        Cs2EngineSession previous = _session;
        DetachSessionForwarders(previous);
        AttachSessionForwarders(fresh);
        _session = fresh;
        return previous;
    }

    // Bring-up/teardown and the wire seam are implementation concerns, not part of
    // ICs2EngineSession, so a host that substitutes its own session type cannot be composed here.
    private static Cs2EngineSession RequireEngineSession(ICs2EngineSession session) =>
        session as Cs2EngineSession ?? throw new InvalidOperationException(
            $"The engine host returned a session of type '{session.GetType().FullName}'. " +
            $"{nameof(CsvgVideoSession)} composes the built-in {nameof(Cs2EngineSession)}; register a " +
            $"different {nameof(ICs2EngineHost)} only together with your own composition entry point.");

    #endregion

    #region Session Lifecycle

    /// <summary>
    ///     Starts a session with CS2's default window shape and capture enabled: launches CS2, waits
    ///     for the plugin to connect and report ready, and brings the capture backend up first.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort startup.</param>
    /// <exception cref="TimeoutException">The plugin did not connect within the timeout period.</exception>
    /// <exception cref="InvalidOperationException">A session is already active or connecting.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(new EngineSessionOptions(), initializeCapture: true, cancellationToken);

    /// <summary>
    ///     Starts a session: brings the capture backend up (when requested), launches CS2 with the
    ///     given window shape, and waits until the plugin has connected and reported ready. Returns
    ///     when the session is ready to receive commands through <see cref="Engine" />.
    /// </summary>
    /// <param name="engineOptions">The launch shape (width, height, fullscreen) for this run.</param>
    /// <param name="initializeCapture">
    ///     Whether to bring the capture backend up. False gives a playback-only session, which is what
    ///     a watch/live-sync host wants; it is also the only option when no provider is configured.
    /// </param>
    /// <param name="cancellationToken">Cancellation token to abort startup.</param>
    /// <exception cref="TimeoutException">The plugin did not connect within the timeout period.</exception>
    /// <exception cref="InvalidOperationException">
    ///     A session is already active or connecting, or the configured capture provider is not
    ///     available.
    /// </exception>
    /// <remarks>
    ///     A session can only be started from <see cref="CsvgSessionState.Disconnected" />. If it is
    ///     <see cref="CsvgSessionState.Faulted" /> (unexpected disconnect or failed startup), call
    ///     <see cref="StopAsync" /> first. That is the reset path: it stops any still-running CS2
    ///     process, restores configuration backups, and returns to
    ///     <see cref="CsvgSessionState.Disconnected" />. Starting straight out of Faulted is
    ///     deliberately not allowed, because a faulted session may still have a live CS2 process and
    ///     unrestored backups.
    /// </remarks>
    public async Task StartAsync(EngineSessionOptions engineOptions, bool initializeCapture = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engineOptions);

        Cs2VideoGeneratorOptions opts = _options.Value;
        using Activity? activity = CsvgTracing.StartActivity("CsvgVideoSession.Start", ActivityKind.Client);

        Cs2EngineSession session;
        Cs2EngineSession previousSession;
        ICaptureSession? previousCaptureSession;
        CancellationTokenSource? previousCaptureSessionCts;
        Func<CsvgSessionState, CsvgSessionState, Task>? stateChangedHandler;

        lock (_gate)
        {
            if (_state != CsvgSessionState.Disconnected)
            {
                throw new InvalidOperationException(
                    $"Cannot start session: current state is {_state}. Expected Disconnected." +
                    (_state == CsvgSessionState.Faulted
                        ? " Call StopAsync() to clean up the faulted session first."
                        : string.Empty));
            }

            // Claim the start under the same gate acquisition that checked it: the state has to leave
            // Disconnected before the lock is released, or two concurrent starts both pass the check
            // above and race each other through session replacement. Only the mutation happens here.
            // Subscribers are told once the lock is gone (AnnounceStateChanged below).
            _state = CsvgSessionState.Connecting;
            stateChangedHandler = StateChanged;

            // A previous run's capture session has been shut down (and its backend torn down) by
            // StopAsync, so it must not be reused: hand it to the disposal below and let this start
            // create a fresh one.
            previousCaptureSession = _captureSession;
            previousCaptureSessionCts = _captureSessionCts;
            _captureSession = null;
            _captureSessionCts = null;
            _captureInitialized = false;

            // A brand new engine session for this start: no latched truth, capabilities, process
            // status, or pending command waiter from a previous run can exist on it, and its
            // session-ended latch exists before launch so a session that starts and ends during
            // startup still signals waiters instead of leaving them hanging.
            previousSession = ReplaceSessionCore(engineOptions);
            session = _session;
        }

        // The replaced session can never be answered again; release anything still waiting on it.
        previousSession.Shutdown("The engine session was replaced before the command completed.");
        await DisposeCaptureSessionAsync(previousCaptureSession, previousCaptureSessionCts).ConfigureAwait(false);

        LogStartingSession(opts.ExternalMockServerPath, opts.MockMode);
        activity?.SetTag("csvg.display.width", engineOptions.Width);
        activity?.SetTag("csvg.display.height", engineOptions.Height);
        activity?.SetTag("csvg.display.fullscreen", engineOptions.Fullscreen);
        AnnounceStateChanged(CsvgSessionState.Disconnected, CsvgSessionState.Connecting, stateChangedHandler);

        try
        {
            // Capture comes up before CS2 launches; a screen recorder's game capture has to be ready
            // first. The hook runs inside ConnectAsync after its gRPC endpoint probe, preserving the
            // fail-fast ordering: probe, then capture init, then launch.
            Func<CancellationToken, Task>? preLaunchInitialization = null;
            if (initializeCapture && _captureSessionFactory != null)
            {
                preLaunchInitialization = async ct =>
                {
                    CaptureAvailabilityResult captureResult =
                        await InitializeCaptureInternalAsync(session, ct).ConfigureAwait(false);
                    activity?.SetTag("csvg.capture.available", captureResult.IsAvailable);
                    activity?.SetTag("csvg.capture.version", captureResult.ProviderVersion);

                    if (!captureResult.IsAvailable)
                    {
                        string issues = string.Join("; ", captureResult.Issues);
                        activity?.SetTag("csvg.capture.issues", issues);
                        throw new InvalidOperationException(
                            $"Capture provider is configured but not available: {issues}");
                    }
                };
            }

            await session.ConnectAsync(activity, preLaunchInitialization, cancellationToken).ConfigureAwait(false);

            State = CsvgSessionState.Connected;
        }
        catch
        {
            State = CsvgSessionState.Faulted;
            throw;
        }
    }

    /// <summary>
    ///     Starts a playback-only "watch" session and switches tick updates to every-tick delivery:
    ///     equivalent to <see cref="StartAsync(EngineSessionOptions, bool, CancellationToken)" /> with
    ///     <c>initializeCapture: false</c> followed by
    ///     <see cref="ICs2EngineSession.SetTickUpdateFrequencyAsync" /> with frequency 0. This is the
    ///     canonical live-sync bootstrap: no capture backend is initialized, and a host mirroring
    ///     playback gets the densest tick stream the plugin can provide. Works against any plugin
    ///     version (no capability required).
    /// </summary>
    /// <param name="engineOptions">The launch shape for this run, or null for CS2's defaults.</param>
    /// <param name="cancellationToken">Cancellation token to abort startup.</param>
    /// <exception cref="TimeoutException">The plugin did not connect within the timeout period.</exception>
    /// <exception cref="InvalidOperationException">A session is already active or connecting.</exception>
    public async Task StartWatchAsync(EngineSessionOptions? engineOptions = null,
        CancellationToken cancellationToken = default)
    {
        await StartAsync(engineOptions ?? new EngineSessionOptions(), initializeCapture: false, cancellationToken)
            .ConfigureAwait(false);
        await CurrentSession.SetTickUpdateFrequencyAsync(0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stops the active session gracefully: tears the capture backend down, then kills CS2 and
    ///     restores configuration backups. Also the reset path out of
    ///     <see cref="CsvgSessionState.Faulted" />. A no-op when already disconnected, and when another
    ///     stop is already tearing the session down: the caller that claimed the teardown owns it, so
    ///     this returns immediately rather than running a second one.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort shutdown.</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Cs2VideoGeneratorOptions opts = _options.Value;
        using Activity? activity = CsvgTracing.StartActivity("CsvgVideoSession.Stop", ActivityKind.Client);

        LogStoppingSession(opts.ExternalMockServerPath, opts.MockMode);

        Cs2EngineSession session;
        ICaptureSession? captureSession;
        CsvgSessionState previousState;
        Func<CsvgSessionState, CsvgSessionState, Task>? stateChangedHandler;

        lock (_gate)
        {
            // Claim the stop under the same gate acquisition that checked it, exactly as StartAsync
            // does. A stop already in flight owns the teardown: a second caller passing this check
            // would shut the same capture session down again, stop the process again, and — the part
            // that actually corrupts — announce Disconnected from its finally while the first stop is
            // still killing CS2, which lets a concurrent start be accepted against a dying process.
            if (_state is CsvgSessionState.Disconnected or CsvgSessionState.Disconnecting)
            {
                return;
            }

            // Unlike the start path this transition has three possible origins (Connected, Connecting
            // and the Faulted reset path), so the announcement below carries the state actually
            // observed here rather than a hardcoded one.
            previousState = _state;
            _state = CsvgSessionState.Disconnecting;
            stateChangedHandler = StateChanged;

            session = _session;
            captureSession = _captureSession;
            _captureInitialized = false;
        }

        AnnounceStateChanged(previousState, CsvgSessionState.Disconnecting, stateChangedHandler);

        try
        {
            // Tear the capture backend down (stops the recorder, aborts a clip still recording)
            // before killing CS2. A screen recorder's game-capture source is hooked into CS2; killing
            // CS2 while the recorder is still alive and capturing it makes the recorder fault during
            // its own teardown and pop an interaction-blocking crash dialog. Shutdown is reversible:
            // the session object stays, and the next start replaces it with a new one.
            if (captureSession != null)
            {
                try
                {
                    LogStoppingCaptureOnSessionEnd();
                    await captureSession.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogCaptureStopErrorOnSessionEnd(ex);
                }
            }

            // Engine-side teardown: graceful gRPC session stop request, a bounded wait for its
            // acknowledgement, then the CS2 process stop.
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A stopped session is terminal (the next start creates a fresh one): fail anything still
            // waiting on an outstanding command and release session-ended waiters.
            session.Shutdown("The session was stopped before the command completed.");
            State = CsvgSessionState.Disconnected;
        }
    }

    /// <summary>
    ///     Waits for the session to end naturally (CS2 exits or the plugin disconnects). Returns
    ///     immediately when there is no live session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop waiting.</param>
    public async Task WaitForEndAsync(CancellationToken cancellationToken = default)
    {
        Cs2EngineSession session;
        lock (_gate)
        {
            if (_state is CsvgSessionState.Disconnected or CsvgSessionState.Faulted)
            {
                return;
            }

            session = _session;
        }

        // The wait is wrapped (WaitAsync) rather than cancelling the shared latch: cancelling it would
        // poison every other current and future waiter (including StopAsync's graceful-stop wait) just
        // because one caller gave up.
        await session.SessionEndedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // publishes a transition that has already been applied. always called outside _gate, because
    // subscribers must never run under this type's lock. that is why callers that flip the state
    // inside the gate capture the handler there and announce afterwards.
    private void AnnounceStateChanged(CsvgSessionState oldState, CsvgSessionState newState,
        Func<CsvgSessionState, CsvgSessionState, Task>? handler)
    {
        LogStateChanged(oldState, newState);
        _ = InvokeAsyncHandler(handler, oldState, newState);
    }

    #endregion

    #region Capture

    /// <summary>
    ///     Brings the capture backend up for the current engine session and reports its availability.
    ///     Called automatically by <see cref="StartAsync(EngineSessionOptions, bool, CancellationToken)" />
    ///     when capture is requested; call it manually to re-check availability or re-initialize after
    ///     a backend error, which replaces (and disposes) the capture session currently in place.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating availability and any issues.</returns>
    /// <exception cref="InvalidOperationException">No capture provider is configured.</exception>
    public async Task<CaptureAvailabilityResult> InitializeCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (_captureSessionFactory == null)
        {
            throw new InvalidOperationException("No capture provider is configured");
        }

        return await InitializeCaptureInternalAsync(CurrentSession, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Loads a demo through the engine and then tells the capture backend about it. Backends that
    ///     have to register against the loaded demo (HLAE's mirv handshake) do their work in that hook,
    ///     so a demo loaded straight through <see cref="Engine" /> would skip it. The compilation
    ///     pipeline performs the same pair itself, which is why this only matters for the single-clip
    ///     path.
    /// </summary>
    /// <param name="demoPath">Path to the .dem file.</param>
    /// <param name="interactiveDemoUi">
    ///     When true, asks a plugin advertising <see cref="CsvgCapabilities.UserDemoUi" /> to leave the
    ///     in-game demo UI available so the user can pause and seek inside CS2. Older plugins ignore
    ///     the flag.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the demo is loaded and paused at demo tick 0.</returns>
    public async Task LoadDemoAsync(string demoPath, bool interactiveDemoUi = false,
        CancellationToken cancellationToken = default)
    {
        await CurrentSession.LoadDemoAsync(demoPath, interactiveDemoUi, cancellationToken).ConfigureAwait(false);

        ICaptureSession? captureSession;
        lock (_gate)
        {
            captureSession = _captureSession;
        }

        if (captureSession != null)
        {
            await captureSession.OnDemoLoadedAsync(demoPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Records one clip: plays the tick range while the capture backend records it. Everything the
    ///     clip carries (its demo options, its spectator target, the engine-side re-assert that
    ///     survives the seek into it) is applied by <see cref="ClipCaptureCoordinator" /> — the same
    ///     recipe the compilation path runs, so a clip recorded here and a clip recorded inside a
    ///     compilation are prepared identically.
    /// </summary>
    /// <param name="clip">
    ///     The clip plan: tick range, output metadata, and optionally the spectated player, the demo
    ///     options and an explicit playback timeout. Single-use; build a new one per clip.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     The clip's result. Capture problems are reported as a failed
    ///     <see cref="CaptureResult" />, not as exceptions, so the CS2 session survives a bad clip.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     No capture provider is configured, or capture is not initialized for this session (start it
    ///     with capture enabled, or call <see cref="InitializeCaptureAsync" />).
    /// </exception>
    public async Task<CaptureResult> CaptureClipAsync(ClipCaptureContext clip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);

        using Activity? activity = CsvgTracing.StartActivity("CsvgVideoSession.CaptureClip", ActivityKind.Client);
        activity?.SetTag("csvg.tick.start", clip.StartTick);
        activity?.SetTag("csvg.tick.end", clip.EndTick);
        activity?.SetTag("csvg.capture.output", clip.Metadata.OutputFileName);
        activity?.SetTag("csvg.session.id", CurrentSession.SessionId);

        if (_captureSessionFactory == null)
        {
            throw new InvalidOperationException("No capture provider is configured");
        }

        ICaptureSession? captureSession;
        lock (_gate)
        {
            captureSession = _captureInitialized ? _captureSession : null;
        }

        if (captureSession == null)
        {
            throw new InvalidOperationException(
                "Capture provider is not available. Check HasCaptureProvider and CaptureState, or call " +
                "InitializeCaptureAsync to re-initialize.");
        }

        LogCapturingClip(clip.StartTick, clip.EndTick, clip.Metadata.OutputFileName);

        CaptureResult captureResult = await _captureCoordinator
            .CaptureClipAsync(CurrentSession, captureSession, clip, cancellationToken)
            .ConfigureAwait(false);

        activity?.SetTag("csvg.capture.success", captureResult.Success);
        activity?.SetTag("csvg.capture.outputPath", captureResult.OutputFilePath);
        activity?.SetTag("csvg.capture.durationMs", captureResult.Duration.TotalMilliseconds);
        if (captureResult.ErrorMessage != null)
        {
            activity?.SetTag("csvg.capture.error", captureResult.ErrorMessage);
        }

        LogCaptureCompleted(captureResult.Success, captureResult.OutputFilePath);

        LastCaptureResult = captureResult;
        return captureResult;
    }

    /// <summary>
    ///     Plays a demo tick range without recording it and reports the outcome. Playback problems
    ///     (bad demo state, timeout, send failure) come back as a failed
    ///     <see cref="DemoPlaybackResult" /> rather than an exception; use
    ///     <see cref="ICs2EngineSession.PlayTickRangeAsync" /> on <see cref="Engine" /> for the
    ///     throwing form.
    /// </summary>
    /// <param name="startTick">The starting tick.</param>
    /// <param name="endTick">The ending tick.</param>
    /// <param name="timeout">
    ///     Optional playback timeout. When null it is estimated from the tick range and the last known
    ///     timescale.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DemoPlaybackResult> PlayTickRangeAsync(int startTick, int endTick, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("CsvgVideoSession.PlayTickRange", ActivityKind.Client);
        activity?.SetTag("csvg.tick.start", startTick);
        activity?.SetTag("csvg.tick.end", endTick);
        activity?.SetTag("csvg.session.id", CurrentSession.SessionId);

        try
        {
            await CurrentSession.PlayTickRangeAsync(startTick, endTick, timeout, cancellationToken)
                .ConfigureAwait(false);
            return DemoPlaybackResult.Succeeded(startTick, endTick);
        }
        catch (Exception ex)
        {
            activity?.SetTag("csvg.playback.error", ex.Message);
            return DemoPlaybackResult.Failed(startTick, endTick, ex.Message);
        }
    }

    // Check availability (engine-free, so it can fail fast before CS2 launches), then create and
    // initialize the capture session. Idempotent: succeeds straight away if capture is already up
    // for this session.
    //
    // Single-caller by construction. The start path reaches this through ConnectAsync's pre-launch
    // hook, which only runs on a caller that already claimed the session, and the public entry point
    // requires a session that is not mid-start. So the already-initialized read below only has to
    // see a consistent value; it does not have to make the whole bring-up atomic.
    private async Task<CaptureAvailabilityResult> InitializeCaptureInternalAsync(
        Cs2EngineSession engineSession,
        CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("CsvgVideoSession.InitializeCapture", ActivityKind.Client);

        bool alreadyInitialized;
        lock (_gate)
        {
            alreadyInitialized = _captureInitialized;
        }

        if (alreadyInitialized)
        {
            LogCaptureAlreadyInitialized();
            return CaptureAvailabilityResult.Succeeded();
        }

        // Availability is configuration and installation only. The backend is not started here, so a
        // misconfigured provider fails before CS2 is launched.
        LogInitializingCapture();
        CaptureAvailabilityResult result = await _captureSessionFactory!
            .CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        activity?.SetTag("csvg.capture.provider", _captureSessionFactory.ProviderName);
        activity?.SetTag("csvg.capture.available", result.IsAvailable);
        activity?.SetTag("csvg.capture.version", result.ProviderVersion);

        if (!result.IsAvailable)
        {
            LogCaptureNotAvailable(string.Join("; ", result.Issues));
            return result;
        }

        // A capture session of its own for this engine session, brought up before CS2 launches (the
        // game-capture ordering contract). Backends only ever see the engine console.
        CancellationTokenSource sessionCts = new();
        ICaptureSession captureSession = _captureSessionFactory.Create(new CaptureSessionContext
        {
            Engine = engineSession,
            EngineSessionId = engineSession.SessionId,
            SessionToken = sessionCts.Token
        });

        captureSession.ProgressUpdated += OnCaptureProgressUpdated;
        captureSession.ErrorOccurred += OnCaptureErrorOccurred;

        try
        {
            await captureSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeCaptureSessionAsync(captureSession, sessionCts).ConfigureAwait(false);
            throw;
        }

        ICaptureSession? replaced;
        CancellationTokenSource? replacedCts;
        lock (_gate)
        {
            replaced = _captureSession;
            replacedCts = _captureSessionCts;
            _captureSession = captureSession;
            _captureSessionCts = sessionCts;
            _captureInitialized = true;
        }

        // A manual re-initialization (the public InitializeCaptureAsync, e.g. after a stop) replaces
        // whatever was there. Dispose it rather than dropping it with its process manager undisposed
        // and its event handlers still attached, the same lifetime rule the start path follows.
        await DisposeCaptureSessionAsync(replaced, replacedCts).ConfigureAwait(false);

        LogCaptureInitialized(result.ProviderVersion);
        return result;
    }

    // the coordinator outlives individual capture sessions, so this subscription lives as long as
    // the session object and CaptureStateChanged keeps firing across sessions. the per-session
    // progress/error events are attached when the capture session is created. idempotent.
    private void SubscribeToCaptureCoordinatorEvents()
    {
        if (_captureSessionFactory == null || _captureEventsSubscribed)
        {
            return;
        }

        _captureCoordinator.StateChanged += OnCaptureStateChangedInternal;
        _captureCoordinator.ErrorOccurred += OnCaptureErrorOccurred;
        _captureEventsSubscribed = true;
    }

    private void UnsubscribeFromCaptureCoordinatorEvents()
    {
        if (!_captureEventsSubscribed)
        {
            return;
        }

        _captureCoordinator.StateChanged -= OnCaptureStateChangedInternal;
        _captureCoordinator.ErrorOccurred -= OnCaptureErrorOccurred;
        _captureEventsSubscribed = false;
    }

    // Best effort: this runs on teardown paths that are already failing.
    private async Task DisposeCaptureSessionAsync(ICaptureSession? captureSession,
        CancellationTokenSource? sessionCts)
    {
        if (captureSession == null)
        {
            CancelAndDispose(sessionCts);
            return;
        }

        captureSession.ProgressUpdated -= OnCaptureProgressUpdated;
        captureSession.ErrorOccurred -= OnCaptureErrorOccurred;

        // Signal first: background work observing the session token should be winding down while the
        // backend tears itself down, not after it.
        CancelAndDispose(sessionCts);

        try
        {
            await captureSession.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCaptureStopErrorOnSessionEnd(ex);
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? sessionCts)
    {
        if (sessionCts == null)
        {
            return;
        }

        sessionCts.Cancel();
        sessionCts.Dispose();
    }

    private void OnCaptureStateChangedInternal(object? sender, CaptureStateChangedEventArgs e)
    {
        LogCaptureStateChanged(e.PreviousState, e.NewState);
        _ = InvokeAsyncHandler(CaptureStateChanged, e.PreviousState, e.NewState);
    }

    private void OnCaptureProgressUpdated(object? sender, CaptureProgressEventArgs e) =>
        CaptureProgressUpdated?.Invoke(e);

    private void OnCaptureErrorOccurred(object? sender, CaptureErrorEventArgs e)
    {
        LogCaptureProviderError(e.ErrorMessage, e.Severity);
        _ = InvokeAsyncHandler(CaptureErrorOccurred, e);
    }

    #endregion

    #region Compilation

    /// <summary>
    ///     Runs a whole compilation: the pipeline itself lives in <see cref="Cs2CompilationRunner" />;
    ///     this hands it the current engine and capture sessions plus the two clip callbacks that feed
    ///     <see cref="CompilationClipStarted" /> and <see cref="CompilationClipCompleted" />.
    /// </summary>
    /// <param name="compilation">The compilation definition with clips and settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing status and details for each captured clip.</returns>
    /// <exception cref="ArgumentException">The compilation is invalid.</exception>
    /// <exception cref="InvalidOperationException">Capture is not initialized for this session.</exception>
    public Task<Cs2CompilationResult> RunCompilationAsync(
        Cs2Compilation compilation,
        CancellationToken cancellationToken = default)
    {
        ICaptureSession? captureSession;
        lock (_gate)
        {
            // A stop leaves the (shut down) capture session object in place and only clears the
            // initialized flag, so the flag is what readiness means here. Handing the runner no
            // capture session is how "capture is not initialized" reaches it, reported after the
            // compilation validates.
            captureSession = _captureInitialized ? _captureSession : null;
        }

        return _compilationRunner.RunAsync(compilation, new Cs2CompilationRunContext
        {
            Engine = CurrentSession,
            Capture = captureSession,
            ClipStarted = OnCompilationClipStartedAsync,
            ClipCompleted = OnCompilationClipCompletedAsync
        }, cancellationToken);
    }

    private Task OnCompilationClipStartedAsync(int clipIndex, Cs2CompilationClip clip) =>
        InvokeAsyncHandler(CompilationClipStarted, clipIndex, clip);

    /// <summary>
    ///     Raises <see cref="CompilationClipCompleted" />, recording the clip's capture outcome as
    ///     <see cref="LastCaptureResult" /> first so subscribers observe the same ordering the
    ///     single-clip path gives them (a compilation's clips are captured inside
    ///     <see cref="Cs2CompilationRunner" />, which knows nothing about this type's bookkeeping).
    /// </summary>
    private Task OnCompilationClipCompletedAsync(int clipIndex, Cs2ClipResult result)
    {
        if (result.CaptureResult != null)
        {
            LastCaptureResult = result.CaptureResult;
        }

        return InvokeAsyncHandler(CompilationClipCompleted, clipIndex, result);
    }

    #endregion

    #region Async Event Invocation Helpers

    // Each subscriber is invoked inside its own try/catch: a throwing subscriber must neither skip
    // later subscribers nor (for fire-and-forget dispatch) surface as an unobserved task exception.

    private async Task InvokeAsyncHandler<T>(Func<T, Task>? handler, T arg)
    {
        if (handler == null)
        {
            return;
        }

        foreach (Func<T, Task> h in handler.GetInvocationList().Cast<Func<T, Task>>())
        {
            try
            {
                await h(arg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogEventHandlerError(ex);
            }
        }
    }

    private async Task InvokeAsyncHandler<T1, T2>(Func<T1, T2, Task>? handler, T1 arg1, T2 arg2)
    {
        if (handler == null)
        {
            return;
        }

        foreach (Func<T1, T2, Task> h in handler.GetInvocationList().Cast<Func<T1, T2, Task>>())
        {
            try
            {
                await h(arg1, arg2).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogEventHandlerError(ex);
            }
        }
    }

    private async Task InvokeAsyncHandler<T1, T2, T3>(Func<T1, T2, T3, Task>? handler, T1 arg1, T2 arg2, T3 arg3)
    {
        if (handler == null)
        {
            return;
        }

        foreach (Func<T1, T2, T3, Task> h in handler.GetInvocationList().Cast<Func<T1, T2, T3, Task>>())
        {
            try
            {
                await h(arg1, arg2, arg3).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogEventHandlerError(ex);
            }
        }
    }

    #endregion

    #region IDisposable / IAsyncDisposable

    // Upper bound on how long sync Dispose blocks trying to stop an active session (kill CS2,
    // restore backups) before it gives up and finishes disposing anyway.
    private static readonly TimeSpan SyncDisposeStopTimeout = TimeSpan.FromSeconds(15);

    private bool _disposed;

    /// <summary>
    ///     Synchronously disposes the session. If a run is still active (or faulted with a potentially
    ///     live CS2 process), this makes a <b>best-effort, time-bounded</b> attempt to stop it (kill
    ///     CS2, restore configuration backups) by running <see cref="StopAsync" /> on the thread pool
    ///     and blocking for at most <see cref="SyncDisposeStopTimeout" />. The bounded wait on a pool
    ///     thread avoids the synchronization-context deadlocks of naive sync-over-async. If the stop
    ///     cannot finish in time, a warning is logged and CS2 may be left running. Prefer
    ///     <see cref="DisposeAsync" />, which awaits the full session teardown.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Best-effort session stop, outside the lock (StopAsync takes it too). Faulted is included: an
        // unexpected disconnect may leave CS2 running with backups unrestored.
        if (State is CsvgSessionState.Connected or CsvgSessionState.Connecting or CsvgSessionState.Faulted)
        {
            try
            {
                // Task.Run keeps the async stop off any ambient SynchronizationContext; Wait(timeout)
                // strictly bounds the block even if the thread pool is starved.
                Task stopTask = Task.Run(() => StopAsync());
                if (!stopTask.Wait(SyncDisposeStopTimeout))
                {
                    LogSyncDisposeStopTimeout(SyncDisposeStopTimeout.TotalSeconds);
                }
            }
            catch (AggregateException ex)
            {
                LogSyncDisposeStopError(ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                LogSyncDisposeStopError(ex);
            }
        }

        ICaptureSession? captureSession;
        CancellationTokenSource? captureSessionCts;
        lock (_gate)
        {
            UnsubscribeFromCaptureCoordinatorEvents();

            captureSession = _captureSession;
            captureSessionCts = _captureSessionCts;
            _captureSession = null;
            _captureSessionCts = null;

            // drop the subscriber references so a disposed session stops pinning them
            StateChanged = null;
            SessionStarted = null;
            SessionEnded = null;
            TickUpdated = null;
            DemoPlaybackStatusChanged = null;
            DemoStateChanged = null;
            GameEventReceived = null;
            ProcessStatusChanged = null;
            CommandRejected = null;
            CaptureStateChanged = null;
            CaptureProgressUpdated = null;
            CaptureErrorOccurred = null;
            CompilationClipStarted = null;
            CompilationClipCompleted = null;

            _state = CsvgSessionState.Disconnected;
            _captureInitialized = false;
            _lastCaptureResult = null;
        }

        // The capture session belongs to this object, so disposing it releases the backend (recorder
        // process, WebSocket, process manager). ObsCaptureSession has its own bounded sync bridge; a
        // backend that is async-only gets the same bounded best-effort treatment as the session stop
        // above.
        DisposeCaptureSessionSync(captureSession, captureSessionCts);

        // Tear down the engine session: clears its event handlers and command delegates, resets the
        // latched engine truth, releases anyone still waiting on an outstanding command, and releases
        // anyone still waiting for the session to end.
        CurrentSession.Shutdown("The video session was disposed before the command completed.");
    }

    /// <summary>
    ///     Asynchronously disposes the session, stopping any active or faulted run first (kills CS2
    ///     and restores configuration backups). This is the preferred disposal path; the synchronous
    ///     <see cref="Dispose" /> only makes a time-bounded best-effort stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (State is CsvgSessionState.Connected or CsvgSessionState.Connecting or CsvgSessionState.Faulted)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors during async disposal
            }
        }

        // Dispose the capture session on the async path so the full backend teardown is awaited rather
        // than time-bounded.
        ICaptureSession? captureSession;
        CancellationTokenSource? captureSessionCts;
        lock (_gate)
        {
            captureSession = _captureSession;
            captureSessionCts = _captureSessionCts;
            _captureSession = null;
            _captureSessionCts = null;
        }

        await DisposeCaptureSessionAsync(captureSession, captureSessionCts).ConfigureAwait(false);

        Dispose();
    }

    // Never blocks on an async teardown longer than SyncDisposeStopTimeout.
    private void DisposeCaptureSessionSync(ICaptureSession? captureSession, CancellationTokenSource? sessionCts)
    {
        if (captureSession == null)
        {
            CancelAndDispose(sessionCts);
            return;
        }

        captureSession.ProgressUpdated -= OnCaptureProgressUpdated;
        captureSession.ErrorOccurred -= OnCaptureErrorOccurred;
        CancelAndDispose(sessionCts);

        try
        {
            if (captureSession is IDisposable syncDisposable)
            {
                syncDisposable.Dispose();
                return;
            }

            if (!Task.Run(() => captureSession.DisposeAsync().AsTask()).Wait(SyncDisposeStopTimeout))
            {
                LogSyncDisposeStopTimeout(SyncDisposeStopTimeout.TotalSeconds);
            }
        }
        catch (AggregateException ex)
        {
            LogCaptureStopErrorOnSessionEnd(ex.GetBaseException());
        }
        catch (Exception ex)
        {
            LogCaptureStopErrorOnSessionEnd(ex);
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Session state changed: {OldState} -> {NewState}")]
    partial void LogStateChanged(CsvgSessionState oldState, CsvgSessionState newState);

    [LoggerMessage(LogLevel.Information,
        "Starting video session. ExternalMockServerPath={mockServerPath} MockMode={mockMode}")]
    partial void LogStartingSession(string? mockServerPath, bool mockMode);

    [LoggerMessage(LogLevel.Information,
        "Stopping video session. ExternalMockServerPath={mockServerPath} MockMode={mockMode}")]
    partial void LogStoppingSession(string? mockServerPath, bool mockMode);

    [LoggerMessage(LogLevel.Information, "Stopping active capture during session cleanup")]
    partial void LogStoppingCaptureOnSessionEnd();

    [LoggerMessage(LogLevel.Warning, "Failed to stop capture during session cleanup")]
    partial void LogCaptureStopErrorOnSessionEnd(Exception ex);

    [LoggerMessage(LogLevel.Warning, "Event subscriber threw an exception; continuing with remaining subscribers")]
    partial void LogEventHandlerError(Exception ex);

    [LoggerMessage(LogLevel.Warning,
        "Synchronous Dispose could not stop the session within {TimeoutSeconds}s; CS2 may still be running. Prefer DisposeAsync.")]
    partial void LogSyncDisposeStopTimeout(double timeoutSeconds);

    [LoggerMessage(LogLevel.Warning, "Synchronous Dispose failed to stop the session; CS2 may still be running")]
    partial void LogSyncDisposeStopError(Exception ex);

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Initializing capture provider")]
    partial void LogInitializingCapture();

    [LoggerMessage(EventId = 101, Level = LogLevel.Information,
        Message = "Capture provider initialized successfully (version: {Version})")]
    partial void LogCaptureInitialized(string? version);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Capture provider not available: {Issues}")]
    partial void LogCaptureNotAvailable(string issues);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "Capturing clip: ticks {StartTick}-{EndTick}, output: {OutputFileName}")]
    partial void LogCapturingClip(int startTick, int endTick, string outputFileName);

    [LoggerMessage(EventId = 108, Level = LogLevel.Information,
        Message = "Capture completed: success={Success}, output={OutputFilePath}")]
    partial void LogCaptureCompleted(bool success, string outputFilePath);

    [LoggerMessage(EventId = 112, Level = LogLevel.Debug,
        Message = "Capture state changed: {PreviousState} -> {NewState}")]
    partial void LogCaptureStateChanged(CaptureState previousState, CaptureState newState);

    [LoggerMessage(EventId = 113, Level = LogLevel.Warning,
        Message = "Capture provider error: {Message} (severity: {Severity})")]
    partial void LogCaptureProviderError(string message, CaptureErrorSeverity severity);

    [LoggerMessage(EventId = 114, Level = LogLevel.Debug, Message = "Capture provider already initialized")]
    partial void LogCaptureAlreadyInitialized();

    #endregion
}
