namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     A capture backend bound to a single engine session: owns whatever has to exist once per CS2
///     run (a WebSocket connection, an external process, a staging directory) and hands out one
///     <see cref="IClipCapture" /> per clip.
/// </summary>
public interface ICaptureSession : IAsyncDisposable
{
    /// <summary>
    ///     Stable provider name, matching <see cref="ICaptureSessionFactory.ProviderName" />.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Invariant properties of this backend: timing model, engine use, frame-rate handling.
    /// </summary>
    CaptureTraits Traits { get; }

    /// <summary>
    ///     Raised roughly once per second while a clip is recording, with elapsed time and whatever
    ///     byte/frame counters the backend can report.
    /// </summary>
    event EventHandler<CaptureProgressEventArgs>? ProgressUpdated;

    /// <summary>
    ///     Raised for backend problems, from warnings that do not stop capture up to critical
    ///     failures. Capture failures are reported as failed <see cref="CaptureResult" />s; this
    ///     event is the diagnostic channel alongside them.
    /// </summary>
    event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    /// <summary>
    ///     Brings the backend up: launches and connects to external software, prepares scenes or
    ///     staging directories, probes encoders. Runs before CS2 is launched, which is what the OBS
    ///     game-capture source needs, so no clip information is available yet; per-clip settings are
    ///     applied in <see cref="BeginClipAsync" />. Idempotent: calling it again on an
    ///     already-initialized session is a no-op.
    /// </summary>
    /// <exception cref="InvalidOperationException">The backend could not be brought up.</exception>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Notifies the backend that a demo has been loaded in the engine. Backends that register
    ///     against the loaded demo (HLAE's mirv handshake) do their work here; the default is a
    ///     no-op.
    /// </summary>
    Task OnDemoLoadedAsync(string demoFilePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    ///     Prepares a single clip and returns the object that brackets its playback. Per-clip
    ///     settings (output directory, audio policy, tick command plan) are applied here.
    /// </summary>
    Task<IClipCapture> BeginClipAsync(ClipCaptureContext context, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Tears the backend back down without disposing it: aborts a clip still in flight,
    ///     disconnects, and stops any process this session started. Called before CS2 is killed,
    ///     because a screen recorder hooked into CS2 must not outlive it. Reversible:
    ///     <see cref="InitializeAsync" /> can bring the same session up again.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
