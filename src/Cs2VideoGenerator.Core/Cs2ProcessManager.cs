#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     Real CS2 mode: plugin install and uninstall, backup and restore, and the launch itself.
/// </summary>
public partial class Cs2ProcessManager(
    Cs2DirectoryUtil directoryUtil,
    IBackupManager backupManager,
    IPluginInstaller pluginInstaller,
    ILoggerFactory loggerFactory,
    IPathDiscoveryService? pathDiscoveryService = null)
    : IntegratedProcessLifecycleManager<Cs2LaunchConfiguration>(loggerFactory?.CreateLogger<Cs2ProcessManager>(),
        loggerFactory?.CreateLogger("Cs2VideoGenerator.Core.Cs2Process")),
        ICs2ProcessManager
{
    // what the after-stop teardown has to restore
    private readonly List<string> _backedUpPaths = [];

    private readonly IBackupManager _backupManager =
        backupManager ?? throw new ArgumentNullException(nameof(backupManager));

    private readonly Cs2DirectoryUtil _directoryUtil =
        directoryUtil ?? throw new ArgumentNullException(nameof(directoryUtil));

    private readonly ILogger _logger = loggerFactory?.CreateLogger<Cs2ProcessManager>() ??
                                       throw new ArgumentNullException(nameof(loggerFactory));

    private readonly IPluginInstaller _pluginInstaller =
        pluginInstaller ?? throw new ArgumentNullException(nameof(pluginInstaller));

    #region Private Methods

    private List<string> GetPathsToBackup()
    {
        List<string> paths = new();

        foreach (Cs2GameFileChanges item in _directoryUtil.PathsThatGetModified.Where(f => f.ShouldBeBackedUp))
        {
            paths.Add(item.Path);
        }

        // the user's own CS2 settings, when they exist
        if (_directoryUtil.CanBackupUserSettings)
        {
            string? userConfigDir = _directoryUtil.UserConfigDirectory;
            if (!string.IsNullOrWhiteSpace(userConfigDir))
            {
                LogAddingUserSettingsToBackup(_logger, userConfigDir);
                paths.Add(userConfigDir);
            }
        }
        else
        {
            LogUserSettingsNotAvailable(_logger);
        }

        return paths;
    }

    #endregion

    #region IntegratedProcessLifecycleManager Implementation

    /// <inheritdoc />
    protected override string LogPrefix => "CS2";

    /// <inheritdoc />
    /// <remarks>Five seconds, giving the CS2 client time to initialize before callers proceed.</remarks>
    protected override TimeSpan PostLaunchDelay => TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    /// <remarks>
    ///     Resolves the CS2 executable through <see cref="Cs2DirectoryUtil" /> (explicit
    ///     configuration override, otherwise platform path discovery); the launch configuration
    ///     itself carries no executable path.
    /// </remarks>
    protected override string? DiscoverExecutable(Cs2LaunchConfiguration config) => _directoryUtil.Cs2ExecutablePath;

    /// <inheritdoc />
    /// <remarks>
    ///     Always passes <c>-insecure</c> (no VAC, required for the plugin), <c>-novid</c>, and
    ///     <c>-allow_third_party_software</c> (so OBS game capture can hook the process), then
    ///     window mode (<c>-fullscreen</c> or <c>-sw</c>) and any configured <c>-w</c>/<c>-h</c>
    ///     resolution from the launch configuration.
    /// </remarks>
    protected override IEnumerable<string> BuildArguments(Cs2LaunchConfiguration config)
    {
        yield return "-insecure";
        yield return "-novid";
        yield return "-allow_third_party_software";

        if (config.Fullscreen)
        {
            yield return "-fullscreen";
        }
        else
        {
            yield return "-sw";
        }

        if (config.Width.HasValue)
        {
            yield return "-w";
            yield return config.Width.Value.ToString();
        }

        if (config.Height.HasValue)
        {
            yield return "-h";
            yield return config.Height.Value.ToString();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Backs up every game file the plugin install will modify (gameinfo.gi etc.) plus the
    ///     user's CS2 settings when available, then installs the CSVG server plugin files and
    ///     patches gameinfo.gi. Missing optional backup paths are skipped; any other backup
    ///     failure aborts the launch (and the paths backed up so far are restored by the
    ///     after-stop teardown).
    /// </remarks>
    protected override async Task OnBeforeLaunchAsync(Cs2LaunchConfiguration config,
        CancellationToken cancellationToken)
    {
        _backedUpPaths.Clear();

        List<string> pathsToBackup = GetPathsToBackup();

        foreach (string path in pathsToBackup)
        {
            LogBackingUp(_logger, path);
            try
            {
                _backupManager.BackupPath(path);
                _backedUpPaths.Add(path);
            }
            catch (FileNotFoundException)
            {
                // fine for optional paths like the user settings directory
                LogBackupPathNotFound(_logger, path);
            }
            catch (Exception ex)
            {
                LogBackupError(_logger, path, ex);
                throw;
            }
        }

        _pluginInstaller.InstallPluginFiles();
        _pluginInstaller.ModifyGameInfoFile();

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Uninstalls the CSVG plugin files from the game directory as soon as CS2 exits, whatever
    ///     the exit reason, and records launch metrics. Backup restoration happens separately, in
    ///     <see cref="OnAfterStopAsync" />.
    /// </remarks>
    protected override async Task OnProcessExitedAsync(ProcessExitResult result)
    {
        Cs2LaunchConfiguration? config = CurrentConfig;
        if (config == null)
        {
            return;
        }

        _pluginInstaller.UninstallPluginFiles();
        CsvgMetrics.RecordProcessLaunch(result, mockMode: false);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Restores every path backed up in <see cref="OnBeforeLaunchAsync" /> so the user's
    ///     game files and settings are returned to their pre-session state. Restoration is
    ///     best-effort per path: a failure is logged and the remaining paths are still restored.
    /// </remarks>
    protected override async Task OnAfterStopAsync(CancellationToken cancellationToken)
    {
        LogAttemptingRestore(_logger, _backedUpPaths.Count);

        foreach (string path in _backedUpPaths)
        {
            try
            {
                LogRestoringPath(_logger, path);
                _backupManager.RestorePath(path);
            }
            catch (Exception ex)
            {
                LogRestoreError(_logger, path, ex);
                // Continue restoring other paths even if one fails
            }
        }

        _backedUpPaths.Clear();

        await Task.CompletedTask;
    }

    #endregion

    #region Public API

    /// <summary>
    ///     Launches CS2 with default display options, handling backup/restore and plugin install lifecycle.
    /// </summary>
    public Task LaunchAsync(CancellationToken cancellationToken) =>
        LaunchWithCacheInvalidationAsync(new Cs2LaunchConfiguration(), cancellationToken);

    /// <summary>
    ///     Launches CS2 with the specified display options, handling backup/restore and plugin install lifecycle.
    /// </summary>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    /// <param name="fullscreen">Whether to launch in fullscreen mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task LaunchAsync(int? width, int? height, bool fullscreen, CancellationToken cancellationToken) =>
        LaunchWithCacheInvalidationAsync(
            new Cs2LaunchConfiguration { Width = width, Height = height, Fullscreen = fullscreen },
            cancellationToken);

    // A resolved executable that is not on disk means the cached install root went stale (game moved
    // or uninstalled). Drop the cache so the next run rediscovers.
    private async Task LaunchWithCacheInvalidationAsync(
        Cs2LaunchConfiguration config,
        CancellationToken cancellationToken)
    {
        try
        {
            await LaunchAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            pathDiscoveryService?.InvalidateCache();
            throw;
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Backing up: {Path}")]
    static partial void LogBackingUp(ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Backup path not found (skipping): {Path}")]
    static partial void LogBackupPathNotFound(ILogger logger, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Error backing up {Path}")]
    static partial void LogBackupError(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Attempting to restore {Count} backed up paths...")]
    static partial void LogAttemptingRestore(ILogger logger, int count);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Restoring: {Path}")]
    static partial void LogRestoringPath(ILogger logger, string path);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "Error restoring {Path}")]
    static partial void LogRestoreError(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug,
        Message = "Adding user settings to backup: {Path}")]
    static partial void LogAddingUserSettingsToBackup(ILogger logger, string path);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "User settings not available for backup - user may not have launched CS2")]
    static partial void LogUserSettingsNotAvailable(ILogger logger);

    #endregion
}
