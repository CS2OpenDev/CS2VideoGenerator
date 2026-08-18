#region

using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;

#endregion

namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     Engine session for a single CS2 run: owns the plugin command channel, the latched engine
///     truth (last tick, demo state, capabilities), plugin/game compatibility evaluation, and all
///     demo playback control.
/// </summary>
public interface ICs2EngineSession : IEngineConsole, IAsyncDisposable
{
    /// <summary>
    ///     The active session identifier, or null if disconnected.
    /// </summary>
    string? SessionId { get; }

    /// <summary>
    ///     Latest tick reported by the session, or null if none received.
    /// </summary>
    long? LastTick { get; }

    /// <summary>
    ///     Whether demo playback was paused as of the latest tick update, or null when unknown.
    ///     Best-effort: updated on the read loop alongside <see cref="LastTick" /> whenever the
    ///     plugin serializes <c>TickUpdateEvent.is_paused</c>; v1.0 plugins never send the field, so
    ///     against them this stays null. Engine truth only when the plugin advertises
    ///     <see cref="CsvgCapabilities.EnginePauseDetection" /> ("engine-pause-detection").
    /// </summary>
    bool? LastTickIsPaused { get; }

    /// <summary>
    ///     Latest CS2 process status received from the plugin.
    /// </summary>
    Cs2ProcessStatus? LastProcessStatus { get; }

    /// <summary>
    ///     Latest demo playback status change received.
    /// </summary>
    DemoPlaybackStatusChange? LastDemoPlaybackStatusChange { get; }

    /// <summary>
    ///     Latest engine-truth demo state snapshot received, or null if none has arrived. Only
    ///     plugins advertising <see cref="CsvgCapabilities.DemoStateEvents" /> ("demo-state-events")
    ///     emit demo state; against older plugins this stays null.
    /// </summary>
    DemoState? LastDemoState { get; }

    /// <summary>
    ///     The demo timescale this session last learned about, or null when unknown: updated from
    ///     successful <see cref="SetDemoTimescaleAsync" /> sends and from
    ///     <c>DemoStateEvent.demo_timescale</c> reports. Host-set truth only - there is no engine
    ///     readback in this protocol version.
    /// </summary>
    float? LastKnownTimescale { get; }

    /// <summary>
    ///     Capability tokens advertised by the connected plugin build (see
    ///     <see cref="CsvgCapabilities" />). Empty until the plugin's process-status handshake
    ///     arrives, latched anew from every process status (last write wins), and reset to empty
    ///     when the session ends. An empty set means "assume the v1.0 baseline command set".
    /// </summary>
    IReadOnlySet<string> PluginCapabilities { get; }

    /// <summary>
    ///     Raised when the plugin reports a tick update, carrying the session id and observed tick.
    /// </summary>
    /// <remarks>
    ///     Synchronous <see cref="Action" /> rather than async, for throughput. Handlers must be
    ///     fast and non-blocking or they stall the event processing pipeline.
    /// </remarks>
    event Action<string, long>? TickUpdated;

    /// <summary>
    ///     Raised when demo playback status changes.
    /// </summary>
    event Func<string, DemoPlaybackStatusChange, Task>? DemoPlaybackStatusChanged;

    /// <summary>
    ///     Raised when the plugin reports an engine-truth demo state snapshot (change-driven, in
    ///     reply to <see cref="QueryDemoStateAsync" />, or as a command completion payload).
    ///     Requires a plugin advertising <see cref="CsvgCapabilities.DemoStateEvents" />
    ///     ("demo-state-events"); never raised against older plugins. Subscriber exceptions are
    ///     isolated per-subscriber (logged and swallowed), like the other async events.
    /// </summary>
    event Func<string, DemoState, Task>? DemoStateChanged;

    /// <summary>
    ///     Raised for each client-side game event the plugin forwards while a subscription started
    ///     by <see cref="SetGameEventSubscriptionAsync" /> is active. Requires
    ///     <see cref="CsvgCapabilities.GameEventTelemetry" />; never raised otherwise (telemetry is
    ///     off until explicitly enabled).
    /// </summary>
    event Func<string, GameEventTelemetry, Task>? GameEventReceived;

    /// <summary>
    ///     Raised when CS2 process status updates are reported.
    /// </summary>
    event Func<string, Cs2ProcessStatus, Task>? ProcessStatusChanged;

    /// <summary>
    ///     Raised when the plugin establishes this engine session, carrying the session id.
    /// </summary>
    event Func<string, Task>? Started;

    /// <summary>
    ///     Raised when the engine session ends (disconnect or shutdown), carrying the session id,
    ///     the last observed tick, and the last time the plugin was seen.
    /// </summary>
    event Func<string, long?, DateTimeOffset, Task>? Ended;

