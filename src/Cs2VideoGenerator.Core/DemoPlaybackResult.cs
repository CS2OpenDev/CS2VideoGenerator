#region

using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     Result of a demo playback operation, optionally including capture information.
/// </summary>
public record DemoPlaybackResult
{
    /// <summary>
    ///     Whether demo playback completed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     The starting tick that was played.
    /// </summary>
    public required int StartTick { get; init; }

    /// <summary>
    ///     The ending tick that was played.
    /// </summary>
    public required int EndTick { get; init; }

    /// <summary>
    ///     Whether this playback included capture.
    /// </summary>
    public bool WasCaptured { get; init; }

    /// <summary>
    ///     The capture result, if capture was performed. Null if WasCaptured is false.
    /// </summary>
    public CaptureResult? CaptureResult { get; init; }

    /// <summary>
    ///     Error message if playback failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Creates a successful playback result without capture.
    /// </summary>
    public static DemoPlaybackResult Succeeded(int startTick, int endTick) => new()
    {
        Success = true, StartTick = startTick, EndTick = endTick, WasCaptured = false
    };

    /// <summary>
    ///     Creates a successful playback result with capture.
    /// </summary>
    public static DemoPlaybackResult Succeeded(int startTick, int endTick, CaptureResult captureResult) => new()
    {
        Success = captureResult.Success,
        StartTick = startTick,
        EndTick = endTick,
        WasCaptured = true,
        CaptureResult = captureResult,
        ErrorMessage = captureResult.Success ? null : captureResult.ErrorMessage
    };

    /// <summary>
    ///     Creates a failed playback result.
    /// </summary>
    public static DemoPlaybackResult Failed(int startTick, int endTick, string errorMessage) => new()
    {
        Success = false,
        StartTick = startTick,
        EndTick = endTick,
        WasCaptured = false,
        ErrorMessage = errorMessage
    };
}
