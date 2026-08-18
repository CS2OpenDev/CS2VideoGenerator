namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Console commands a capture backend wants executed on the engine thread when the observed demo
///     tick first crosses <paramref name="DemoTick" />.
/// </summary>
/// <param name="DemoTick">The demo tick the batch fires on.</param>
/// <param name="Commands">The console commands to run, in order.</param>
public sealed record TickCommandBatch(int DemoTick, IReadOnlyList<string> Commands);

/// <summary>
///     A capture backend's declarative request for engine-side brackets around a clip. The backend
///     describes what has to run and when; <see cref="ClipCaptureCoordinator" /> arms the batches
///     before playback starts, so the same-tick ordering guarantee puts them ahead of the plugin's
///     internal end-of-range auto-pause.
/// </summary>
/// <remarks>
///     Three channels, and which one a command belongs in is decided by whether it needs a tick.
///     <see cref="Batches" /> is for work that does: it has to happen at a moment in the demo.
///     <see cref="SetupCommands" /> and <see cref="AbortCommands" /> are the immediate ones (they
///     have to happen now, before playback and after a failure respectively), and both go through
///     the plugin's engine-thread queue rather than its tick scheduler.
/// </remarks>
public sealed record TickCommandPlan
{
    /// <summary>
    ///     Console commands to run immediately before playback starts, for engine state the clip needs
    ///     in place but which has no tick of its own (cvar assignment, above all). Empty for backends
    ///     with nothing to set up.
    /// </summary>
    /// <remarks>
    ///     Deliberately not tick-scheduled. A batch armed at or below the tick the plugin has already
    ///     observed is stranded for good - the plugin fires batches on the half-open interval
    ///     (last observed tick, current tick] - and a second clip from the same demo starts from
    ///     wherever the previous one left the playhead. Setup that only has to land before the
    ///     recording tick, not on any particular one, has nothing to gain from the scheduler and
    ///     everything to lose to that interval.
    /// </remarks>
    public IReadOnlyList<string> SetupCommands { get; init; } = [];

    /// <summary>
    ///     The batches to arm before playback, in the order they should be registered.
    /// </summary>
    public required IReadOnlyList<TickCommandBatch> Batches { get; init; }

    /// <summary>
    ///     Console commands that must run if the clip is aborted mid-playback (the backend's
    ///     emergency stop). Empty for backends that need no engine-side cleanup.
    /// </summary>
    public IReadOnlyList<string> AbortCommands { get; init; } = [];
}