    /// <summary>
    ///     Load a demo file and wait until it is loaded and paused at tick 0.
    /// </summary>
    Task LoadDemoAsync(string demoPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Load a demo file and wait until it is loaded and paused at tick 0, optionally keeping
    ///     the interactive demo UI enabled.
    /// </summary>
    Task LoadDemoAsync(string demoPath, bool interactiveDemoUi, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Close the current demo.
    /// </summary>
    Task CloseDemoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Pause demo playback.
    /// </summary>
    Task PauseDemoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resume demo playback.
    /// </summary>
    Task ResumeDemoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Seek to a specific tick.
    /// </summary>
    Task SetDemoTickAsync(int tick, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Seek to a specific tick, optionally forcing the post-seek play/pause state
    ///     (<see cref="CsvgCapabilities.SeekAck" /> plugins honor <paramref name="pauseAfterSeek" />;
    ///     older plugins ignore it) and optionally waiting for the plugin's acknowledgement
    ///     (<see cref="CsvgCapabilities.CommandAck" /> required for
    ///     <paramref name="waitForCompletion" />; without the capability an acknowledged seek throws
    ///     <see cref="NotSupportedException" /> while a non-waiting call behaves like the legacy
    ///     fire-and-forget <see cref="SetDemoTickAsync(int, CancellationToken)" /> overload).
    /// </summary>
    Task<SeekResult> SetDemoTickAsync(int demoTick, bool? pauseAfterSeek, bool waitForCompletion,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets the spectator target to a specific player by name.
    /// </summary>
    Task SetSpectatorTargetAsync(string playerName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets demo playback options (xray, hud, etc.).
    /// </summary>
    Task SetDemoOptionsAsync(Cs2ClipOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets how often tick updates are sent from the plugin.
    ///     Default is 500 (every 500 ticks, ~7.8 seconds at 64 ticks/sec).
    ///     Set to 0 for every tick (original behavior).
    ///     Tick updates are always sent at key moments (pause, resume, seek) regardless of frequency.
    /// </summary>
    Task SetTickUpdateFrequencyAsync(int frequency, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets the demo playback timescale (<c>demo_timescale</c>; 1.0 = normal speed). Requires a
    ///     plugin advertising <see cref="CsvgCapabilities.TimescaleSet" /> ("timescale-set") - there
    ///     is no degraded speed-set path on older plugins. On a successful send,
    ///     <see cref="LastKnownTimescale" /> is updated (host-set truth; no engine readback exists
    ///     in this protocol version).
    /// </summary>
    Task SetDemoTimescaleAsync(float timescale, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Requests an engine-truth demo state snapshot and waits for the matching
    ///     <c>DemoStateEvent</c> reply, correlated by command id via <c>in_reply_to_command_id</c>
    ///     (deliberately not by the <c>CommandAckEvent</c> - the query's answer is the state
    ///     snapshot itself). Requires a plugin advertising <see cref="CsvgCapabilities.DemoStateEvents" />
    ///     ("demo-state-events"). The reply also updates <see cref="LastDemoState" /> and raises
    ///     <see cref="DemoStateChanged" /> like any other demo state event.
    /// </summary>
    Task<DemoState> QueryDemoStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Turns game-event telemetry on or off ("game-event-telemetry", off by default). While
    ///     enabled the plugin forwards matching client-side game events as
    ///     <see cref="GameEventReceived" />. Requires
    ///     <see cref="CsvgCapabilities.GameEventTelemetry" /> - advertised only by plugin builds
    ///     with a game-event hook available (the mock server and the Windows plugin; the Linux
    ///     plugin withholds it), so this throws rather than silently reporting nothing.
    /// </summary>
    /// <param name="enabled">Whether to forward game events.</param>
    /// <param name="eventNames">
    ///     Event names to forward; null or empty means all events. Each call replaces the previous
    ///     filter rather than adding to it. Ignored when <paramref name="enabled" /> is false.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetGameEventSubscriptionAsync(bool enabled, IReadOnlyList<string>? eventNames = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Registers console commands to execute on CS2's engine thread when the observed demo tick
    ///     first crosses <paramref name="demoTick" /> ("tick-scheduled-commands"). Requires a plugin
    ///     advertising <see cref="CsvgCapabilities.TickScheduledCommands" /> - there is no legacy
    ///     equivalent, so this throws rather than degrading.
    /// </summary>
    Task ScheduleCommandsAtTickAsync(int demoTick, IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Drops every not-yet-fired schedule registered by
    ///     <see cref="ScheduleCommandsAtTickAsync" />. Requires
    ///     <see cref="CsvgCapabilities.TickScheduledCommands" />.
    /// </summary>
    Task ClearScheduledCommandsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Plays a demo tick range and waits until playback completes and pauses. Pure playback -
    ///     no capture coordination. Throws on invalid demo state, timeout, or send failure, and -
    ///     because a dead engine can never deliver the end tick - immediately with an
    ///     <see cref="InvalidOperationException" /> naming the exit code when the CS2 process dies
    ///     while the wait is in flight, instead of running the timeout out.
    /// </summary>
    Task PlayTickRangeAsync(int startTick, int endTick, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
