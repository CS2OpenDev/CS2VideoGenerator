#region

using System.Collections.Concurrent;
using System.Diagnostics;
using Cs2VideoGenerator.Core.Compatibility;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     Default <see cref="ICs2EngineSession" /> implementation. Owns the plugin command channel
///     (send delegate, pending acknowledgement/query waiters, command id allocation), the latched
///     engine truth, capability latching and plugin/game compatibility evaluation, and all demo
///     playback control. Wire events are routed in through <see cref="ICs2EngineWire" />.
///     Every wait it performs is guarded against the CS2 process dying underneath it (see
///     <see cref="FailOnProcessExit" />): nothing the engine was supposed to answer can arrive once
///     the process is gone, so those waits fail at once naming the exit rather than timing out.
/// </summary>
public sealed partial class Cs2EngineSession : ICs2EngineSession, ICs2EngineWire
{
    private static readonly TimeSpan DefaultLoadDemoTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Default timeout for session connection (waiting for plugin to report Ready).
    /// </summary>
    private static readonly TimeSpan DefaultSessionConnectTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Default timeout waiting for the plugin's <c>CommandAckEvent</c> on an acknowledged seek.
    ///     Must exceed the plugin's own arrival deadline (<c>kSeekArrivalTimeout</c> = 30 s in
    ///     <c>Cs2PluginClient.h</c>): the plugin deliberately budgets 30 s so a slow backward-seek
    ///     reload still succeeds — a shorter host timeout would report failure while the plugin
    ///     later completes the seek and applies <c>pause_after_seek</c>, diverging host belief
    ///     from engine state.
    /// </summary>
    private static readonly TimeSpan DefaultSeekAckTimeout = TimeSpan.FromSeconds(35);

    private static readonly TimeSpan DefaultDemoStateQueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Shared empty capability set used before the handshake and after session end.
    /// </summary>
    private static readonly IReadOnlySet<string> EmptyCapabilities = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    ///     Pending acknowledgement waiters keyed by command id: an acknowledged command registers a
    ///     TCS here before sending, and <see cref="OnCommandAck" /> completes it from the read loop.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<CommandAckEvent>> _pendingAcks = new();

    /// <summary>
    ///     Pending demo-state-query waiters keyed by command id: completed by the
    ///     <c>DemoStateEvent</c> whose <c>in_reply_to_command_id</c> matches - not by the
    ///     <c>CommandAckEvent</c>, since the query's answer is the state snapshot itself.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<DemoState>> _pendingDemoStateQueries = new();

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly IOptions<Cs2VideoGeneratorOptions> _options;
    private readonly ICs2ProcessManager _processManager;
    private readonly IGrpcEndpointProbe? _endpointProbe;
    private readonly EngineSessionOptions _sessionOptions;

    /// <summary>
    ///     Completed when this engine session ends - the current gRPC session ending (signalled by
    ///     the owning client after its own teardown ordering) or <see cref="Shutdown" />. Created
    ///     with the session object itself so a session that starts and ends during startup still
    ///     signals waiters instead of leaving them hanging, and never replaced: one session object
    ///     is one lifetime.
    /// </summary>
    private readonly TaskCompletionSource<bool> _endedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Completed with the CS2 (or mock) process's exit result the first time the process manager
    ///     reports an exit during this session's lifetime. Every wait in this class races against it
    ///     (see <see cref="FailOnProcessExit" />): a dead CS2 can never deliver the tick, status or
    ///     ack the wait is parked on, so the wait must fail with the exit code instead of burning its
    ///     full timeout. Created with the session object and never reset - one session object is one
    ///     CS2 run, so a latched exit stays true for the rest of this session.
    /// </summary>
    private readonly TaskCompletionSource<ProcessExitResult> _processExitedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Latest engine-truth demo state / host-set timescale (v1.1 protocol additions).
    private DemoState? _lastDemoState;
    private float? _lastKnownTimescale;

    // Monotonically increasing command id source; the first issued id is 1 (0/absent on the wire
    // means "no ack requested", so 0 is never issued).
    private long _nextCommandId;

    // Capability tokens latched from the plugin's process-status handshake. The set is
    // replaced wholesale on every process status (last write wins) and never mutated in place, so
    // the public property can hand out the reference as a stable snapshot.
    private IReadOnlySet<string> _pluginCapabilities = EmptyCapabilities;

    // Plugin/game version compatibility: evaluated once per session from the first
    // process-status (handshake) message. Only a known-broken pair may block session start.
    private PluginCompatibilityChecker? _compatibilityChecker;
    private bool _compatibilityEvaluated;
    private PluginCompatibilityResult? _compatibilityResult;
    private Action? _requestSessionStop;
    private Func<CsControlRequest, Task>? _sendAsync;

    internal Cs2EngineSession(ILogger logger, IOptions<Cs2VideoGeneratorOptions> options,
        ICs2ProcessManager processManager, IGrpcEndpointProbe? endpointProbe,
        EngineSessionOptions sessionOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _endpointProbe = endpointProbe;
        _sessionOptions = sessionOptions ?? throw new ArgumentNullException(nameof(sessionOptions));

        // Subscribed for the whole session lifetime (released in Shutdown) rather than per wait: the
        // exit has to be latched even when no wait is in flight, so a wait started after CS2 died
        // fails just as fast as one that was already parked when it died.
        _processManager.Exited += OnProcessExited;
    }

    /// <summary>
    ///     Time to wait for a seek <c>CommandAckEvent</c> (default 35 seconds - see
    ///     <see cref="DefaultSeekAckTimeout" /> for why it exceeds the plugin's own 30 s arrival
    ///     deadline). Internal-settable purely as a test seam; production code never changes it.
    /// </summary>
    internal TimeSpan SeekAckTimeout { get; set; } = DefaultSeekAckTimeout;

    /// <summary>
    ///     Time to wait for the <c>DemoStateEvent</c> reply to a demo state query (default
    ///     10 seconds). Internal-settable purely as a test seam; production code never changes it.
    /// </summary>
    internal TimeSpan DemoStateQueryTimeout { get; set; } = DefaultDemoStateQueryTimeout;

    /// <summary>
    ///     How long to wait for an ordinary <c>CommandAckEvent</c> (see
    ///     <see cref="SendAcknowledgedCommandAsync" />). Short compared with the seek-ack timeout:
    ///     these commands are applied on the plugin's reader thread as soon as the message is read,
    ///     with no engine round-trip to wait for.
    /// </summary>
    internal TimeSpan CommandAckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    #region Events

    /// <inheritdoc />
    public event Action<string, long>? TickUpdated;

    /// <inheritdoc />
    public event Action<long>? TickObserved;

    /// <inheritdoc />
    public event Func<string, DemoPlaybackStatusChange, Task>? DemoPlaybackStatusChanged;

    /// <inheritdoc />
    public event Func<string, DemoState, Task>? DemoStateChanged;

    /// <inheritdoc />
    public event Func<string, GameEventTelemetry, Task>? GameEventReceived;

    /// <summary>
    ///     Raised when a command is rejected because no active session exists.
    /// </summary>
    public event Func<string, Task>? CommandRejected;

    /// <inheritdoc />
    public event Func<string, Cs2ProcessStatus, Task>? ProcessStatusChanged;

    /// <inheritdoc />
    public event Func<string, Task>? Started;

    /// <inheritdoc />
    public event Func<string, long?, DateTimeOffset, Task>? Ended;

    #endregion

    #region State Properties

    /// <inheritdoc />
    public string? SessionId { get; private set; }

    /// <inheritdoc />
    public long? LastTick { get; private set; }

    /// <inheritdoc />
    public Cs2ProcessStatus? LastProcessStatus { get; private set; }

    /// <inheritdoc />
    public DemoPlaybackStatusChange? LastDemoPlaybackStatusChange { get; private set; }

    /// <inheritdoc />
    public IReadOnlySet<string> PluginCapabilities
    {
        get
        {
            lock (_gate)
            {
                return _pluginCapabilities;
            }
        }
    }

    /// <inheritdoc />
    public bool Supports(string capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
        {
            return _pluginCapabilities.Contains(capability);
        }
    }

    /// <inheritdoc />
    public DemoState? LastDemoState
    {
        get
        {
            lock (_gate)
            {
                return _lastDemoState;
            }
        }
    }

    /// <inheritdoc />
    public float? LastKnownTimescale
    {
        get
        {
            lock (_gate)
            {
                return _lastKnownTimescale;
            }
        }
    }

    /// <inheritdoc />
    public bool? LastTickIsPaused { get; private set; }

    IReadOnlySet<string> IEngineConsole.Capabilities => PluginCapabilities;

    long? IEngineConsole.LastObservedTick => LastTick;

    /// <summary>
    ///     The plugin/game compatibility verdict for this session, or null when no evaluation has
    ///     produced one yet (see <see cref="EvaluateCompatibilityOnce" />).
    /// </summary>
    private PluginCompatibilityResult? CompatibilityResult
    {
        get
        {
            lock (_gate)
            {
                return _compatibilityResult;
            }
        }
    }

    /// <summary>
    ///     The graceful-stop delegate installed by the active gRPC session, or null when none is
    ///     available.
    /// </summary>
    private Action? RequestSessionStop
    {
        get
        {
            lock (_gate)
            {
                return _requestSessionStop;
            }
        }
    }

    /// <summary>
    ///     Completed when this engine session has ended (current gRPC session end, or
    ///     <see cref="Shutdown" />). Waiters wrap it with <c>WaitAsync</c> rather than cancelling
    ///     it: cancelling the shared source would poison every other current and future waiter
    ///     (including <see cref="StopAsync" />'s graceful-stop wait) just because one caller gave
    ///     up.
    /// </summary>
    internal Task SessionEndedTask => _endedTcs.Task;

