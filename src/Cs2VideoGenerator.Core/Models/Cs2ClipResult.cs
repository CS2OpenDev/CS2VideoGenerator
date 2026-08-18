#region

using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Result of capturing a single clip.
/// </summary>
public record Cs2ClipResult
{
    /// <summary>
    ///     Index of the clip in the compilation (0-based).
    /// </summary>
    public required int ClipIndex { get; init; }

    /// <summary>
    ///     Whether this clip was captured successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     Path to the output file.
    /// </summary>
    public required string OutputFilePath { get; init; }

    /// <summary>
    ///     Duration of the captured clip.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    ///     Error message if the clip capture failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     The underlying capture result from the video capture provider.
    /// </summary>
    public CaptureResult? CaptureResult { get; init; }
}
