#region

using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Capture;
using Microsoft.Extensions.Logging;

#endregion

namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Manages the OBS Studio process lifecycle.
/// </summary>
public partial class ObsProcessManager(
    ILoggerFactory loggerFactory,
    IPathDiscoveryService pathDiscoveryService)
    : IntegratedProcessLifecycleManager<ObsCaptureConfiguration>(
        loggerFactory?.CreateLogger<ObsProcessManager>(),
        loggerFactory?.CreateLogger("Cs2VideoGenerator.Core.ObsProcess"))
{
    private readonly ILogger _logger = loggerFactory?.CreateLogger<ObsProcessManager>() ??
                                       throw new ArgumentNullException(nameof(loggerFactory));

    private readonly IPathDiscoveryService _pathDiscoveryService =
        pathDiscoveryService ?? throw new ArgumentNullException(nameof(pathDiscoveryService));

    /// <summary>
    ///     Resolves the OBS executable path that would be used to launch OBS: the configured
    ///     <see cref="CaptureConfiguration.ExecutablePath" /> override if set, otherwise the
    ///     (cached) discovered path. Returns null when OBS cannot be found.
    /// </summary>
    /// <param name="config">The OBS capture configuration.</param>
    /// <returns>The OBS executable path, or null if not found.</returns>
    public string? ResolveExecutablePath(ObsCaptureConfiguration config) => DiscoverExecutable(config);

    #region IntegratedProcessLifecycleManager Implementation

    /// <inheritdoc />
    protected override string LogPrefix => "OBS";

    /// <inheritdoc />
    /// <remarks>
    ///     Deletes OBS's <c>.sentinel</c> folder (best-effort) so OBS 32.0+ does not show the
    ///     "Safe Mode" dialog after a previous unclean shutdown. The <c>--disable-shutdown-check</c>
    ///     flag this replaced was removed in OBS 32.0.0, and a modal dialog would block the automated
    ///     launch.
    /// </remarks>
    protected override Task OnBeforeLaunchAsync(ObsCaptureConfiguration config, CancellationToken cancellationToken)
    {
        ClearObsSentinelFolder();
        return Task.CompletedTask;
    }

    private void ClearObsSentinelFolder()
    {
        try
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string sentinelPath = Path.Combine(appDataPath, "obs-studio", ".sentinel");

            if (Directory.Exists(sentinelPath))
            {
                Directory.Delete(sentinelPath, true);
                LogSentinelFolderCleared(_logger);
            }
        }
        catch (Exception ex)
        {
            LogSentinelFolderClearFailed(_logger, ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>Two seconds, so the OBS WebSocket server has a chance to come up before connecting.</remarks>
    protected override TimeSpan PostLaunchDelay => TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    /// <remarks>
    ///     30 seconds (vs. the 5-second base default). Only relevant as a fallback: OBS is normally
    ///     force-killed (see <see cref="AttemptGracefulShutdown" />), so this timeout applies only
    ///     if that toggle is ever flipped back to a graceful close.
    /// </remarks>
    protected override TimeSpan GracefulShutdownTimeout => TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    /// <remarks>
    ///     OBS is force-killed rather than graceful-closed. Its graceful (WM_CLOSE) exit path
    ///     crashes inside OBS's own Qt teardown (<c>obs_enter_graphics</c> in the preview widget
    ///     destructor — a known upstream OBS bug) and pops a crash dialog that blocks the automated
    ///     pipeline until dismissed. Force-killing is safe here because the recording is already
    ///     stopped and finalized over the WebSocket
    ///     (<see cref="Cs2VideoGenerator.Core.Capture.ObsCaptureSession" />)
    ///     before the process is stopped, and OBS's unclean-shutdown <c>.sentinel</c> (which would
    ///     otherwise trigger the Safe Mode dialog) is cleared on the next launch in
    ///     <see cref="OnBeforeLaunchAsync" />.
    /// </remarks>
    protected override bool AttemptGracefulShutdown => false;

    /// <inheritdoc />
    /// <remarks>
    ///     Uses the explicit <see cref="Cs2VideoGenerator.Core.Capture.CaptureConfiguration.ExecutablePath" />
    ///     override when set; otherwise resolves OBS via the shared cached discovery service
    ///     (<see cref="IPathDiscoveryService.GetObsExecutablePath" />).
    /// </remarks>
    protected override string? DiscoverExecutable(ObsCaptureConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.ExecutablePath))
        {
            return config.ExecutablePath;
        }

        // Single OBS discovery implementation (cached) — see IPathDiscoveryService.
        return _pathDiscoveryService.GetObsExecutablePath();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Always passes the configured WebSocket port and <c>--minimize-to-tray</c>, then
    ///     optional <c>--startminimized</c>, <c>--profile</c>, <c>--collection</c>, and any
    ///     extra <see cref="ObsCaptureConfiguration.StartupArguments" /> verbatim.
    /// </remarks>
    protected override IEnumerable<string> BuildArguments(ObsCaptureConfiguration config)
    {
        yield return "--websocket_port";
        yield return config.Port.ToString();
        yield return "--minimize-to-tray";

        if (config.StartMinimized)
        {
            yield return "--startminimized";
        }

        if (!string.IsNullOrWhiteSpace(config.ProfileName))
        {
            yield return "--profile";
            yield return config.ProfileName;
        }

        if (!string.IsNullOrWhiteSpace(config.SceneCollectionName))
        {
            yield return "--collection";
            yield return config.SceneCollectionName;
        }

        foreach (string arg in config.StartupArguments ?? [])
        {
            yield return arg;
        }
    }

    /// <inheritdoc />
    /// <remarks>Logs the exit code and reason; OBS exits require no cleanup on this side.</remarks>
    protected override Task OnProcessExitedAsync(ProcessExitResult result)
    {
        string resultTag = result.Reason switch
        {
            ProcessExitReason.Completed => "normal",
            ProcessExitReason.Canceled => "cancelled",
            _ => "error"
        };

        LogObsProcessExited(_logger, result.ExitCode, resultTag);

        return Task.CompletedTask;
    }

    #endregion

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "OBS process exited (code: {ExitCode}, reason: {Reason})")]
    static partial void LogObsProcessExited(ILogger logger, int exitCode, string reason);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Cleared OBS .sentinel folder to prevent Safe Mode dialog")]
    static partial void LogSentinelFolderCleared(ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Failed to clear OBS .sentinel folder")]
    static partial void LogSentinelFolderClearFailed(ILogger logger, Exception ex);

    #endregion
}
