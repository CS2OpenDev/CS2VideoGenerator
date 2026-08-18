#region

using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Settings for the compilation output.
/// </summary>
public class Cs2CompilationSettings
{
    /// <summary>
    ///     Directory where output files will be saved.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    ///     Base filename for output files (e.g., "highlight" -> highlight_000.mp4, highlight_001.mp4)
    /// </summary>
    public required string BaseFileName { get; init; }

    /// <summary>
    ///     Video container format (e.g., "mp4", "mkv")
    /// </summary>
    public string ContainerFormat { get; init; } = "mp4";

    /// <summary>
    ///     Frame rate for the output video.
    /// </summary>
    public int FrameRate { get; init; } = 60;

    /// <summary>
    ///     Optional output width in pixels. If null, uses capture default.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    ///     Optional output height in pixels. If null, uses capture default.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    ///     Whether to launch CS2 in fullscreen mode. If false (default), launches windowed.
    /// </summary>
    public bool Fullscreen { get; init; }

    /// <summary>
    ///     Whether to capture audio in the recordings. When false, the capture provider mutes the
    ///     relevant OBS audio inputs for the duration of each clip and restores each input's prior
    ///     mute state afterwards (runtime-only; the OBS profile is never permanently modified).
    ///     Can be overridden per clip via <see cref="Cs2CompilationClip.CaptureAudio" />. Default is true.
    /// </summary>
    public bool CaptureAudio { get; init; } = true;

    /// <summary>
    ///     Whether to concatenate all clips into a single output file after capture.
    ///     When true, uses FFmpeg to combine clips in order. Default is true.
    /// </summary>
    public bool ConcatenateClips { get; init; } = true;

    /// <summary>
    ///     Whether to delete individual clip files after successful concatenation.
    ///     Only applies when ConcatenateClips is true. Default is false.
    /// </summary>
    public bool DeleteClipsAfterConcat { get; init; }

    /// <summary>
    ///     Optional FFmpeg encode controls for the final concatenated output file.
    ///     When absent (or all-null), clips are concatenated with lossless stream copy.
    ///     Per-clip recording quality is governed by the OBS profile; see
    ///     <see cref="Cs2EncodingSettings" />.
    /// </summary>
    public Cs2EncodingSettings? Encoding { get; init; }

    /// <summary>
    ///     Gets the expected path for the concatenated output file.
    /// </summary>
    public string GetConcatenatedOutputPath() =>
        Path.Combine(OutputDirectory, $"{BaseFileName}.{ContainerFormat}");

    /// <summary>
    ///     Builds the capture metadata for one clip of this compilation: these settings, with the
    ///     clip's own audio override applied and the indexed output name
    ///     (<c>{BaseFileName}_000</c>, <c>_001</c>, ...) derived from <paramref name="clipIndex" />.
    /// </summary>
    /// <remarks>
    ///     The only place compilation settings become a <see cref="CaptureMetadata" />. Naming runs
    ///     through <see cref="CaptureMetadata.WithClipIndex" />, the one place the indexed clip
    ///     filename is formed, shared with the session-metadata recording path.
    /// </remarks>
    /// <param name="clip">The clip being captured; its <see cref="Cs2CompilationClip.CaptureAudio" /> wins when set.</param>
    /// <param name="clipIndex">The clip's original index in the compilation, which numbers the output file.</param>
    public CaptureMetadata CreateClipMetadata(Cs2CompilationClip clip, int clipIndex)
    {
        ArgumentNullException.ThrowIfNull(clip);

        return new CaptureMetadata
        {
            OutputDirectory = OutputDirectory,
            OutputFileName = BaseFileName,
            ContainerFormat = ContainerFormat,
            FrameRate = FrameRate,
            Width = Width,
            Height = Height,
            CaptureAudio = clip.CaptureAudio ?? CaptureAudio,
            PlaybackSpeed = clip.PlaybackSpeed,
            ProduceNormalizedCopy = clip.ProduceNormalizedCopy
        }.WithClipIndex(clipIndex);
    }
}
