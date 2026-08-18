namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     One clip's recording, brackets included. Created by
///     <see cref="ICaptureSession.BeginClipAsync" /> and driven by
///     <see cref="ClipCaptureCoordinator" />; never reused for a second clip.
/// </summary>
/// <remarks>
///     Pause and resume are deliberately absent: only OBS can do them, nothing outside OBS ever
///     asked, and a backend that can pause advertises
///     <see cref="CaptureTraits.SupportsPauseResume" /> and exposes the operations on its concrete
///     clip type.
/// </remarks>
public interface IClipCapture : IAsyncDisposable
{
    /// <summary>
    ///     Engine-side brackets this clip needs, or null when the backend touches no engine state
    ///     (every screen recorder). A non-null plan requires the plugin to advertise
    ///     <see cref="CsvgCapabilities.TickScheduledCommands" />, and one carrying
    ///     <see cref="TickCommandPlan.SetupCommands" /> also requires
    ///     <see cref="CsvgCapabilities.ConsoleExec" />; the coordinator fails the clip with a failed
    ///     <see cref="CaptureResult" /> naming the capability when either is missing.
    /// </summary>
    TickCommandPlan? TickPlan { get; }

    /// <summary>
    ///     Called once the engine brackets are armed and immediately before playback is requested.
    ///     A real-time recorder starts recording here.
    /// </summary>
    Task OnPlaybackStartingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Called once playback has reached the end tick and the engine has paused. A real-time
    ///     recorder stops recording here.
    /// </summary>
    Task OnPlaybackEndedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Completes the clip and reports the outcome: waits for the output to land, moves it into
    ///     place, and collects statistics. Never throws for capture-level problems: a failure is a
    ///     <see cref="CaptureResult" /> with <see cref="CaptureResult.Success" /> false.
    /// </summary>
    Task<CaptureResult> FinalizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gives up on the clip and releases whatever it holds. Idempotent and never throws:
    ///     it runs on paths that are already failing.
    /// </summary>
    Task AbortAsync(CaptureAbortReason reason, CancellationToken cancellationToken = default);
}
