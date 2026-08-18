namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Base configuration for capture providers
/// </summary>
public record CaptureConfiguration
{
    /// <summary>
    ///     Path to the capture software executable (if applicable)
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    ///     Working directory for capture operations
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    ///     Whether to automatically start the capture software if not running
    /// </summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>
    ///     Maximum number of retry attempts
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;
}
