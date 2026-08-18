#region

using Cs2VideoGenerator.Core.Engine;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Everything a capture session is given about the engine session it belongs to. Deliberately
///     narrow: a capture backend gets console/telemetry access through
///     <see cref="IEngineConsole" /> and nothing more; scheduling and playback stay with
///     orchestration.
/// </summary>
public sealed record CaptureSessionContext
{
    /// <summary>
    ///     The least-privilege engine view: capability introspection and tick telemetry.
    /// </summary>
    public required IEngineConsole Engine { get; init; }

    /// <summary>
    ///     The engine session identifier when it is already known, or null. Capture sessions are
    ///     created before CS2 launches, so this is usually null at construction time and is only
    ///     useful for logging.
    /// </summary>
    public string? EngineSessionId { get; init; }

    /// <summary>
    ///     Cancelled when the owning engine session goes away. Long-running background work inside
    ///     a capture session should observe it.
    /// </summary>
    public CancellationToken SessionToken { get; init; }
}
