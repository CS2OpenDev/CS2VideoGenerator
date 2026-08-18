namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Metadata describing the desired capture output
/// </summary>
public record CaptureMetadata
{
    /// <summary>
    ///     Desired output filename (without extension)
    /// </summary>
    public required string OutputFileName { get; init; }

    /// <summary>
    ///     Output directory path
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    ///     Video container format (e.g., "mp4", "mkv", "mov")
    /// </summary>
    public string ContainerFormat { get; init; } = "mp4";

    /// <summary>
    ///     Video codec (e.g., "h264", "h265", "vp9")
    /// </summary>
    public string VideoCodec { get; init; } = "h264";

    /// <summary>
    ///     Audio codec (e.g., "aac", "opus", "mp3")
    /// </summary>
    public string? AudioCodec { get; init; } = "aac";

    /// <summary>
    ///     Whether to capture audio. When false, the capture provider mutes its audio inputs for
    ///     the duration of the capture and restores their prior mute states afterwards.
    /// </summary>
    public bool CaptureAudio { get; init; } = true;

    /// <summary>
    ///     Video resolution width in pixels
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    ///     Video resolution height in pixels
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    ///     Frame rate (frames per second)
    /// </summary>
    public int FrameRate { get; init; } = 60;

    /// <summary>
    ///     Playback speed of the finished clip relative to game time: 1 (default) plays in real
    ///     time, 0.5 is 2x slow motion, 0.25 is 4x. Honored by the offline in-engine providers,
    ///     which render at <c>FrameRate / PlaybackSpeed</c> and encode at <see cref="FrameRate" />
    ///     (every slow-motion frame is a real rendered frame, not an interpolation), and stretch the
    ///     engine's game-time audio by the same factor so the tracks stay the same length.
    /// </summary>
    /// <remarks>
    ///     Not the engine's <c>demo_timescale</c>, which is measurably inert against game time for
    ///     offline capture (2026-07-30/31 matrix runs R7/R8: frame counts track host_framerate
    ///     alone). The realtime OBS provider ignores this; its speed mechanism is playing the demo
    ///     at a timescale, which is a session-level act, not clip metadata. Values above 1
    ///     (fast-forward) work by the same arithmetic but subsample the simulation below the demo's
    ///     tick rate; 2 with a 60 fps clip renders 30 simulated frames per game second.
    /// </remarks>
    public double PlaybackSpeed { get; init; } = 1.0;

    /// <summary>
    ///     When <see cref="PlaybackSpeed" /> is not 1, additionally produce a real-time copy of the
    ///     clip next to it (<c>&lt;name&gt;.normalized.&lt;container&gt;</c>). The copy costs almost
    ///     nothing and loses nothing: the video is retimed by rewriting timestamps
    ///     (<c>-itsscale</c> + stream copy, no re-encode), and its audio is the engine's original
    ///     game-time wav, muxed untouched - zero tempo-processing passes, unlike the speed clip's
    ///     own track (one pass) or a post-hoc renormalization (two passes, measurably smeared;
    ///     see the G-block analysis). Offline in-engine providers only; ignored at speed 1.
    /// </summary>
    public bool ProduceNormalizedCopy { get; init; }

    /// <summary>
    ///     Video bitrate in kbps
    /// </summary>
    public int? VideoBitrate { get; init; }

    /// <summary>
    ///     Audio bitrate in kbps
    /// </summary>
    public int? AudioBitrate { get; init; }

    /// <summary>
    ///     Encoder preset (e.g., "ultrafast", "fast", "medium", "slow")
    /// </summary>
    public string? EncoderPreset { get; init; }

    /// <summary>
    ///     Constant Rate Factor for quality (lower = higher quality, typical range 18-28)
    /// </summary>
    public int? Crf { get; init; }

    /// <summary>
    ///     Tags or metadata to embed in the video file
    /// </summary>
    public Dictionary<string, string>? Tags { get; init; }

    /// <summary>
    ///     Description or notes about this capture
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Gets the full output file path
    /// </summary>
    public string GetFullOutputPath() =>
        Path.Combine(OutputDirectory, $"{OutputFileName}.{ContainerFormat}");

    /// <summary>
    ///     Gets a formatted resolution string (e.g., "1920x1080")
    /// </summary>
    public string? GetResolutionString() =>
        Width.HasValue && Height.HasValue ? $"{Width}x{Height}" : null;

    /// <summary>
    ///     Creates a copy of this metadata with an indexed filename for a specific clip
    ///     (<c>clip</c> -> <c>clip_000</c>, <c>clip_001</c>, ...). The one place a clip's indexed
    ///     output name is formed, for compilation clips and session recordings alike.
    /// </summary>
    public CaptureMetadata WithClipIndex(int clipIndex) => this with
    {
        OutputFileName = $"{OutputFileName}_{clipIndex:D3}"
    };
}
