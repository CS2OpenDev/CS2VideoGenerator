#region

using Cs2VideoGenerator.Core.Services;

#endregion

namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Result of a compilation capture operation.
/// </summary>
public record Cs2CompilationResult
{
    /// <summary>
    ///     Whether the compilation completed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     Results for each clip that was processed.
    /// </summary>
    public required IReadOnlyList<Cs2ClipResult> ClipResults { get; init; }

    /// <summary>
    ///     Error message if the compilation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Path to the concatenated output file, if concatenation was performed.
    /// </summary>
    public string? ConcatenatedOutputPath { get; init; }

    /// <summary>
    ///     Result of the FFmpeg concatenation operation, if performed.
    /// </summary>
    public FfmpegConcatResult? ConcatenationResult { get; init; }

    /// <summary>
    ///     Total number of clips in the compilation.
    /// </summary>
    public int TotalClips => ClipResults.Count;

    /// <summary>
    ///     Number of successfully completed clips.
    /// </summary>
    public int CompletedClips => ClipResults.Count(r => r.Success);

    /// <summary>
    ///     Total duration of all captured clips.
    /// </summary>
    public TimeSpan TotalDuration => TimeSpan.FromTicks(ClipResults.Sum(r => r.Duration.Ticks));

    /// <summary>
    ///     Creates a successful compilation result.
    /// </summary>
    public static Cs2CompilationResult Succeeded(IReadOnlyList<Cs2ClipResult> results) =>
        new() { Success = true, ClipResults = results };

    /// <summary>
    ///     Creates a successful compilation result with concatenation.
    /// </summary>
    public static Cs2CompilationResult Succeeded(
        IReadOnlyList<Cs2ClipResult> results,
        FfmpegConcatResult concatResult) =>
        new()
        {
            Success = true,
            ClipResults = results,
            ConcatenatedOutputPath = concatResult.OutputPath,
            ConcatenationResult = concatResult
        };

    /// <summary>
    ///     Creates a failed compilation result.
    /// </summary>
    public static Cs2CompilationResult Failed(IReadOnlyList<Cs2ClipResult> results, string error) =>
        new() { Success = false, ClipResults = results, ErrorMessage = error };
}
