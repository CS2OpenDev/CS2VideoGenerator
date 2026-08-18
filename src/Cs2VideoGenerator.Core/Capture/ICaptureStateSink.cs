namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Optional hook a capture session can implement to be told about the
///     <see cref="ClipCaptureCoordinator" />'s state transitions. The coordinator owns the
///     <see cref="CaptureState" /> machine; a session implements this only when it needs to observe
///     it. Today that means feeding the capture metrics gauges, which are registered per session and
///     have to report what capture is currently doing.
/// </summary>
internal interface ICaptureStateSink
{
    /// <summary>
    ///     Called after every state transition, on the thread that made it. Implementations must be
    ///     fast and must not throw.
    /// </summary>
    void OnCaptureStateChanged(CaptureState state);
}
