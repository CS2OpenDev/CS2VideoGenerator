namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     Least-privilege engine view for capture providers: capability introspection, tick telemetry
///     and the console. No scheduling, no playback and no demo control are reachable through this
///     interface - components that need those depend on <see cref="ICs2EngineSession" /> instead.
///     The console is here because a backend's own engine work (starting and stopping a recording,
///     restoring its cvars) is console work, and it is the only privilege that is not read-only.
/// </summary>
public interface IEngineConsole
{
    /// <summary>
    ///     Capability tokens advertised by the connected plugin build (see
    ///     <see cref="CsvgCapabilities" />). Empty until the plugin's process-status handshake
    ///     arrives and after the session ends. An empty set means "assume the v1.0 baseline
    ///     command set".
    /// </summary>
    IReadOnlySet<string> Capabilities { get; }

    /// <summary>
    ///     Latest demo tick observed from the plugin, or null if none has been received.
    /// </summary>
    long? LastObservedTick { get; }

    /// <summary>
    ///     Whether the connected plugin advertises the given capability token (see
    ///     <see cref="CsvgCapabilities" />). Always false before the handshake and after the
    ///     session ends.
    /// </summary>
    bool Supports(string capability);

    /// <summary>
    ///     Raised for each tick update observed from the plugin, carrying the observed demo tick.
    /// </summary>
    /// <remarks>
    ///     Synchronous <see cref="Action" /> rather than async, for throughput. Handlers must be
    ///     fast and non-blocking or they stall the event processing pipeline.
    /// </remarks>
    event Action<long>? TickObserved;

    /// <summary>
    ///     Executes console commands on CS2's engine thread as soon as it reaches its next frame -
    ///     no tick condition, which is what an abort path needs (a tick-scheduled restore at a tick
    ///     that will never arrive restores nothing). Requires a plugin advertising
    ///     <see cref="CsvgCapabilities.ConsoleExec" />; there is no legacy equivalent, so this
    ///     throws rather than degrading.
    /// </summary>
    /// <remarks>
    ///     Blind: the engine reports nothing back, so the returned task completes when the plugin
    ///     has queued the batch for its engine thread. A command CS2 rejects (unknown name, refused
    ///     value) fails silently there, exactly as it does when typed into the console.
    /// </remarks>
    Task ExecuteConsoleCommandsAsync(IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default);
}
