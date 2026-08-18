namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Defines the contract for managing CS2 or mock CS2 process lifecycle.
///     Implementations handle process launch, plugin management, and cleanup.
/// </summary>
public interface ICs2ProcessManager : IAsyncDisposable
{
    /// <summary>
    ///     The process ID of the running process, or null if not running.
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    ///     Whether the process is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Launches the process with the configured options.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LaunchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Launches the process with the specified display options.
    /// </summary>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    /// <param name="fullscreen">Whether to launch in fullscreen mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LaunchAsync(int? width, int? height, bool fullscreen, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops the process and performs cleanup (restore configs for real CS2).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Waits for the process to exit naturally.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Information about how the process exited.</returns>
    Task<ProcessExitResult> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Raised when the process exits.
    /// </summary>
    event EventHandler<ProcessExitedEventArgs>? Exited;
}
