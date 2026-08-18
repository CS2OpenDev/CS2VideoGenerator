#region

using System.Text.Json.Serialization;
using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Everything needed to generate one continuous clip from a CS2 match.
/// </summary>
public class Cs2CompilationClip
{
    /*
     * Limitations we are currently okay with accepting:
     *   * Only a single continuous tick range from a single demo file
     *   * Force the view blindly without ensuring the player is alive for the duration
     */

    /// <summary>
    ///     Steam ID of the player featured in this clip.
    /// </summary>
    public required long PlayerSteamId { get; init; }

    /// <summary>
    ///     Checksum of the match this clip is from.
    /// </summary>
    public required string MatchChecksum { get; init; }

    /// <summary>
    ///     Path to the demo file.
    /// </summary>
    public required string DemoFilePath { get; init; }

    /// <summary>
    ///     Starting tick for the clip.
    /// </summary>
    public required int StartTick { get; init; }

    /// <summary>
    ///     Ending tick for the clip.
    /// </summary>
    public required int EndTick { get; init; }

    /// <summary>
    ///     Player name to spectate during this clip.
    /// </summary>
    public required string PlayerNameToSpectate { get; init; }

    /// <summary>
    ///     Demo playback options for this clip.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Cs2ClipOptions ClipOptions { get; init; } = Cs2ClipOptions.Default;

    /// <summary>
    ///     Per-clip override of <see cref="Cs2CompilationSettings.CaptureAudio" />.
    ///     When null (default), the compilation-level setting applies.
    /// </summary>
    public bool? CaptureAudio { get; init; }

    /// <summary>
    ///     Playback speed of this clip relative to game time: 1 (default) is real time, 0.5 is 2x
    ///     slow motion, 2 is 2x fast-forward. Honored by the offline in-engine capture providers,
    ///     which render every slow-motion frame for real (see
    ///     <see cref="CaptureMetadata.PlaybackSpeed" />); the realtime OBS provider ignores it.
    ///     Per clip because slow motion is usually wanted on one key moment, not a whole compilation.
    /// </summary>
    public double PlaybackSpeed { get; init; } = 1.0;

    /// <summary>
    ///     When <see cref="PlaybackSpeed" /> is not 1, also emit a real-time copy of this clip
    ///     alongside it (<see cref="CaptureMetadata.ProduceNormalizedCopy" />). The copy is produced
    ///     losslessly at capture time (retimed by stream copy, muxing the engine's original
    ///     game-time audio untouched), so it costs almost nothing. Reported on
    ///     <see cref="CaptureResult.AdditionalOutputs" />; the concatenation step uses the primary
    ///     (speed) clip, so a normalized copy is a side artifact, not part of the reel.
    /// </summary>
    public bool ProduceNormalizedCopy { get; init; }
}