    /// <summary>
    ///     The owner's synchronous reaction to this session ending, invoked from
    ///     <see cref="OnSessionEndedAsync" /> after the session's own teardown and before
    ///     <see cref="SessionEndedTask" /> completes - so a waiter woken by the latch never observes
    ///     a not-yet-updated owner state. Set by whoever owns the client-facing session state; null
    ///     when nobody does. A stale session's end returns before this runs.
    /// </summary>
    internal Action? EndedSynchronously { get; set; }

    #endregion

    #region Wire Event Ingestion

    /// <summary>
    ///     Applies a new game session taking over: latches the session id, resets the per-session
    ///     engine truth and capability/compatibility latches, fails pending command waiters, then
    ///     dispatches <see cref="Started" />. The dispatch is awaited, so a caller on the plugin's
    ///     read loop sees subscriber handlers run inline.
    /// </summary>
    internal async Task OnSessionStartedAsync(string sessionId)
    {
        LogSessionStarted(sessionId);

        Func<string, Task>? handler;
        lock (_gate)
        {
            SessionId = sessionId;
            LastTick = null;
            LastTickIsPaused = null;
            LastProcessStatus = null;
            LastDemoPlaybackStatusChange = null;
            _lastDemoState = null;
            _lastKnownTimescale = null;
            _pluginCapabilities = EmptyCapabilities;
            _compatibilityEvaluated = false;
            _compatibilityResult = null;
            handler = Started;
        }

        // A new session taking over means any command still awaiting an ack from the previous
        // connection can never be answered (the new plugin session knows nothing of those ids).
        FailPendingCommandWaiters($"Session '{sessionId}' replaced the previous session before the command completed.");

        await InvokeAsyncHandler(handler, sessionId).ConfigureAwait(false);
    }

    /// <summary>
    ///     Applies a session ending: engine-side teardown, the owner's synchronous reaction
    ///     (<see cref="EndedSynchronously" />), the session-ended latch, then the awaited
    ///     <see cref="Ended" /> dispatch - in that order, so a waiter released by the latch never
    ///     observes a stale owner state and subscribers never run before the latch is set.
    /// </summary>
    /// <remarks>
    ///     Teardown only applies when the ending session is the <em>current</em> one: a stale session
    ///     (e.g. the previous connection ending after the plugin reconnected) must not wipe the new
    ///     session's command sender, and is ignored before anything else runs.
    /// </remarks>
    internal async Task OnSessionEndedAsync(string sessionId, long? lastTick, DateTimeOffset lastSeenUtc)
    {
        LogSessionEnded(sessionId, lastTick);

        Func<string, long?, DateTimeOffset, Task>? handler;
        Action? endedSynchronously;
        lock (_gate)
        {
            if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
            {
                // A session that is no longer current ended (the plugin reconnected and a newer
                // session took over). Ignore it - tearing down here would break the live session.
                LogStaleSessionEndIgnored(sessionId, SessionId);
                return;
            }

            SessionId = null;
            _sendAsync = null;
            _requestSessionStop = null;
            _pluginCapabilities = EmptyCapabilities; // No connected plugin -> no capabilities.
            handler = Ended;
            endedSynchronously = EndedSynchronously;
        }

        // Nothing can acknowledge outstanding commands once the stream is gone - fail their
        // waiters now instead of letting them burn their full ack timeouts.
        FailPendingCommandWaiters($"Session '{sessionId}' ended before the command completed.");

        // The owner reacts (e.g. faulting its session state) before waiters are released.
        endedSynchronously?.Invoke();
        _endedTcs.TrySetResult(true);

        await InvokeAsyncHandler(handler, sessionId, lastTick, lastSeenUtc).ConfigureAwait(false);
    }

    /// <summary>
    ///     Applies a tick update. <paramref name="isPaused" /> carries the optional
    ///     <c>TickUpdateEvent.is_paused</c> field (null when the plugin build predates it);
    ///     it updates <see cref="LastTickIsPaused" /> only - the hot <see cref="TickUpdated" />
    ///     event signature deliberately stays <c>Action&lt;string, long&gt;</c>.
    /// </summary>
    internal void OnTickUpdated(string sessionId, long currentTick, bool? isPaused = null)
    {
        LogTickReceived(currentTick);
        Action<string, long>? handler;
        Action<long>? observedHandler;
        lock (_gate)
        {
            LastTick = currentTick;
            if (isPaused.HasValue)
            {
                LastTickIsPaused = isPaused;
            }

            handler = TickUpdated;
            observedHandler = TickObserved;
        }

        LogTickHandlerStatus(handler != null);
        handler?.Invoke(sessionId, currentTick);
        observedHandler?.Invoke(currentTick);
    }

    internal async Task OnDemoPlaybackStatusChangedAsync(string sessionId, DemoPlaybackStatusChange statusChange)
    {
        Func<string, DemoPlaybackStatusChange, Task>? handler;
        lock (_gate)
        {
            LastDemoPlaybackStatusChange = statusChange;
            handler = DemoPlaybackStatusChanged;
        }

        await InvokeAsyncHandler(handler, sessionId, statusChange);
    }

    internal void OnCommandSenderAvailable(string sessionId, Func<CsControlRequest, Task> sendAsync,
        Action? requestStop = null)
    {
        LogCommandSenderAvailable(sessionId);
        lock (_gate)
        {
            if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
            {
                SessionId = sessionId;
            }

            _sendAsync = sendAsync;
            _requestSessionStop = requestStop;
        }
    }

    /// <summary>
    ///     Applies a CS2 process status update: latches the status and advertised capabilities,
    ///     evaluates the plugin/game compatibility handshake, and dispatches
    ///     <see cref="ProcessStatusChanged" />.
    /// </summary>
    internal async Task OnProcessStatusChangedAsync(string sessionId, Cs2ProcessStatus status)
    {
        Func<string, Cs2ProcessStatus, Task>? handler;
        lock (_gate)
        {
            LastProcessStatus = status;

            // Latch the advertised capability tokens alongside the version fields:
            // every process status re-latches, last write wins. An absent/empty list (v1.0 plugin)
            // latches the empty set - capability-gated APIs then degrade per their documentation.
            _pluginCapabilities = status.Capabilities.Count == 0
                ? EmptyCapabilities
                : new HashSet<string>(status.Capabilities, StringComparer.Ordinal);
            handler = ProcessStatusChanged;
        }

        if (status.Capabilities.Count > 0)
        {
            LogPluginCapabilitiesLatched(sessionId, string.Join(",", status.Capabilities));
        }

        // Evaluate the plugin/game version handshake before notifying subscribers so the
        // session-connect ready-wait observes the compatibility verdict.
        EvaluateCompatibilityOnce(status);

        LogProcessStatusChanged(sessionId, status.Status);
        await InvokeAsyncHandler(handler, sessionId, status);
    }

    /// <summary>
    ///     Applies an engine-truth demo state snapshot. Maps the wire event to
    ///     <see cref="Models.DemoState" />, updates <see cref="LastDemoState" /> (and
    ///     <see cref="LastKnownTimescale" /> when the event carries a timescale), completes a
    ///     pending <see cref="QueryDemoStateAsync" /> whose command id matches
    ///     <c>in_reply_to_command_id</c>, then dispatches <see cref="DemoStateChanged" /> with the
    ///     usual per-subscriber exception isolation.
    /// </summary>
    internal async Task OnDemoStateAsync(string sessionId, DemoStateEvent stateEvent)
    {
        DemoState state = DemoState.FromProto(stateEvent);
        LogDemoStateReceived(sessionId, state.IsPlayingDemo, state.IsPaused, state.DemoTick, state.Origin);

        Func<string, DemoState, Task>? handler;
        lock (_gate)
        {
            _lastDemoState = state;
            if (state.Timescale is { } timescale)
            {
                _lastKnownTimescale = timescale;
            }

            handler = DemoStateChanged;
        }

        // A demo state carrying in_reply_to_command_id answers a pending QueryDemoStateAsync;
        // completion happens before the public event so the querying caller never observes its own
        // reply arriving "after" a subscriber already saw it fail.
        if (stateEvent.HasInReplyToCommandId &&
            _pendingDemoStateQueries.TryRemove(stateEvent.InReplyToCommandId,
                out TaskCompletionSource<DemoState>? queryTcs))
        {
            queryTcs.TrySetResult(state);
        }

        await InvokeAsyncHandler(handler, sessionId, state);
    }

    /// <summary>
    ///     Applies a forwarded client-side game event ("game-event-telemetry"). Dispatches
    ///     <see cref="GameEventReceived" /> with the usual per-subscriber exception isolation. No
    ///     state is latched: this is a pure pass-through stream, and buffering an unbounded event
    ///     history would be a leak, not a feature.
    /// </summary>
    internal async Task OnGameEventTelemetryAsync(string sessionId, GameEventTelemetryEvent telemetryEvent)
    {
        GameEventTelemetry telemetry = GameEventTelemetry.FromProto(telemetryEvent);
        LogGameEventReceived(sessionId, telemetry.EventName);

        Func<string, GameEventTelemetry, Task>? handler;
        lock (_gate)
        {
            handler = GameEventReceived;
        }

        await InvokeAsyncHandler(handler, sessionId, telemetry);
    }

    /// <summary>
    ///     Applies a command acknowledgement for a command that carried a command id. Routed
    ///     synchronously (no async event surface): the ack's only job is to complete the pending
    ///     waiter registered by the sending method. An ack for an unknown id (waiter already timed
    ///     out / cancelled, or a stale session's ack) is debug-logged and dropped.
    /// </summary>
    internal void OnCommandAck(string sessionId, CommandAckEvent ack)
    {
        if (_pendingAcks.TryRemove(ack.CommandId, out TaskCompletionSource<CommandAckEvent>? ackTcs))
        {
            ackTcs.TrySetResult(ack);
        }
        else
        {
            LogUnknownCommandAck(sessionId, ack.CommandId);
        }
    }

