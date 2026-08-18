namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Why a clip is being aborted, passed to <see cref="IClipCapture.AbortAsync" /> so a backend can
///     choose how much cleanup is worth attempting.
/// </summary>
public enum CaptureAbortReason
{
    /// <summary>
    ///     The clip could not be armed: the connected plugin is missing a capability the backend's
    ///     <see cref="IClipCapture.TickPlan" /> requires. Nothing has been recorded yet.
    /// </summary>
    CapabilityMissing,

    /// <summary>
    ///     Demo playback failed, timed out, or threw while the clip was recording.
    /// </summary>
    PlaybackFailed,

    /// <summary>
    ///     The backend itself failed and vetoed the clip through
    ///     <see cref="ClipCaptureContext.FailClip" />.
    /// </summary>
    BackendFault,

    /// <summary>
    ///     The caller cancelled the clip.
    /// </summary>
    Cancelled,

    /// <summary>
    ///     The capture session is being torn down with a clip still in flight.
    /// </summary>
    SessionShutdown
}
