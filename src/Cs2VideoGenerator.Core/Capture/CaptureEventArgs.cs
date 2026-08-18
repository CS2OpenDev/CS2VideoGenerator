namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Event args for capture state changes.
/// </summary>
public class CaptureStateChangedEventArgs(CaptureState previousState, CaptureState newState, string? message = null)
    : EventArgs
{
    /// <summary>
    ///     The capture state before the transition.
    /// </summary>
    public CaptureState PreviousState { get; } = previousState;

    /// <summary>
    ///     The capture state after the transition.
    /// </summary>
    public CaptureState NewState { get; } = newState;

    /// <summary>
    ///     Optional message providing context about the state change.
    /// </summary>
    public string? Message { get; } = message;
}

/// <summary>
///     Event args for capture progress updates
/// </summary>
public class CaptureProgressEventArgs(
    TimeSpan elapsedTime,
    long? fileSizeBytes = null,
    long? frameCount = null,
    double? currentBitrate = null,
    long? droppedFrames = null,
    Dictionary<string, object>? additionalMetrics = null)
    : EventArgs
{
    /// <summary>
    ///     Elapsed capture time
    /// </summary>
    public TimeSpan ElapsedTime { get; } = elapsedTime;

    /// <summary>
    ///     Current file size in bytes
    /// </summary>
    public long? FileSizeBytes { get; } = fileSizeBytes;

    /// <summary>
    ///     Current frame count
    /// </summary>
    public long? FrameCount { get; } = frameCount;

    /// <summary>
    ///     Current bitrate in kbps
    /// </summary>
    public double? CurrentBitrate { get; } = currentBitrate;

    /// <summary>
    ///     Dropped frames count
    /// </summary>
    public long? DroppedFrames { get; } = droppedFrames;

    /// <summary>
    ///     Additional provider-specific metrics
    /// </summary>
    public Dictionary<string, object>? AdditionalMetrics { get; } = additionalMetrics;
}

/// <summary>
///     Event args for capture errors.
/// </summary>
public class CaptureErrorEventArgs(
    string errorMessage,
    Exception? exception = null,
    CaptureErrorSeverity severity = CaptureErrorSeverity.Error)
    : EventArgs
{
    /// <summary>
    ///     A human-readable description of the error.
    /// </summary>
    public string ErrorMessage { get; } = errorMessage;

    /// <summary>
    ///     The underlying exception that caused the error, if any.
    /// </summary>
    public Exception? Exception { get; } = exception;

    /// <summary>
    ///     The severity level of the error.
    /// </summary>
    public CaptureErrorSeverity Severity { get; } = severity;
}

/// <summary>
///     Severity level for capture errors
/// </summary>
public enum CaptureErrorSeverity
{
    /// <summary>
    ///     Informational message
    /// </summary>
    Info,

    /// <summary>
    ///     Warning that doesn't stop capture
    /// </summary>
    Warning,

    /// <summary>
    ///     Error that may affect capture quality
    /// </summary>
    Error,

    /// <summary>
    ///     Critical error that stops capture
    /// </summary>
    Critical
}