    // The ingestion methods above are internal, which C# does not accept as an implicit
    // implementation of an interface member, so the transport seam is bound explicitly. Keeping the
    // bodies internal is what lets tests drive them by name without going through the interface.

    Task ICs2EngineWire.OnSessionStartedAsync(string sessionId) => OnSessionStartedAsync(sessionId);

    Task ICs2EngineWire.OnSessionEndedAsync(string sessionId, long? lastTick, DateTimeOffset lastSeenUtc) =>
        OnSessionEndedAsync(sessionId, lastTick, lastSeenUtc);

    void ICs2EngineWire.OnCommandSenderAvailable(string sessionId, Func<CsControlRequest, Task> sendAsync,
        Action? requestStop) =>
        OnCommandSenderAvailable(sessionId, sendAsync, requestStop);

    void ICs2EngineWire.OnTickUpdated(string sessionId, long currentTick, bool? isPaused) =>
        OnTickUpdated(sessionId, currentTick, isPaused);

    Task ICs2EngineWire.OnDemoPlaybackStatusChangedAsync(string sessionId, DemoPlaybackStatusChange statusChange) =>
        OnDemoPlaybackStatusChangedAsync(sessionId, statusChange);

    Task ICs2EngineWire.OnProcessStatusChangedAsync(string sessionId, Cs2ProcessStatus status) =>
        OnProcessStatusChangedAsync(sessionId, status);

    Task ICs2EngineWire.OnDemoStateAsync(string sessionId, DemoStateEvent stateEvent) =>
        OnDemoStateAsync(sessionId, stateEvent);

    Task ICs2EngineWire.OnGameEventTelemetryAsync(string sessionId, GameEventTelemetryEvent telemetryEvent) =>
        OnGameEventTelemetryAsync(sessionId, telemetryEvent);

    void ICs2EngineWire.OnCommandAck(string sessionId, CommandAckEvent ack) => OnCommandAck(sessionId, ack);

    /// <summary>
    ///     Allocates the next command id. Ids are monotonically increasing and start at 1 - 0 is
    ///     never issued because an absent/zero <c>command_id</c> on the wire means "no ack
    ///     requested".
    /// </summary>
    private ulong NextCommandId() => (ulong)Interlocked.Increment(ref _nextCommandId);

    /// <summary>
    ///     Fails every pending command-ack and demo-state-query waiter with an
    ///     <see cref="InvalidOperationException" /> carrying <paramref name="reason" />. Called when
    ///     the session ends, when a new session replaces the current one, and on dispose - once the
    ///     originating stream is gone the plugin can never answer, so waiters must fail immediately
    ///     instead of burning their full timeouts.
    /// </summary>
    private void FailPendingCommandWaiters(string reason)
    {
        foreach (ulong commandId in _pendingAcks.Keys)
        {
            if (_pendingAcks.TryRemove(commandId, out TaskCompletionSource<CommandAckEvent>? ackTcs))
            {
                FailWaiter(ackTcs, new InvalidOperationException(reason));
            }
        }

        foreach (ulong commandId in _pendingDemoStateQueries.Keys)
        {
            if (_pendingDemoStateQueries.TryRemove(commandId, out TaskCompletionSource<DemoState>? queryTcs))
            {
                FailWaiter(queryTcs, new InvalidOperationException(reason));
            }
        }
    }

    /// <summary>
    ///     Fails a pending waiter with <paramref name="error" /> and observes the resulting exception:
    ///     the waiter may already have given up (timeout/cancel race), and an unobserved faulted task
    ///     would surface as an <c>UnobservedTaskException</c>.
    /// </summary>
    private static void FailWaiter<T>(TaskCompletionSource<T> waiter, Exception error)
    {
        if (waiter.TrySetException(error))
        {
            _ = waiter.Task.Exception;
        }
    }

    /// <summary>
    ///     Evaluates the plugin/game version compatibility check exactly once per session, from the
    ///     first process-status message (the handshake carries the version fields on every status).
    ///     Policy: game updates usually do not break the plugin - matching/known-good versions
    ///     log debug, newer or unknown versions log a single warning and proceed, and only version pairs on
    ///     the known-broken list block session start (overridable via
    ///     <see cref="Cs2VideoGeneratorOptions.ForceIncompatiblePlugin" /> / <c>--force</c>).
    /// </summary>
    private void EvaluateCompatibilityOnce(Cs2ProcessStatus status)
    {
        // The whole evaluation runs under the gate: two callers race here (the session read loop's
        // per-status dispatch and the ready-wait's post-launch LastProcessStatus consultation), and
        // the "already evaluated" latch must never be observable before the verdict has been
        // published to _compatibilityResult - otherwise the ready-wait reads a null verdict and a
        // known-broken pair silently connects.
        lock (_gate)
        {
            if (_compatibilityEvaluated)
            {
                return;
            }

            _compatibilityEvaluated = true;
            EvaluateCompatibilityCore(status);
        }
    }

    private void EvaluateCompatibilityCore(Cs2ProcessStatus status)
    {
        Cs2VideoGeneratorOptions opts = _options.Value;
        string? pluginVersion = status.HasPluginVersion ? status.PluginVersion : null;
        string? hl2SdkCommit = status.HasHl2SdkCommit ? status.Hl2SdkCommit : null;
        string? gameVersion = status.HasGameVersion ? status.GameVersion : null;

        // Mock mode has no running game: no game version is expected, so an absent one is not
        // worth a warning. A simulated version (CSVG_MOCK_GAME_VERSION) still gets the full check.
        if (opts.MockMode && string.IsNullOrWhiteSpace(gameVersion))
        {
            LogCompatibilityMockNoGameVersion(pluginVersion);
            return;
        }

        PluginCompatibilityResult result;
        try
        {
            _compatibilityChecker ??=
                new PluginCompatibilityChecker(PluginCompatibilityData.Load(opts.CompatibilityOverridePath));
            result = _compatibilityChecker.Check(pluginVersion, gameVersion);
        }
        catch (Exception ex)
        {
            // The compatibility check itself must never break a session.
            LogCompatibilityCheckFailed(ex);
            return;
        }

        lock (_gate)
        {
            _compatibilityResult = result;
        }

        switch (result.Status)
        {
            case PluginCompatibilityStatus.Compatible:
                LogCompatibilityOk(result.Message, hl2SdkCommit);
                break;
            case PluginCompatibilityStatus.KnownIncompatible when opts.ForceIncompatiblePlugin:
                LogCompatibilityForced(result.Message);
                break;
            case PluginCompatibilityStatus.KnownIncompatible:
                LogCompatibilityKnownBroken(result.Message);
                break;
            case PluginCompatibilityStatus.UnknownVersion:
            case PluginCompatibilityStatus.GameVersionMismatch:
            default:
                LogCompatibilityWarning(result.Message);
                break;
        }
    }

    #endregion

    #region Session Lifecycle (Connect / Stop)

