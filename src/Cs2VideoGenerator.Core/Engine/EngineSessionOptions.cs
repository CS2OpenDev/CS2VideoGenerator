namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     Per-session options passed to <see cref="ICs2EngineHost.CreateSession" />: the launch shape
///     of the CS2 process this engine session brings up. Session-wide configuration (telemetry
///     defaults, capture wiring) continues to bind here as the engine session takes ownership of
///     the corresponding behavior.
/// </summary>
public sealed record EngineSessionOptions
{
    /// <summary>
    ///     Window width in pixels, or null to use CS2's default.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    ///     Window height in pixels, or null to use CS2's default.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    ///     Whether to launch CS2 in fullscreen mode.
    /// </summary>
    public bool Fullscreen { get; init; }
}
