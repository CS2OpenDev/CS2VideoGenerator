#region

using Cs2VideoGenerator.Core.Proto;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Engine-truth demo state snapshot reported by a plugin supporting the
///     <see cref="CsvgCapabilities.DemoStateEvents" /> capability ("demo-state-events"). Every state
///     field is nullable. <c>null</c> means the connected plugin build cannot read that field (the
///     wire fields are proto3 optionals shipped incrementally); it does not mean false or zero.
/// </summary>
/// <param name="IsPlayingDemo">
///     Whether a demo is currently playing (<c>ISource2EngineToClient::IsPlayingDemo</c>), or null
///     if this plugin build does not report it.
/// </param>
/// <param name="IsPaused">
///     Whether demo playback is paused (<c>IDemoFile::IsDemoPaused</c>), or null if this plugin
///     build does not report it. Engine truth only when the plugin also advertises
///     <see cref="CsvgCapabilities.EnginePauseDetection" /> ("engine-pause-detection").
/// </param>
/// <param name="DemoTick">
///     The current demo tick (<c>IDemoFile::GetDemoTick</c>), or null if not reported.
/// </param>
/// <param name="DemoFilePath">
///     Path of the currently loaded demo, or null if not reported. An empty string means no demo is
///     loaded. Populated only by plugins advertising <see cref="CsvgCapabilities.DemoIdentity" />
///     ("demo-identity").
/// </param>
/// <param name="Timescale">
///     The demo timescale, or null if not reported. In this protocol version this is the last
///     host-set value (there is no engine readback), so user console changes are not reflected.
/// </param>
/// <param name="SpectatedPlayerName">
///     Reserved: the currently spectated player. Always null until a plugin ships the
///     <see cref="CsvgCapabilities.SpectatorReport" /> ("spectator-report") capability.
/// </param>
/// <param name="Origin">
///     Whether the plugin attributes this state change to a host command or a user action inside
///     the game client (suppression-window attribution).
/// </param>
public record DemoState(
    bool? IsPlayingDemo,
    bool? IsPaused,
    int? DemoTick,
    string? DemoFilePath,
    float? Timescale,
    string? SpectatedPlayerName,
    DemoStateOrigin Origin)
{
    // proto3 optional-field presence maps to nullability (absent = null). unrecognized ChangeOrigin
    // values fold into DemoStateOrigin.Unknown.
    internal static DemoState FromProto(DemoStateEvent stateEvent) => new(
        stateEvent.HasIsPlayingDemo ? stateEvent.IsPlayingDemo : null,
        stateEvent.HasIsPaused ? stateEvent.IsPaused : null,
        stateEvent.HasDemoTick ? stateEvent.DemoTick : null,
        stateEvent.HasDemoFilePath ? stateEvent.DemoFilePath : null,
        stateEvent.HasDemoTimescale ? stateEvent.DemoTimescale : null,
        stateEvent.HasSpectatedPlayerName ? stateEvent.SpectatedPlayerName : null,
        stateEvent.Origin switch
        {
            DemoStateEvent.Types.ChangeOrigin.OriginHostCommand => DemoStateOrigin.HostCommand,
            DemoStateEvent.Types.ChangeOrigin.OriginUser => DemoStateOrigin.User,
            _ => DemoStateOrigin.Unknown
        });
}