    /// <summary>
    ///     Brings this engine session up: verifies the host's gRPC endpoint is listening (fast-fail
    ///     before doing any work - the plugin and the mock server connect back to it, so a session
    ///     could never become ready otherwise), runs the caller's optional pre-launch
    ///     initialization, launches CS2 via the process manager using the launch shape in
    ///     <see cref="EngineSessionOptions" />, waits for the plugin to connect and report Ready
    ///     (including the process-exit race and compatibility evaluation), then applies engine-scoped
    ///     post-ready setup (the configured game-event subscription).
    /// </summary>
    /// <param name="activity">
    ///     The caller's session-start activity; ready/compatibility/timeout tags are recorded on it
    ///     so the telemetry shape matches the pre-move behavior. May be null.
    /// </param>
    /// <param name="preLaunchInitializationAsync">
    ///     Optional hook run after the endpoint probe but before CS2 is launched. This is the
    ///     capture-before-CS2 ordering seam: OBS's game-capture must be up before the game starts,
    ///     while the endpoint probe must still fail fast before any capture work begins.
    ///     <see cref="CsvgVideoSession" /> passes <c>ICaptureSession.InitializeAsync</c> here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token to abort startup.</param>
    internal async Task ConnectAsync(Activity? activity,
        Func<CancellationToken, Task>? preLaunchInitializationAsync, CancellationToken cancellationToken)
    {
        Cs2VideoGeneratorOptions opts = _options.Value;

        // Fast-fail before doing any work: the CS2 plugin (and the mock server) connect back to
        // the gRPC server this host is supposed to expose. If nothing is listening, the session
        // could never become ready and would burn the full connect timeout.
        await VerifyGrpcEndpointListeningAsync(opts.GrpcPort, cancellationToken).ConfigureAwait(false);

        if (preLaunchInitializationAsync != null)
        {
            await preLaunchInitializationAsync(cancellationToken).ConfigureAwait(false);
        }

        await LaunchAndWaitForReadyAsync(activity, cancellationToken).ConfigureAwait(false);

        await ApplyConfiguredGameEventSubscriptionAsync(opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Verifies (via the injected <see cref="IGrpcEndpointProbe" />, when available) that the
    ///     orchestrator's gRPC server is actually listening on the configured loopback port before
    ///     CS2 (or the mock server) is launched. Both the real plugin and the mock server connect
    ///     back to this endpoint, so a host that never called <c>AddGrpc()</c> /
    ///     <c>MapGrpcService&lt;Cs2GameService&gt;()</c> would otherwise time out after minutes
    ///     with no root-cause hint.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Nothing is accepting connections on localhost:<paramref name="port" />.
    /// </exception>
    private async Task VerifyGrpcEndpointListeningAsync(int port, CancellationToken cancellationToken)
    {
        if (_endpointProbe == null)
        {
            return;
        }

        LogProbingGrpcEndpoint(port);
        if (await _endpointProbe.IsListeningAsync(port, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        LogGrpcEndpointNotListening(port);
        throw new InvalidOperationException(
            $"gRPC endpoint not listening on localhost:{port}. The CS2 plugin (and the mock server) " +
            "connect back to this port, so the session can never become ready. Library consumers must " +
            "host it themselves: builder.Services.AddGrpc(); ... app.MapGrpcService<Cs2GameService>(); " +
            "and start the host (with Kestrel listening on HTTP/2 at this port) before calling " +
            "StartSessionAsync. Set the port via the \"Cs2VideoGenerator:GrpcPort\" configuration key " +
            "(default 50051). See the \"Using Core as a library\" section of the README.");
    }

    /// <summary>
    ///     Launches CS2 and waits for the plugin to connect and report Ready.
    ///     The status subscription is installed before launch (and <see cref="LastProcessStatus" /> is
    ///     consulted after launch) so a Ready delivered during the post-launch delay window is not lost,
    ///     and the wait races against process exit so a CS2 that dies during startup fails fast with its
    ///     exit code instead of burning the full connect timeout.
    /// </summary>
    private async Task LaunchAndWaitForReadyAsync(Activity? activity, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void EvaluateStatus(Cs2ProcessStatus status)
        {
            // Idempotent: ensures the handshake versions are evaluated even when this races the
            // per-status evaluation in OnProcessStatusChangedAsync (e.g. the post-launch
            // LastProcessStatus consultation below).
            EvaluateCompatibilityOnce(status);

            if (status.Status == Cs2ProcessStatus.Types.ProcessStatus.Ready)
            {
                // Only a version pair on the explicit known-broken list may block here; newer or
                // unknown versions were already logged as a warning and proceed.
                PluginCompatibilityResult? compatibility = CompatibilityResult;

                if (compatibility is { IsKnownIncompatible: true } && !_options.Value.ForceIncompatiblePlugin)
                {
                    activity?.SetTag("csvg.session.ready", false);
                    activity?.SetTag("csvg.plugin.incompatible", true);
                    readyTcs.TrySetException(new PluginIncompatibleException(compatibility.Message));
                    return;
                }

                activity?.SetTag("csvg.session.ready", true);
                readyTcs.TrySetResult(true);
            }
            else if (status.Status == Cs2ProcessStatus.Types.ProcessStatus.Error)
            {
                readyTcs.TrySetException(
                    new InvalidOperationException($"CS2 plugin reported error: {status.ErrorMessage}"));
            }
        }

        Task StatusHandler(string sessionId, Cs2ProcessStatus status)
        {
            EvaluateStatus(status);
            return Task.CompletedTask;
        }

        ProcessStatusChanged += StatusHandler;

        // Armed before the launch, like the status subscription: a CS2 that dies on startup (Steam
        // not running, plugin crash on load) must fail the connect with its exit code rather than
        // burn the two-minute plugin-connect timeout.
        using ProcessExitWatch processExitWatch = FailOnProcessExit(
            "the CS2 plugin to connect and report Ready",
            exitError => FailWaiter(readyTcs, exitError));

        try
        {
            // Launch CS2 process with display options (returns after process start + post-launch delay)
            await _processManager.LaunchAsync(_sessionOptions.Width, _sessionOptions.Height,
                _sessionOptions.Fullscreen, cancellationToken).ConfigureAwait(false);

            // The plugin may already have reported a status while LaunchAsync was still running
            // (e.g. during the post-launch delay); consult the latest recorded status so it isn't lost.
            Cs2ProcessStatus? lastStatus = LastProcessStatus;

            if (lastStatus != null)
            {
                EvaluateStatus(lastStatus);
            }

            try
            {
                await readyTcs.Task.WaitAsync(DefaultSessionConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                activity?.SetTag("csvg.timeout.occurred", true);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation timed out");
                throw new TimeoutException(
                    $"Timed out after {DefaultSessionConnectTimeout.TotalSeconds:F1} seconds waiting for the CS2 plugin to connect and report Ready. " +
                    "If CS2 was recently updated, the plugin may be incompatible with the new game build - " +
                    "run 'csvg doctor' and check for a CSVG update.");
            }
        }
        finally
        {
            ProcessStatusChanged -= StatusHandler;
        }
    }

    /// <summary>
    ///     Applies <see cref="Cs2VideoGeneratorOptions.EnableGameEventTelemetry" /> once the plugin
    ///     handshake has landed. Off by default, so the usual path does nothing at all.
    /// </summary>
    /// <remarks>
    ///     Never fails session start: telemetry is observability, and a plugin build without the
    ///     capability (currently the Linux plugin, which has no game-event hook) must not stop a
    ///     capture run. Both the missing capability and a send failure log a warning and proceed,
    ///     which is the warn-not-block policy applied to an opt-in extra - callers that need the
    ///     hard failure should call <see cref="SetGameEventSubscriptionAsync" /> themselves.
    /// </remarks>
    private async Task ApplyConfiguredGameEventSubscriptionAsync(Cs2VideoGeneratorOptions opts,
        CancellationToken cancellationToken)
    {
        if (!opts.EnableGameEventTelemetry)
        {
            return;
        }

        if (!Supports(CsvgCapabilities.GameEventTelemetry))
        {
            LogGameEventTelemetryUnsupported();
            return;
        }

        try
        {
            await SetGameEventSubscriptionAsync(true, opts.GameEventNames, cancellationToken).ConfigureAwait(false);
            LogGameEventTelemetryEnabled(opts.GameEventNames?.Count ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGameEventTelemetryEnableFailed(ex);
        }
    }

    /// <summary>
    ///     Brings this engine session down: requests the gRPC session to stop gracefully, waits
    ///     briefly for the acknowledgement, then stops the CS2 process. The graceful stop cancels
    ///     the session's read loop, allowing it to exit cleanly via OperationCanceledException
    ///     rather than IOException when the client process dies. Capture teardown ordering is the
    ///     caller's responsibility: the OBS provider must already be disposed before this method
    ///     kills CS2 (killing CS2 while OBS still has a game-capture hook into it makes OBS fault
    ///     during its own teardown and pop an interaction-blocking crash dialog).
    /// </summary>
    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Action? stopSession = RequestSessionStop;
        if (stopSession != null)
        {
            LogRequestingGracefulSessionStop();

            // Never let a stop-request failure (e.g. the session ending naturally at the same
            // moment) abort the stop - CS2 must still be stopped and backups restored below.
            try
            {
                stopSession();
            }
            catch (Exception ex)
            {
                LogSessionStopRequestError(ex);
            }

            try
            {
                await _endedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
                LogSessionStoppedGracefully();
            }
            catch (TimeoutException)
            {
                // Session didn't stop in time, proceed to kill CS2 anyway
                LogSessionStopTimeout();
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled, proceed
            }
        }

        await _processManager.StopAsync(cancellationToken).ConfigureAwait(false);
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

        IEnumerable<Func<T, Task>> handlers = handler.GetInvocationList().Cast<Func<T, Task>>();
        foreach (Func<T, Task> h in handlers)
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

        IEnumerable<Func<T1, T2, Task>> handlers = handler.GetInvocationList().Cast<Func<T1, T2, Task>>();
        foreach (Func<T1, T2, Task> h in handlers)
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

        IEnumerable<Func<T1, T2, T3, Task>> handlers = handler.GetInvocationList().Cast<Func<T1, T2, T3, Task>>();
        foreach (Func<T1, T2, T3, Task> h in handlers)
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

    #region Demo Control Commands

    /// <summary>
    ///     Maximum accepted demo timescale (sanity bound for <see cref="SetDemoTimescaleAsync" />).
    /// </summary>
    internal const float MaxDemoTimescale = 8.0f;

    /// <inheritdoc />
    public Task ResumeDemoAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.ResumeDemo", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        return SendCommandAsync(() => new CsControlRequest { ResumeDemo = new ResumeDemoCommand() }, cancellationToken);
    }

    /// <inheritdoc />
    public Task PauseDemoAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.PauseDemo", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        return SendCommandAsync(() => new CsControlRequest { PauseDemo = new PauseDemoCommand() }, cancellationToken);
    }

    /// <inheritdoc />
    public Task CloseDemoAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.CloseDemo", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        return SendCommandAsync(() => new CsControlRequest { CloseDemo = new CloseDemoCommand() }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetDemoTickAsync(int tick, CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.SetDemoTick", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.tick", tick);
        return SendCommandAsync(() => new CsControlRequest { SetDemoTick = new SetDemoTickCommand { DemoTick = tick } },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SeekResult> SetDemoTickAsync(int demoTick, bool? pauseAfterSeek, bool waitForCompletion,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.SetDemoTick", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.tick", demoTick);
        activity?.SetTag("csvg.seek.pauseAfterSeek", pauseAfterSeek);
        activity?.SetTag("csvg.seek.waitForCompletion", waitForCompletion);

        SetDemoTickCommand seek = new() { DemoTick = demoTick };
        if (pauseAfterSeek.HasValue)
        {
            // Included regardless of capability: a plugin without "seek-ack" simply ignores the
            // optional field, which is exactly the documented legacy degradation.
            seek.PauseAfterSeek = pauseAfterSeek.Value;
        }

        if (!waitForCompletion)
        {
            // Legacy path: fire-and-forget, no command id (so no ack is ever requested). Success
            // means only that the command was written to the plugin stream.
            await SendCommandAsync(() => new CsControlRequest { SetDemoTick = seek }, cancellationToken)
                .ConfigureAwait(false);
            return new SeekResult(true, null, null);
        }

        if (!Supports(CsvgCapabilities.CommandAck))
        {
            throw new NotSupportedException(
                $"Cannot wait for seek completion: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.CommandAck}' capability. Update the CSVG plugin, or call with " +
                "waitForCompletion: false for the legacy fire-and-forget behavior.");
        }

        ulong commandId = NextCommandId();
        activity?.SetTag("csvg.command.id", commandId);
        TaskCompletionSource<CommandAckEvent> ackTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[commandId] = ackTcs;

        // The seek ack budget is the longest in the class (35 s, matching the plugin's own arrival
        // deadline), so a CS2 that dies mid-seek is exactly the case worth short-circuiting.
        using ProcessExitWatch processExitWatch = FailOnProcessExit(
            $"the plugin to acknowledge SetDemoTick (command id {commandId}, target tick {demoTick})",
            exitError => FailWaiter(ackTcs, exitError));

        try
        {
            await SendCommandAsync(() => new CsControlRequest { SetDemoTick = seek, CommandId = commandId },
                cancellationToken).ConfigureAwait(false);

            CommandAckEvent ack;
            try
            {
                ack = await ackTcs.Task.WaitAsync(SeekAckTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                activity?.SetTag("csvg.timeout.occurred", true);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation timed out");
                throw new TimeoutException(
                    $"Timed out after {SeekAckTimeout.TotalSeconds:F1} seconds waiting for the plugin to " +
                    $"acknowledge SetDemoTick (command id {commandId}, target tick {demoTick}).");
            }

            activity?.SetTag("csvg.seek.success", ack.Success);
            activity?.SetTag("csvg.seek.observedTick", ack.HasObservedDemoTick ? ack.ObservedDemoTick : null);
            return new SeekResult(
                ack.Success,
                ack.HasObservedDemoTick ? ack.ObservedDemoTick : null,
                ack.HasErrorMessage ? ack.ErrorMessage : null);
        }
        finally
        {
            _pendingAcks.TryRemove(commandId, out _);
        }
    }

    /// <inheritdoc />
    public Task SetSpectatorTargetAsync(string playerName, CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.SetSpectatorTarget", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.player.name", playerName);
        return SendCommandAsync(
            () => new CsControlRequest
            {
                SetSpectatorTarget = new SetSpectatorTargetCommand { PlayerName = playerName }
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetDemoOptionsAsync(Cs2ClipOptions options, CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.SetDemoOptions", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.demo.options", options.ToString());
        return SendCommandAsync(
            () => new CsControlRequest
            {
                SetDemoOptions = new SetDemoOptionsCommand
                {
                    XrayEnabled = options.HasFlag(Cs2ClipOptions.XrayEnabled),
                    TrueViewEnabled = options.HasFlag(Cs2ClipOptions.TrueViewEnabled),
                    HudEnabled = options.HasFlag(Cs2ClipOptions.HudEnabled),
                    ShowAssistsInFeed = options.HasFlag(Cs2ClipOptions.ShowAssistsInFeed),
                    ShowOnlyDeathNotices = options.HasFlag(Cs2ClipOptions.ShowOnlyDeathNotices)
                }
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetTickUpdateFrequencyAsync(int frequency, CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.SetTickUpdateFrequency", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.tick.frequency", frequency);
        return SendCommandAsync(
            () => new CsControlRequest
            {
                SetTickUpdateFrequency = new SetTickUpdateFrequencyCommand { Frequency = frequency }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetDemoTimescaleAsync(float timescale, CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(timescale) || timescale <= 0f || timescale > MaxDemoTimescale)
        {
            throw new ArgumentOutOfRangeException(nameof(timescale), timescale,
                $"Demo timescale must be a finite value greater than 0 and at most {MaxDemoTimescale:F1}.");
        }

        if (!Supports(CsvgCapabilities.TimescaleSet))
        {
            throw new NotSupportedException(
                $"Cannot set the demo timescale: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.TimescaleSet}' capability. Update the CSVG plugin.");
        }

        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.SetDemoTimescale", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.demo.timescale", timescale);

        await SendCommandAsync(
            () => new CsControlRequest { SetDemoTimescale = new SetDemoTimescaleCommand { Timescale = timescale } },
            cancellationToken).ConfigureAwait(false);

        // Only a successful send updates the tracked value - a rejected/failed send never applied.
        lock (_gate)
        {
            _lastKnownTimescale = timescale;
        }
    }

    /// <inheritdoc />
    public async Task<DemoState> QueryDemoStateAsync(CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.DemoStateEvents))
        {
            throw new NotSupportedException(
                $"Cannot query demo state: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.DemoStateEvents}' capability. Update the CSVG plugin.");
        }

        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.QueryDemoState", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);

        ulong commandId = NextCommandId();
        activity?.SetTag("csvg.command.id", commandId);
        TaskCompletionSource<DemoState> queryTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDemoStateQueries[commandId] = queryTcs;

        // The reply is a snapshot of engine state; a dead engine has none to send.
        using ProcessExitWatch processExitWatch = FailOnProcessExit(
            $"the DemoStateEvent reply to QueryDemoState (command id {commandId})",
            exitError => FailWaiter(queryTcs, exitError));

        try
        {
            await SendCommandAsync(
                () => new CsControlRequest { QueryDemoState = new QueryDemoStateCommand(), CommandId = commandId },
                cancellationToken).ConfigureAwait(false);

            try
            {
                return await queryTcs.Task.WaitAsync(DemoStateQueryTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                activity?.SetTag("csvg.timeout.occurred", true);
                activity?.SetStatus(ActivityStatusCode.Error, "Operation timed out");
                throw new TimeoutException(
                    $"Timed out after {DemoStateQueryTimeout.TotalSeconds:F1} seconds waiting for the " +
                    $"DemoStateEvent reply to QueryDemoState (command id {commandId}).");
            }
        }
        finally
        {
            _pendingDemoStateQueries.TryRemove(commandId, out _);
        }
    }

    /// <inheritdoc />
    public Task ScheduleCommandsAtTickAsync(int demoTick, IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consoleCommands);
        if (demoTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(demoTick), demoTick,
                "The scheduled demo tick must be zero or greater.");
        }

        if (consoleCommands.Count == 0)
        {
            throw new ArgumentException("At least one console command must be scheduled.", nameof(consoleCommands));
        }

        if (consoleCommands.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Console commands must be non-empty.", nameof(consoleCommands));
        }

        if (!Supports(CsvgCapabilities.TickScheduledCommands))
        {
            throw new NotSupportedException(
                $"Cannot schedule commands at a tick: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.TickScheduledCommands}' capability. Update the CSVG plugin.");
        }

        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.ScheduleCommandsAtTick", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.demo.tick", demoTick);
        activity?.SetTag("csvg.scheduled.commandCount", consoleCommands.Count);

        CsControlRequest BuildRequest(ulong commandId)
        {
            ScheduleCommandsAtTickCommand command = new() { DemoTick = demoTick };
            command.ConsoleCommands.AddRange(consoleCommands);
            return new CsControlRequest
            {
                ScheduleCommandsAtTick = command, CommandId = commandId
            };
        }

        // Acknowledged when the plugin can ack: the returned task then means "the schedule is
        // armed", which is what a caller about to resume playback past that tick needs. Without
        // "command-ack" it degrades to a plain send (in-order stream delivery still puts the
        // registration ahead of any later command).
        if (Supports(CsvgCapabilities.CommandAck))
        {
            return SendAcknowledgedCommandAsync(BuildRequest, $"ScheduleCommandsAtTick({demoTick})",
                cancellationToken);
        }

        return SendCommandAsync(() =>
        {
            CsControlRequest request = BuildRequest(0);
            request.ClearCommandId();
            return request;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ExecuteConsoleCommandsAsync(IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consoleCommands);
        if (consoleCommands.Count == 0)
        {
            throw new ArgumentException("At least one console command must be executed.", nameof(consoleCommands));
        }

        if (consoleCommands.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Console commands must be non-empty.", nameof(consoleCommands));
        }

        if (!Supports(CsvgCapabilities.ConsoleExec))
        {
            throw new NotSupportedException(
                $"Cannot execute console commands: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.ConsoleExec}' capability. Update the CSVG plugin.");
        }

        using Activity? activity =
            CsvgTracing.StartActivity("Cs2EngineSession.ExecuteConsoleCommands", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.console.commandCount", consoleCommands.Count);

        CsControlRequest BuildRequest(ulong commandId)
        {
            ExecuteConsoleCommandsCommand command = new();
            command.ConsoleCommands.AddRange(consoleCommands);
            return new CsControlRequest { ExecuteConsoleCommands = command, CommandId = commandId };
        }

        // Acknowledged when the plugin can ack: the returned task then means "queued for the engine
        // thread", which is as much as a blind console command can ever promise - the engine reports
        // nothing back. Without "command-ack" it degrades to a plain send.
        if (Supports(CsvgCapabilities.CommandAck))
        {
            return SendAcknowledgedCommandAsync(BuildRequest,
                $"ExecuteConsoleCommands({consoleCommands.Count})", cancellationToken);
        }

        return SendCommandAsync(() =>
        {
            CsControlRequest request = BuildRequest(0);
            request.ClearCommandId();
            return request;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearScheduledCommandsAsync(CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.TickScheduledCommands))
        {
            throw new NotSupportedException(
                $"Cannot clear scheduled commands: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.TickScheduledCommands}' capability. Update the CSVG plugin.");
        }

        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.ClearScheduledCommands", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);

        return SendCommandAsync(
            () => new CsControlRequest { ClearScheduledCommands = new ClearScheduledCommandsCommand() },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetGameEventSubscriptionAsync(bool enabled, IReadOnlyList<string>? eventNames = null,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.GameEventTelemetry))
        {
            throw new NotSupportedException(
                $"Cannot change the game-event subscription: the connected plugin does not advertise the " +
                $"'{CsvgCapabilities.GameEventTelemetry}' capability. The hook is not available on every " +
                "platform - see the CSVG plugin documentation.");
        }

        using Activity? activity =
            CsvgTracing.StartActivity("Cs2EngineSession.SetGameEventSubscription", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", SessionId);
        activity?.SetTag("csvg.gameEvents.enabled", enabled);
        activity?.SetTag("csvg.gameEvents.filterCount", eventNames?.Count ?? 0);

        CsControlRequest BuildRequest(ulong commandId)
        {
            SetGameEventSubscriptionCommand command = new() { Enabled = enabled };
            if (enabled && eventNames is { Count: > 0 })
            {
                command.EventNames.AddRange(eventNames);
            }

            return new CsControlRequest { SetGameEventSubscription = command, CommandId = commandId };
        }

        // Acknowledged when the plugin can ack. Two reasons this matters more here than for a
        // typical setter: enabling installs the game-event hook, which can genuinely fail at
        // runtime (the RTTI lookup depends on the CS2 build), and the returned task doubling as
        // "the subscription is live" is what makes the enable ordered against events that follow.
        if (Supports(CsvgCapabilities.CommandAck))
        {
            return SendAcknowledgedCommandAsync(BuildRequest,
                $"SetGameEventSubscription(enabled: {enabled})", cancellationToken);
        }

        return SendCommandAsync(() =>
        {
            CsControlRequest request = BuildRequest(0);
            request.ClearCommandId();
            return request;
        }, cancellationToken);
    }

    /// <summary>
    ///     Sends a command carrying a correlation id and waits for the plugin's
    ///     <c>CommandAckEvent</c> ("command-ack"), turning a failure ack into an exception.
    /// </summary>
    /// <remarks>
    ///     Requires <see cref="CsvgCapabilities.CommandAck" />; callers gate on it and fall back to
    ///     a plain <see cref="SendCommandAsync" /> when it is absent. Arrival-verified seeks do not
    ///     use this helper: they need the observed tick out of the ack and their own long
    ///     (arrival-sized) timeout, whereas this covers the ordinary "applied yet?" case.
    /// </remarks>
    /// <param name="commandFactory">Builds the request; receives the allocated command id.</param>
    /// <param name="description">Command name used in timeout/failure messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TimeoutException">No ack arrived within <see cref="CommandAckTimeout" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The session ended before the ack, or the plugin reported failure.
    /// </exception>
    private async Task SendAcknowledgedCommandAsync(Func<ulong, CsControlRequest> commandFactory, string description,
        CancellationToken cancellationToken)
    {
        ulong commandId = NextCommandId();
        TaskCompletionSource<CommandAckEvent> ackTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[commandId] = ackTcs;

        // Only the plugin inside the CS2 process can ack, so its death is terminal for this wait.
        using ProcessExitWatch processExitWatch = FailOnProcessExit(
            $"the plugin to acknowledge {description} (command id {commandId})",
            exitError => FailWaiter(ackTcs, exitError));

        try
        {
            await SendCommandAsync(() => commandFactory(commandId), cancellationToken).ConfigureAwait(false);

            CommandAckEvent ack;
            try
            {
                ack = await ackTcs.Task.WaitAsync(CommandAckTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {CommandAckTimeout.TotalSeconds:F1} seconds waiting for the plugin to " +
                    $"acknowledge {description} (command id {commandId}).");
            }

            if (!ack.Success)
            {
                throw new InvalidOperationException(
                    $"The plugin rejected {description}: " +
                    (ack.HasErrorMessage ? ack.ErrorMessage : "no error detail reported") + ".");
            }
        }
        finally
        {
            _pendingAcks.TryRemove(commandId, out _);
        }
    }

    /// <summary>
    ///     Fails fast with an <see cref="InvalidOperationException" /> when there is no active session
    ///     or the session is disconnected, instead of silently dropping the command and letting the
    ///     caller burn its full wait timeout.
    /// </summary>
    private async Task SendCommandAsync(Func<CsControlRequest> commandFactory, CancellationToken cancellationToken)
    {
        Func<CsControlRequest, Task>? sender;
        Func<string, Task>? rejectedHandler;
        lock (_gate)
        {
            sender = _sendAsync;
            rejectedHandler = CommandRejected;
        }

        if (sender is null)
        {
            _ = InvokeAsyncHandler(rejectedHandler, "No active session");
            throw new InvalidOperationException("Cannot send command: no active session");
        }

        CsControlRequest command = commandFactory();
        try
        {
            await sender(command).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The session rejected the command (disconnected / write failed). Surface it to
            // subscribers and rethrow so the caller fails immediately with a clear message.
            _ = InvokeAsyncHandler(rejectedHandler, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public Task LoadDemoAsync(string demoPath, CancellationToken cancellationToken = default) =>
        LoadDemoAsync(demoPath, interactiveDemoUi: false, cancellationToken);

    /// <inheritdoc />
    public async Task LoadDemoAsync(string demoPath, bool interactiveDemoUi,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2EngineSession.LoadDemo", ActivityKind.Client);
        activity?.SetTag("csvg.demo.path", demoPath);
        activity?.SetTag("csvg.demo.interactiveUi", interactiveDemoUi);
        activity?.SetTag("csvg.session.id", SessionId);

        await WaitForConditionAsyncEvent<DemoPlaybackStatusChange>(
            "Cs2EngineSession.LoadDemo.WaitForStatus",
            handler => DemoPlaybackStatusChanged += handler,
            handler => DemoPlaybackStatusChanged -= handler,
            (sessionId, status) =>
            {
                switch (status.NewStatus)
                {
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded:
                        activity?.SetTag("csvg.demo.loaded", true);
                        return (true, null);
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoFileNotFound:
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoFileUnplayable:
                        activity?.SetTag("csvg.demo.loaded", false);
                        activity?.SetTag("csvg.demo.error", status.ErrorMessage);
                        return (false,
                            new InvalidOperationException(
                                $"Demo failed to load: {status.ErrorMessage ?? "unknown error"}"));
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Unknown:
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading:
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Playing:
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused:
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Stopping:
                    case DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Stopped:
                    default:
                        return (null, null);
                }
            },
            async () =>
            {
                await SendCommandAsync(
                    () => new CsControlRequest
                    {
                        LoadDemo = new LoadDemoCommand
                        {
                            DemoPath = demoPath,
                            InteractiveDemoUi = interactiveDemoUi
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            },
            DefaultLoadDemoTimeout,
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlayTickRangeAsync(int startTick, int endTick, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity =
            CsvgTracing.StartActivity("Cs2EngineSession.PlayTickRange", ActivityKind.Client);
        activity?.SetTag("csvg.tick.start", startTick);
        activity?.SetTag("csvg.tick.end", endTick);
        activity?.SetTag("csvg.session.id", SessionId);

        DemoPlaybackStatusChange.Types.DemoPlaybackStatus? currentStatus = LastDemoPlaybackStatusChange?.NewStatus;
        if (currentStatus is null or < DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded
            or >= DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Stopping)
        {
            activity?.SetTag("csvg.error", "Invalid demo state");
            activity?.SetTag("csvg.demo.status", currentStatus?.ToString() ?? "Unknown");
            throw new InvalidOperationException(
                $"Cannot play tick range: demo must be in Loaded, Playing, or Paused state. Current: {currentStatus?.ToString() ?? "Unknown"}");
        }

        // Calculate timeout if not provided (see ComputeDefaultPlaybackTimeout for the math).
        TimeSpan effectiveTimeout = timeout ?? ComputeDefaultPlaybackTimeout(startTick, endTick, LastKnownTimescale);

        bool reachedEndTick = false;

        // Wait for end tick using sync event (TickUpdated is Action, not Func<Task>)
        await WaitForConditionSyncEvent<long>(
            "Cs2EngineSession.PlayTickRange.WaitForEndTick",
            handler => TickUpdated += handler,
            handler => TickUpdated -= handler,
            (sessionId, tick) =>
            {
                if (tick >= endTick)
                {
                    reachedEndTick = true;
                    activity?.SetTag("csvg.tick.reached", tick);
                    return (true, null); // Complete when we reach end tick
                }

                return (null, null);
            },
            async () =>
            {
                await SendCommandAsync(
                    () => new CsControlRequest
                    {
                        PlayDemoTickRange =
                            new PlayDemoTickRangeCommand { StartingTick = startTick, EndingTick = endTick }
                    }, cancellationToken).ConfigureAwait(false);
            },
            effectiveTimeout,
            cancellationToken
        ).ConfigureAwait(false);

        if (reachedEndTick)
        {
            // Fast path: the plugin emits the Paused status change microseconds after the
            // forced end-tick update that completed the wait above, so it has often been
            // processed already by the time this continuation runs.
            if (LastDemoPlaybackStatusChange?.NewStatus == DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused)
            {
                activity?.SetTag("csvg.playback.completed", true);
                activity?.SetTag("csvg.pause.already_paused", true);
                return; // Already paused, no need to wait
            }

            // Not processed yet, but it is already in flight: it is the last status change the
            // range produces (nothing later can complete this wait), and the read loop can
            // dispatch it at any point between the check above and the moment the event
            // subscription below takes effect. That check-then-subscribe gap was a real lost
            // wakeup: the auto-pause landed inside it roughly every other mock-mode E2E run
            // (fallback leg) and WaitForPause then timed out despite the plugin having sent
            // Paused. initialCheck re-probes the latched status once the subscription is
            // active, which closes the window: a status processed before the subscription is
            // seen by the probe, and one processed after it fires the handler.
            await WaitForConditionAsyncEvent<DemoPlaybackStatusChange>(
                "Cs2EngineSession.PlayTickRange.WaitForPause",
                handler => DemoPlaybackStatusChanged += handler,
                handler => DemoPlaybackStatusChanged -= handler,
                (sessionId, status) =>
                {
                    if (status.NewStatus == DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused)
                    {
                        activity?.SetTag("csvg.playback.completed", true);
                        return (true, null);
                    }

                    return (null, null);
                },
                () => Task.CompletedTask,
                effectiveTimeout,
                cancellationToken,
                initialCheck: () =>
                {
                    if (LastDemoPlaybackStatusChange?.NewStatus ==
                        DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused)
                    {
                        activity?.SetTag("csvg.playback.completed", true);
                        activity?.SetTag("csvg.pause.already_paused", true);
                        return (true, null);
                    }

                    return (null, null);
                }
            ).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Computes the default <see cref="PlayTickRangeAsync" /> timeout: the wall-clock
    ///     playback estimate at 64 ticks/sec divided by the effective demo timescale, plus a
    ///     30-second buffer. The historical math hardcoded 64 t/s and ignored timescale entirely
    ///     (plan §5.4 rationale: a half-speed session takes twice the wall clock, so slowed-down
    ///     playback falsely timed out while sped-up playback over-waited). The timescale used for
    ///     the estimate is <paramref name="lastKnownTimescale" /> defaulted to 1.0 when unknown and
    ///     clamped to at least 0.25, so an extreme slow-motion value cannot inflate the default
    ///     timeout unboundedly - callers wanting a longer budget pass an explicit timeout.
    /// </summary>
    internal static TimeSpan ComputeDefaultPlaybackTimeout(int startTick, int endTick, float? lastKnownTimescale)
    {
        double effectiveTimescale = Math.Max(lastKnownTimescale ?? 1.0, 0.25);
        return TimeSpan.FromSeconds((endTick - startTick) / 64.0 / effectiveTimescale + 30);
    }

    #endregion

    #region Wait Helpers

    /// <summary>
    ///     Latches the CS2 (or mock) process exit reported by the process manager. Only the first
    ///     exit is recorded: this session object covers exactly one CS2 run, and once that process is
    ///     gone nothing it could have answered will ever arrive.
    /// </summary>
    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) =>
        _processExitedTcs.TrySetResult(e.Result);

    /// <summary>
    ///     The failure message every wait abandoned by a process exit carries. It names the fact
    ///     (CS2 is gone, with its exit code) instead of the generic timeout that used to be reported
    ///     minutes later, and points at the process output where the crash itself was printed.
    /// </summary>
    private static string BuildProcessExitedMessage(ProcessExitResult result, string operation) =>
        $"CS2 process exited (exit code {result.ExitCode}, reason: {result.Reason}) while waiting for " +
        $"{operation}. The wait can never complete, so it failed immediately instead of running out its " +
        "timeout; see the CS2 process output ([CS2] / [CS2-ERR] log lines) for the cause.";

    /// <summary>
    ///     Arms the process-exit fast-fail for a single wait: <paramref name="fail" /> is invoked with
    ///     an <see cref="InvalidOperationException" /> the moment the CS2 process is known to have
    ///     exited - synchronously, before this method returns, when it is already dead. Callers keep
    ///     the returned registration alive for exactly as long as their wait and dispose it
    ///     afterwards, which is what stops a finished wait from leaving a continuation behind on the
    ///     session-lifetime exit latch (a capture run performs several waits per clip).
    /// </summary>
    /// <param name="operation">
    ///     What the caller is waiting for, as it should read in the failure message (e.g.
    ///     <c>'Cs2EngineSession.PlayTickRange.WaitForEndTick'</c>).
    /// </param>
    /// <param name="fail">
    ///     Fails the caller's wait. Must be safe to call when the wait has already completed - it is
    ///     always a <c>TrySet*</c> on the wait's own completion source.
    /// </param>
    private ProcessExitWatch FailOnProcessExit(string operation, Action<Exception> fail)
    {
        ProcessExitWatch watch = new();
        _ = WatchProcessExitAsync(operation, fail, watch.Token);
        return watch;
    }

    /// <summary>
    ///     The body behind <see cref="FailOnProcessExit" />. Runs detached from the wait it guards, so
    ///     it swallows its own failures: a watch that threw would surface as an unobserved task
    ///     exception and tell the caller nothing about the operation it was guarding.
    /// </summary>
    private async Task WatchProcessExitAsync(string operation, Action<Exception> fail,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessExitResult result = await _processExitedTcs.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            fail(new InvalidOperationException(BuildProcessExitedMessage(result, operation)));
        }
        catch (OperationCanceledException)
        {
            // The guarded wait finished first and disposed the registration - nothing to report.
        }
        catch (Exception ex)
        {
            LogProcessExitWatchFailed(ex);
        }
    }

    /// <summary>
    ///     The registration handed out by <see cref="FailOnProcessExit" />. Disposal cancels the
    ///     watch rather than merely dropping it: the watch is parked on a task that only completes
    ///     when CS2 dies, so without the cancellation every completed wait would leave a continuation
    ///     chained to it for the rest of the session.
    /// </summary>
    private sealed class ProcessExitWatch : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Cancelled when the guarded wait ends, releasing the watch.</summary>
        internal CancellationToken Token => _cts.Token;

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    /// <summary>
    ///     Internal helper for waiting on async event-based conditions (Func&lt;..., Task&gt; events).
    ///     <paramref name="initialCheck" /> (optional) probes already-latched state right after the
    ///     subscription takes effect - callers waiting on a condition whose triggering event may
    ///     already be in flight pass it to avoid the check-then-subscribe lost-wakeup race (see
    ///     <see cref="PlayTickRangeAsync" />'s pause wait).
    /// </summary>
    private Task WaitForConditionAsyncEvent<TEventArgs>(
        string activityName,
        Action<Func<string, TEventArgs, Task>> subscribeAction,
        Action<Func<string, TEventArgs, Task>> unsubscribeAction,
        Func<string, TEventArgs, (bool? success, Exception? error)> conditionCheck,
        Func<Task> triggerAction,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        Func<(bool? success, Exception? error)>? initialCheck = null) =>
        WaitForConditionCoreAsync<TEventArgs, Func<string, TEventArgs, Task>>(
            activityName,
            onEvent => (sessionId, args) =>
            {
                onEvent(sessionId, args);
                return Task.CompletedTask;
            },
            subscribeAction,
            unsubscribeAction,
            conditionCheck,
            triggerAction,
            timeout,
            cancellationToken,
            logEventDiagnostics: false,
            initialCheck);

    /// <summary>
    ///     Internal helper for waiting on sync event-based conditions (Action&lt;...&gt; events like TickUpdated).
    /// </summary>
    private Task WaitForConditionSyncEvent<TEventArgs>(
        string activityName,
        Action<Action<string, TEventArgs>> subscribeAction,
        Action<Action<string, TEventArgs>> unsubscribeAction,
        Func<string, TEventArgs, (bool? success, Exception? error)> conditionCheck,
        Func<Task> triggerAction,
        TimeSpan? timeout,
        CancellationToken cancellationToken) =>
        WaitForConditionCoreAsync<TEventArgs, Action<string, TEventArgs>>(
            activityName,
            onEvent => new Action<string, TEventArgs>(onEvent),
            subscribeAction,
            unsubscribeAction,
            conditionCheck,
            triggerAction,
            timeout,
            cancellationToken,
            logEventDiagnostics: true);

    /// <summary>
    ///     Shared subscribe/trigger/await-with-timeout scaffolding behind
    ///     <see cref="WaitForConditionAsyncEvent{TEventArgs}" /> and
    ///     <see cref="WaitForConditionSyncEvent{TEventArgs}" />. The event delegate shape is the only
    ///     variation between the two, surfaced via <typeparamref name="THandler" /> and
    ///     <paramref name="wrapHandler" />. <paramref name="logEventDiagnostics" /> preserves the
    ///     debug logging historically emitted only for sync (tick-style) events.
    ///     <paramref name="initialCheck" /> (optional) is evaluated once, immediately after the
    ///     subscription is installed: an event dispatched before the subscription took effect has
    ///     already latched the state it observes, and one dispatched after it reaches the handler,
    ///     so a condition that was signalled in that gap cannot be lost.
    /// </summary>
    private async Task WaitForConditionCoreAsync<TEventArgs, THandler>(
        string activityName,
        Func<Action<string, TEventArgs>, THandler> wrapHandler,
        Action<THandler> subscribeAction,
        Action<THandler> unsubscribeAction,
        Func<string, TEventArgs, (bool? success, Exception? error)> conditionCheck,
        Func<Task> triggerAction,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        bool logEventDiagnostics,
        Func<(bool? success, Exception? error)>? initialCheck = null)
    {
        using Activity? activity = CsvgTracing.StartActivity(activityName);
        activity?.SetTag("csvg.session.id", SessionId);

        if (timeout.HasValue)
        {
            activity?.SetTag("csvg.timeout.seconds", timeout.Value.TotalSeconds);
        }

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Every condition this scaffolding can wait for is produced by the CS2 process (a tick
        // update, a playback status change), so a CS2 that dies under the wait makes it unsatisfiable.
        // Armed before the subscription and the trigger, so an already-dead process is reported by
        // this method rather than as a downstream send failure.
        using ProcessExitWatch processExitWatch =
            FailOnProcessExit($"'{activityName}'", exitError => FailWaiter(tcs, exitError));

        CancellationTokenSource? timeoutCts = null;
        CancellationToken effectiveCancellationToken = cancellationToken;

        if (timeout.HasValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout.Value);
            effectiveCancellationToken = timeoutCts.Token;
        }

        void OnEvent(string sessionId, TEventArgs args)
        {
            using Activity? eventActivity = CsvgTracing.StartActivity($"{activityName}.EventHandler");
            eventActivity?.SetTag("csvg.session.id", sessionId);

            if (logEventDiagnostics)
            {
                LogSyncEventHandlerInvoked(activityName, args?.ToString() ?? "null");
            }

            (bool? success, Exception? error) = conditionCheck(sessionId, args);

            if (logEventDiagnostics)
            {
                LogSyncEventConditionResult(activityName, success, error?.Message);
            }

            switch (success)
            {
                case true:
                    eventActivity?.SetTag("csvg.condition.met", true);
                    tcs.TrySetResult(true);
                    break;
                case false when error != null:
                    eventActivity?.SetTag("csvg.condition.met", false);
                    eventActivity?.SetTag("csvg.error", error.Message);
                    tcs.TrySetException(error);
                    break;
            }
        }

        THandler handler = wrapHandler(OnEvent);

        try
        {
            if (logEventDiagnostics)
            {
                LogSyncEventSubscribing(activityName);
            }

            subscribeAction(handler);

            // The subscription is live from here on; a state probe can no longer race the
            // event dispatch (whichever side ran first, one of the two paths sees it).
            if (initialCheck != null)
            {
                (bool? success, Exception? error) = initialCheck();
                switch (success)
                {
                    case true:
                        activity?.SetTag("csvg.condition.met", true);
                        activity?.SetTag("csvg.condition.met_on_initial_check", true);
                        tcs.TrySetResult(true);
                        break;
                    case false when error != null:
                        activity?.SetTag("csvg.condition.met", false);
                        activity?.SetTag("csvg.error", error.Message);
                        tcs.TrySetException(error);
                        break;
                }
            }

            if (logEventDiagnostics)
            {
                LogSyncEventTriggerStart(activityName);
            }

            try
            {
                await triggerAction().ConfigureAwait(false);
            }
            catch (Exception) when (tcs.Task.IsFaulted)
            {
                // CS2 is already gone: the plugin stream died with it, so a send failure here is a
                // symptom. Surface the root cause the exit watch recorded instead of "no active
                // session", which says nothing about why the session went away.
                await tcs.Task.ConfigureAwait(false);
                throw;
            }

            if (logEventDiagnostics)
            {
                LogSyncEventTriggerComplete(activityName);
            }

            await using (effectiveCancellationToken.Register(() => tcs.TrySetCanceled(effectiveCancellationToken)))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.HasValue && timeoutCts?.IsCancellationRequested == true &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("csvg.timeout.occurred", true);
            activity?.SetStatus(ActivityStatusCode.Error, "Operation timed out");
            throw new TimeoutException(
                $"Operation '{activityName}' timed out after {timeout.Value.TotalSeconds:F1} seconds");
        }
        finally
        {
            unsubscribeAction(handler);
            timeoutCts?.Dispose();
        }
    }

    #endregion

    #region Shutdown / IAsyncDisposable

    /// <summary>
    ///     Tears down the session's engine-facing state: clears the wire event handlers and command
    ///     delegates, resets the latched truth, and fails every pending command waiter with
    ///     <paramref name="waiterFailureReason" />.
    /// </summary>
    internal void Shutdown(string waiterFailureReason)
    {
        // The process manager outlives this session (it is a singleton shared by every run), so the
        // session-lifetime exit subscription has to be released or a Start/Stop cycle would leave a
        // handler per dead session behind on it.
        _processManager.Exited -= OnProcessExited;

        lock (_gate)
        {
            TickUpdated = null;
            DemoPlaybackStatusChanged = null;
            DemoStateChanged = null;
            CommandRejected = null;
            ProcessStatusChanged = null;

            _sendAsync = null;
            _requestSessionStop = null;
            SessionId = null;
            _pluginCapabilities = EmptyCapabilities;
            _lastDemoState = null;
            _lastKnownTimescale = null;
            LastTickIsPaused = null;
        }

        // Nothing can answer outstanding commands anymore - release their waiters.
        FailPendingCommandWaiters(waiterFailureReason);

        // A shut-down session is terminal: release anyone still waiting for it to end.
        _endedTcs.TrySetResult(true);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Shutdown("The session was disposed before the command completed.");
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Session started: {SessionId}")]
    partial void LogSessionStarted(string sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Session ended: {SessionId} lastTick={LastTick}")]
    partial void LogSessionEnded(string sessionId, long? lastTick);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Command sender available for session {SessionId}")]
    partial void LogCommandSenderAvailable(string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Process status changed: {SessionId} {Status}")]
    partial void LogProcessStatusChanged(string sessionId, Cs2ProcessStatus.Types.ProcessStatus status);

    [LoggerMessage(LogLevel.Debug,
        "Ignoring end of stale session {EndedSessionId}; current session is {CurrentSessionId}")]
    partial void LogStaleSessionEndIgnored(string endedSessionId, string? currentSessionId);

    [LoggerMessage(LogLevel.Warning, "Event subscriber threw an exception; continuing with remaining subscribers")]
    partial void LogEventHandlerError(Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Tick received: {CurrentTick}")]
    partial void LogTickReceived(long currentTick);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "TickUpdated handler registered: {HasHandler}")]
    partial void LogTickHandlerStatus(bool hasHandler);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "SyncEvent [{ActivityName}] subscribing handler")]
    partial void LogSyncEventSubscribing(string activityName);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "SyncEvent [{ActivityName}] trigger starting")]
    partial void LogSyncEventTriggerStart(string activityName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "SyncEvent [{ActivityName}] trigger complete")]
    partial void LogSyncEventTriggerComplete(string activityName);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug,
        Message = "SyncEvent [{ActivityName}] handler invoked with: {Args}")]
    partial void LogSyncEventHandlerInvoked(string activityName, string args);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "SyncEvent [{ActivityName}] condition result: success={Success}, error={Error}")]
    partial void LogSyncEventConditionResult(string activityName, bool? success, string? error);

    // v1.1 capability release
    [LoggerMessage(EventId = 13, Level = LogLevel.Debug,
        Message = "Dropping CommandAck for unknown command id {CommandId} (session {SessionId})")]
    partial void LogUnknownCommandAck(string sessionId, ulong commandId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
        Message = "Plugin capabilities latched for session {SessionId}: [{Capabilities}]")]
    partial void LogPluginCapabilitiesLatched(string sessionId, string capabilities);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message =
            "Demo state received (session {SessionId}): playing={IsPlayingDemo} paused={IsPaused} tick={DemoTick} origin={Origin}")]
    partial void LogDemoStateReceived(string sessionId, bool? isPlayingDemo, bool? isPaused, int? demoTick,
        DemoStateOrigin origin);

    [LoggerMessage(EventId = 16, Level = LogLevel.Trace,
        Message = "Game event received (session {SessionId}): {EventName}")]
    partial void LogGameEventReceived(string sessionId, string eventName);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning,
        Message =
            "Game-event telemetry is enabled in configuration but the connected plugin does not advertise the '" +
            CsvgCapabilities.GameEventTelemetry + "' capability - continuing without it")]
    partial void LogGameEventTelemetryUnsupported();

    [LoggerMessage(EventId = 18, Level = LogLevel.Information,
        Message = "Game-event telemetry enabled ({FilterCount} event name filter(s); 0 = all events)")]
    partial void LogGameEventTelemetryEnabled(int filterCount);

    [LoggerMessage(EventId = 19, Level = LogLevel.Warning,
        Message = "Could not enable game-event telemetry - continuing without it")]
    partial void LogGameEventTelemetryEnableFailed(Exception exception);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "The process-exit watch guarding a wait failed; that wait falls back to its timeout")]
    partial void LogProcessExitWatchFailed(Exception ex);

    [LoggerMessage(LogLevel.Debug, "Probing gRPC endpoint on localhost:{Port} before launch")]
    partial void LogProbingGrpcEndpoint(int port);

    [LoggerMessage(LogLevel.Error,
        "gRPC endpoint is not listening on localhost:{Port}; failing session start before launching CS2")]
    partial void LogGrpcEndpointNotListening(int port);

    [LoggerMessage(LogLevel.Debug, "Requesting graceful session stop before killing CS2")]
    partial void LogRequestingGracefulSessionStop();

    [LoggerMessage(LogLevel.Debug, "Session stopped gracefully")]
    partial void LogSessionStoppedGracefully();

    [LoggerMessage(LogLevel.Debug, "Session did not stop gracefully within timeout, proceeding to kill CS2")]
    partial void LogSessionStopTimeout();

    [LoggerMessage(LogLevel.Warning, "Graceful session stop request failed; continuing with CS2 shutdown")]
    partial void LogSessionStopRequestError(Exception ex);

    // Plugin/game version compatibility
    [LoggerMessage(EventId = 400, Level = LogLevel.Debug,
        Message = "Plugin compatibility check passed: {Detail} (HL2SDK commit: {Hl2SdkCommit})")]
    partial void LogCompatibilityOk(string detail, string? hl2SdkCommit);

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "{Message}")]
    partial void LogCompatibilityWarning(string message);

    [LoggerMessage(EventId = 402, Level = LogLevel.Error, Message = "{Message}")]
    partial void LogCompatibilityKnownBroken(string message);

    [LoggerMessage(EventId = 403, Level = LogLevel.Warning,
        Message = "Proceeding despite known-incompatible plugin/game versions (ForceIncompatiblePlugin/--force is set): {Message}")]
    partial void LogCompatibilityForced(string message);

    [LoggerMessage(EventId = 404, Level = LogLevel.Warning,
        Message = "Plugin compatibility check could not run; proceeding without it")]
    partial void LogCompatibilityCheckFailed(Exception ex);

    [LoggerMessage(EventId = 405, Level = LogLevel.Debug,
        Message = "Mock mode without a simulated game version; skipping plugin compatibility check (plugin {PluginVersion})")]
    partial void LogCompatibilityMockNoGameVersion(string? pluginVersion);

    #endregion
}
