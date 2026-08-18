namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Result of a
///     <see cref="Engine.ICs2EngineSession.SetDemoTickAsync(int, bool?, bool, CancellationToken)" /> seek.
/// </summary>
/// <param name="Success">
///     Whether the seek completed successfully. On the fire-and-forget path (no acknowledgement
///     requested or supported) this only means the command was written to the plugin stream; on the
///     acknowledged path it is the plugin-reported outcome from the <c>CommandAckEvent</c>.
/// </param>
/// <param name="ObservedDemoTick">
///     The tick the engine was actually observed at when the seek was deemed complete (unlike the
///     legacy forced tick update, which pre-echoed the target tick before arrival). Null on the
///     fire-and-forget path or when the plugin did not report it.
/// </param>
/// <param name="Error">
///     Plugin-reported error message when <paramref name="Success" /> is false, or null.
/// </param>
public record SeekResult(bool Success, int? ObservedDemoTick, string? Error);
