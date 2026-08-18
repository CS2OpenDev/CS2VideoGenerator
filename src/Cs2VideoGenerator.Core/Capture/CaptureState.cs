namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Represents the state of a capture provider
/// </summary>
public enum CaptureState
{
    /// <summary>
    ///     Provider is not initialized
    /// </summary>
    NotInitialized,

    /// <summary>
    ///     Checking availability and prerequisites
    /// </summary>
    CheckingAvailability,

    /// <summary>
    ///     Provider is available but not prepared
    /// </summary>
    Available,

    /// <summary>
    ///     Preparing capture environment
    /// </summary>
    PreparingEnvironment,

    /// <summary>
    ///     Environment is ready for capture
    /// </summary>
    Ready,

    /// <summary>
    ///     Capture is in progress
    /// </summary>
    Capturing,

    /// <summary>
    ///     Capture is paused
    /// </summary>
    Paused,

    /// <summary>
    ///     Capture is stopping
    /// </summary>
    Stopping,

    /// <summary>
    ///     Finalizing capture (post-processing)
    /// </summary>
    Finalizing,

    /// <summary>
    ///     Capture completed successfully
    /// </summary>
    Completed,

    /// <summary>
    ///     An error occurred
    /// </summary>
    Error,

    /// <summary>
    ///     Provider is disposed
    /// </summary>
    Disposed
}
