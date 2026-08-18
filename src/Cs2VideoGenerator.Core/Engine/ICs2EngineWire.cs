#region

using Cs2VideoGenerator.Core.Proto;

#endregion

namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     The ingestion side of an engine session: everything the gRPC layer pushes in from the
///     plugin's event stream. Internal on purpose - it is a transport seam, not part of the public
///     object graph - and implemented by <see cref="Cs2EngineSession" />.
/// </summary>
/// <remarks>
///     The gRPC layer reaches the current implementation through
///     <see cref="ICs2EngineHost.Current" /> (last writer wins), so a plugin reconnecting is routed
///     to whichever session the host most recently handed out. A host that returns a session type of
///     its own simply receives no wire events.
/// </remarks>
internal interface ICs2EngineWire
{
    /// <summary>
    ///     A new plugin session took over. Resets the per-session engine truth and dispatches the
    ///     session's <c>Started</c> event; the returned task completes once subscribers have run.
    /// </summary>
    Task OnSessionStartedAsync(string sessionId);

    /// <summary>
    ///     The plugin session ended (disconnect or shutdown). Tears the session down, releases
    ///     session-ended waiters and dispatches the session's <c>Ended</c> event; the returned task
    ///     completes once subscribers have run. An end reported for a session that is no longer
    ///     current is ignored.
    /// </summary>
    Task OnSessionEndedAsync(string sessionId, long? lastTick, DateTimeOffset lastSeenUtc);

    /// <summary>
    ///     Installs the delegate commands are written to, plus the optional graceful-stop request for
    ///     the underlying gRPC session.
    /// </summary>
    void OnCommandSenderAvailable(string sessionId, Func<CsControlRequest, Task> sendAsync,
        Action? requestStop = null);

    /// <summary>
    ///     A tick update. <paramref name="isPaused" /> carries the optional
    ///     <c>TickUpdateEvent.is_paused</c> field, null when the plugin build predates it.
    /// </summary>
    void OnTickUpdated(string sessionId, long currentTick, bool? isPaused = null);

    Task OnDemoPlaybackStatusChangedAsync(string sessionId, DemoPlaybackStatusChange statusChange);

    /// <summary>
    ///     A CS2 process status update, including the version handshake.
    /// </summary>
    Task OnProcessStatusChangedAsync(string sessionId, Cs2ProcessStatus status);

    /// <summary>
    ///     An engine-truth demo state snapshot, which may also be the reply to a pending demo state
    ///     query.
    /// </summary>
    Task OnDemoStateAsync(string sessionId, DemoStateEvent stateEvent);

    /// <summary>
    ///     A forwarded client-side CS2 game event ("game-event-telemetry").
    /// </summary>
    Task OnGameEventTelemetryAsync(string sessionId, GameEventTelemetryEvent telemetryEvent);

    /// <summary>
    ///     An acknowledgement for a command that carried a command id. Routed synchronously: its only
    ///     job is to complete the pending waiter registered by the sending method.
    /// </summary>
    void OnCommandAck(string sessionId, CommandAckEvent ack);
}
