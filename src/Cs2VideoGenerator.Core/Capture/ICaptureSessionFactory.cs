namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Creates <see cref="ICaptureSession" /> instances for one capture backend, and answers the
///     engine-free questions that have to be settled before CS2 is launched.
/// </summary>
public interface ICaptureSessionFactory
{
    /// <summary>
    ///     Stable provider name ("OBS", "InEngine", ...). Used for selection, diagnostics and the
    ///     capture metrics tags.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Whether this backend can run on the current operating system at all. False means the
    ///     provider is unavailable no matter how it is configured.
    /// </summary>
    bool IsSupportedOnCurrentPlatform { get; }

    /// <summary>
    ///     Checks whether the backend is installed and configured well enough to be used. Runs
    ///     before CS2 is launched and needs no engine session: it inspects configuration and the
    ///     file system only, and never starts the backend.
    /// </summary>
    Task<CaptureAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates a capture session for one engine session. The returned session is not brought up
    ///     yet; the caller does that with <see cref="ICaptureSession.InitializeAsync" />, before CS2
    ///     launches.
    /// </summary>
    ICaptureSession Create(CaptureSessionContext context);
}
