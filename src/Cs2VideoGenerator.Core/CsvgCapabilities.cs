namespace Cs2VideoGenerator.Core;

/// <summary>
///     Well-known capability tokens a CSVG plugin build can advertise via
///     <c>Cs2ProcessStatus.capabilities</c>. Use these constants instead of raw
///     strings when calling <see cref="Engine.IEngineConsole.Supports" /> or inspecting
///     <see cref="Engine.ICs2EngineSession.PluginCapabilities" />. Older plugins omit the field entirely, which
///     surfaces as an empty capability set: the orchestrator then assumes the baseline command
///     set.
/// </summary>
/// <remarks>
///     What "assume the baseline" means depends on what is missing, and the rule has two halves.
///     Latched telemetry and work the orchestrator started on its own initiative degrade: a reader
///     with nothing to report stays null or empty
///     (<see cref="Engine.ICs2EngineSession.LastDemoState" />), and optional work driven from
///     configuration rather than from a call is skipped with a warning (the startup game-event
///     subscription). An operation a caller explicitly asked for that has no baseline equivalent throws
///     <see cref="NotSupportedException" /> naming the capability, because the alternative is
///     returning as though it had run. The plugin cannot do it, and pretending otherwise puts the
///     failure somewhere far away from its cause. Where a legacy path does exist the call takes it
///     instead of throwing: an unacknowledged <c>SetDemoTick</c> without "command-ack", a plain send
///     without an ack wait.
/// </remarks>
public static class CsvgCapabilities
{
    /// <summary>
    ///     <c>DemoStateEvent</c> emission and <c>QueryDemoStateCommand</c> support
    ///     (<see cref="Engine.ICs2EngineSession.DemoStateChanged" /> /
    ///     <see cref="Engine.ICs2EngineSession.QueryDemoStateAsync" />).
    /// </summary>
    public const string DemoStateEvents = "demo-state-events";

    /// <summary>
    ///     <c>CommandAckEvent</c> replies for commands that carry a <c>command_id</c>
    ///     (the acknowledged
    ///     <see cref="Engine.ICs2EngineSession.SetDemoTickAsync(int, bool?, bool, CancellationToken)" /> path).
    /// </summary>
    public const string CommandAck = "command-ack";

    /// <summary>
    ///     <c>SetDemoTickCommand.pause_after_seek</c> support plus arrival-detected (rather than
    ///     target-echoed) seek acknowledgements.
    /// </summary>
    public const string SeekAck = "seek-ack";

    /// <summary>
    ///     <c>SetDemoTimescaleCommand</c> support (<see cref="Engine.ICs2EngineSession.SetDemoTimescaleAsync" />).
    /// </summary>
    public const string TimescaleSet = "timescale-set";

    /// <summary>
    ///     <c>DemoStateEvent.demo_file_path</c> population (currently-loaded demo identity).
    /// </summary>
    public const string DemoIdentity = "demo-identity";

    /// <summary>
    ///     <c>TickUpdateEvent.is_paused</c> / <c>DemoStateEvent.is_paused</c> reflect engine truth
    ///     (<c>IDemoFile::IsDemoPaused</c>) rather than command-echo state.
    /// </summary>
    public const string EnginePauseDetection = "engine-pause-detection";

    /// <summary>
    ///     <c>DEMO_FILE_NOT_FOUND</c> / <c>DEMO_FILE_UNPLAYABLE</c> playback statuses are actually
    ///     emitted on load failure (instead of the legacy behavior of burning the full load timeout).
    /// </summary>
    public const string LoadFailureDetection = "load-failure-detection";

    /// <summary>
    ///     <c>SetSpectatorTargetCommand.steam_id64</c> is honored (experimental
    ///     <c>spec_lock_to_accountid</c> targeting with name fallback).
    /// </summary>
    public const string SpectateBySteamId = "spectate-by-steamid";

    /// <summary>
    ///     <c>LoadDemoCommand.interactive_demo_ui</c> is honored (the plugin leaves the in-game demo
    ///     UI available to the user instead of hiding it).
    /// </summary>
    public const string UserDemoUi = "user-demo-ui";

    /// <summary>
    ///     <c>DemoStateEvent.spectated_player_name</c> population (future; no engine read exists yet).
    /// </summary>
    public const string SpectatorReport = "spectator-report";

    /// <summary>
    ///     <c>ScheduleCommandsAtTickCommand</c> / <c>ClearScheduledCommandsCommand</c> support: console
    ///     commands registered against a demo tick and executed on the engine thread when the observed
    ///     tick crosses it (<see cref="Engine.ICs2EngineSession.ScheduleCommandsAtTickAsync" /> /
    ///     <see cref="Engine.ICs2EngineSession.ClearScheduledCommandsAsync" />).
    /// </summary>
    public const string TickScheduledCommands = "tick-scheduled-commands";

    /// <summary>
    ///     <c>ExecuteConsoleCommandsCommand</c> support: console commands queued for CS2's engine
    ///     thread and executed at its next frame, with no tick condition
    ///     (<see cref="Engine.IEngineConsole.ExecuteConsoleCommandsAsync" />).
    /// </summary>
    public const string ConsoleExec = "console-exec";

    /// <summary>
    ///     <c>SetGameEventSubscriptionCommand</c> support and <c>GameEventTelemetryEvent</c> emission
    ///     (<see cref="Engine.ICs2EngineSession.SetGameEventSubscriptionAsync" /> /
    ///     <see cref="Engine.ICs2EngineSession.GameEventReceived" />). Advertised only by plugin builds that have a
    ///     game-event hook available: currently the mock server and the Windows plugin. The Linux
    ///     plugin withholds it.
    /// </summary>
    public const string GameEventTelemetry = "game-event-telemetry";

    /// <summary>
    ///     The plugin hooks the swapchain present call and can stream raw backbuffer frames over the
    ///     CSVG frame pipe while a <c>startmovie</c> recording is armed. This is the transport behind
    ///     the <c>InEngineHooked</c> capture provider. Advertised only by plugin builds with the DXGI
    ///     hook (currently the Windows plugin); the mock and the Linux plugin withhold it.
    /// </summary>
    public const string PresentCapture = "present-capture";
}
