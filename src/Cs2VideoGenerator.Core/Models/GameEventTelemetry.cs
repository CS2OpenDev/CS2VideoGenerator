#region

using Cs2VideoGenerator.Core.Proto;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     A client-side CS2 game event observed by the plugin's game-event hook, forwarded while a
///     subscription started by <see cref="Engine.ICs2EngineSession.SetGameEventSubscriptionAsync" /> is
///     active.
///     Requires a plugin advertising <see cref="CsvgCapabilities.GameEventTelemetry" />
///     ("game-event-telemetry").
/// </summary>
/// <remarks>
///     Best-effort telemetry, deliberately not a control channel: the plugin's forwarding queue is
///     bounded and drops the oldest events under backpressure rather than ever blocking CS2's engine
///     thread. Do not drive capture timing off it. Anything that has to be tick-exact belongs in
///     <see cref="Engine.ICs2EngineSession.ScheduleCommandsAtTickAsync" />.
/// </remarks>
/// <param name="EventName">The game event name, e.g. <c>player_death</c> or <c>round_start</c>.</param>
/// <param name="JsonPayload">
///     The event's KV3 payload serialized as JSON by the game's own serializer (tier0
///     <c>SaveKV3AsJSON</c>). The schema is the game's, varies per event name, and is not stable
///     across CS2 updates. Parse defensively.
/// </param>
public record GameEventTelemetry(string EventName, string JsonPayload)
{
    internal static GameEventTelemetry FromProto(GameEventTelemetryEvent telemetryEvent) =>
        new(telemetryEvent.EventName, telemetryEvent.JsonPayload);
}
