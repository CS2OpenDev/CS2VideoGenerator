namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Result of availability check
/// </summary>
public record CaptureAvailabilityResult
{
    /// <summary>
    ///     Whether the capture provider is available
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    ///     List of issues found during availability check
    /// </summary>
    public List<string> Issues { get; init; } = [];

    /// <summary>
    ///     Provider version information
    /// </summary>
    public string? ProviderVersion { get; init; }

    /// <summary>
    ///     Additional diagnostic information
    /// </summary>
    public Dictionary<string, object>? DiagnosticInfo { get; init; }

    /// <summary>
    ///     Creates a successful availability result.
    /// </summary>
    public static CaptureAvailabilityResult Succeeded(string? version = null) =>
        new() { IsAvailable = true, ProviderVersion = version };

    /// <summary>
    ///     Creates a failed availability result.
    /// </summary>
    public static CaptureAvailabilityResult Failed(params string[] issues) =>
        new() { IsAvailable = false, Issues = [..issues] };
}

/// <summary>
///     Result of a completed capture
/// </summary>
public record CaptureResult
{
    /// <summary>
    ///     Whether the capture completed successfully
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     Path to the output file
    /// </summary>
    public required string OutputFilePath { get; init; }

    /// <summary>
    ///     Total capture duration
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    ///     Output file size in bytes
    /// </summary>
    public long? FileSizeBytes { get; init; }

    /// <summary>
    ///     Total frames captured
    /// </summary>
    public long? TotalFrames { get; init; }

    /// <summary>
    ///     Frames dropped during capture
    /// </summary>
    public long? DroppedFrames { get; init; }

    /// <summary>
    ///     Average bitrate in kbps
    /// </summary>
    public double? AverageBitrate { get; init; }

    /// <summary>
    ///     Error message if capture failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Warnings encountered during capture
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    ///     Secondary artifacts produced alongside <c>OutputFilePath</c>: a speed clip's normalized
    ///     real-time copy (<see cref="CaptureMetadata.ProduceNormalizedCopy" />), a kept engine wav
    ///     (<c>Capture:InEngine:KeepEngineAudio</c>). Empty for a plain capture.
    /// </summary>
    public List<string> AdditionalOutputs { get; init; } = [];

    /// <summary>
    ///     Additional provider-specific statistics
    /// </summary>
    public Dictionary<string, object>? Statistics { get; init; }

    /// <summary>
    ///     Timestamp when capture started
    /// </summary>
    public DateTime? StartTime { get; init; }

    /// <summary>
    ///     Timestamp when capture ended
    /// </summary>
    public DateTime? EndTime { get; init; }
}
