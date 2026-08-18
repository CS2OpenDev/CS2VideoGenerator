#region

using System.Text.Json.Serialization;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Optional FFmpeg encode controls for the final concatenated output file.
///     <para>
///     These settings apply only to the FFmpeg concatenation stage. Per-clip recording
///     quality (encoder, bitrate, format of the individual clips) is governed by the
///     OBS profile, not by CSVG.
///     </para>
///     <para>
///     When the block is absent or every property is null, concatenation uses lossless
///     stream copy (<c>-c:v copy -c:a copy</c>) — today's fast default. Setting any
///     video property switches the video stream to re-encode mode with the given
///     settings; setting any audio property does the same for the audio stream. A
///     stream with no settings remains stream-copied.
///     </para>
/// </summary>
public class Cs2EncodingSettings
{
    /// <summary>
    ///     FFmpeg video codec for the concatenated output (e.g. <c>libx264</c>, <c>libx265</c>, <c>libsvtav1</c>).
    ///     Emitted as <c>-c:v &lt;codec&gt;</c>.
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    ///     Constant rate factor for quality-based video encoding (0-51 for x264/x265; lower is higher quality).
    ///     Emitted as <c>-crf &lt;value&gt;</c>. Mutually exclusive with <see cref="VideoBitrate" />.
    /// </summary>
    public int? Crf { get; init; }

    /// <summary>
    ///     Target video bitrate for bitrate-based encoding, in FFmpeg notation (e.g. <c>8M</c>, <c>4000k</c>).
    ///     Emitted as <c>-b:v &lt;value&gt;</c>. Mutually exclusive with <see cref="Crf" />.
    /// </summary>
    public string? VideoBitrate { get; init; }

    /// <summary>
    ///     FFmpeg audio codec for the concatenated output (e.g. <c>aac</c>, <c>libopus</c>).
    ///     Emitted as <c>-c:a &lt;codec&gt;</c>.
    /// </summary>
    public string? AudioCodec { get; init; }

    /// <summary>
    ///     Target audio bitrate in FFmpeg notation (e.g. <c>192k</c>). Emitted as <c>-b:a &lt;value&gt;</c>.
    /// </summary>
    public string? AudioBitrate { get; init; }

    /// <summary>
    ///     Raw FFmpeg arguments inserted before the concat input (e.g. <c>-hwaccel auto</c>).
    ///     Power-user escape hatch: passed to FFmpeg as-is, without validation or escaping.
    ///     Does not by itself switch the output to re-encode mode.
    /// </summary>
    public string? ExtraInputArgs { get; init; }

    /// <summary>
    ///     Raw FFmpeg arguments appended to the output options (e.g. <c>-movflags +faststart</c>).
    ///     Power-user escape hatch: passed to FFmpeg as-is, without validation or escaping.
    ///     Does not by itself switch the output to re-encode mode.
    /// </summary>
    public string? ExtraOutputArgs { get; init; }

    /// <summary>
    ///     Whether any video setting is present, switching the video stream to re-encode mode.
    /// </summary>
    [JsonIgnore]
    public bool HasVideoSettings => VideoCodec is not null || Crf.HasValue || VideoBitrate is not null;

    /// <summary>
    ///     Whether any audio setting is present, switching the audio stream to re-encode mode.
    /// </summary>
    [JsonIgnore]
    public bool HasAudioSettings => AudioCodec is not null || AudioBitrate is not null;

    /// <summary>
    ///     Whether these settings require re-encoding (any video or audio setting present).
    ///     Extra input/output args alone do not require re-encoding; they are appended to
    ///     the stream-copy invocation.
    /// </summary>
    [JsonIgnore]
    public bool RequiresReEncode => HasVideoSettings || HasAudioSettings;
}
