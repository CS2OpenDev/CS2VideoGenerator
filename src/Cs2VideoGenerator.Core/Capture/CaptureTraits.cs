namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     How a capture backend relates recorded time to demo time.
/// </summary>
public enum CaptureTimingModel
{
    /// <summary>
    ///     The backend records the screen in real time, so the output length equals the wall-clock
    ///     duration of playback (demo time divided by the demo timescale, plus whatever paused or
    ///     loading frames render inside the recording brackets). <see cref="CaptureResult.Duration" />
    ///     is that wall-clock span, and <see cref="CaptureMetadata.FrameRate" /> is advisory only:
    ///     the actual frame rate comes from the backend's own configuration.
    /// </summary>
    WallClock,

    /// <summary>
    ///     The backend renders a fixed number of frames per demo tick, so the output length is a
    ///     function of the tick range and the requested frame rate rather than of wall-clock time.
    ///     <see cref="CaptureResult.Duration" /> is frames divided by frame rate, and
    ///     <see cref="CaptureMetadata.FrameRate" /> is honored (it drives the backend's frame
    ///     cadence). <see cref="InEngineCaptureSession" /> is the provider that works this way.
    /// </summary>
    TickExact
}

/// <summary>
///     Invariant properties of a capture backend, published by its
///     <see cref="ICaptureSession" /> so orchestration can adapt without knowing the concrete
///     provider.
/// </summary>
public sealed record CaptureTraits
{
    /// <summary>
    ///     How the backend relates recorded time to demo time. See
    ///     <see cref="CaptureTimingModel" /> for the <see cref="CaptureResult.Duration" /> and
    ///     <see cref="CaptureMetadata.FrameRate" /> semantics each model implies.
    /// </summary>
    public required CaptureTimingModel TimingModel { get; init; }

    /// <summary>
    ///     Whether the backend issues engine console commands (and therefore publishes a
    ///     <see cref="IClipCapture.TickPlan" /> that has to be armed before playback). False for
    ///     screen recorders, which never touch the engine.
    /// </summary>
    public required bool UsesEngine { get; init; }

    /// <summary>
    ///     Whether <see cref="CaptureMetadata.FrameRate" /> is applied to the recording. False for
    ///     backends whose frame rate comes from their own configuration (an OBS profile, for
    ///     instance), where the metadata value is reporting-only.
    /// </summary>
    public required bool HonorsFrameRate { get; init; }

    /// <summary>
    ///     Whether the backend can pause and resume a clip in progress, writing both halves into the
    ///     same output file. Pause/resume is not part of <see cref="IClipCapture" />; a backend that
    ///     advertises it exposes the operations on its concrete clip type.
    /// </summary>
    public bool SupportsPauseResume { get; init; }
}
