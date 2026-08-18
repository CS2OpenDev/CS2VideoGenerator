#region

using System.Diagnostics;
using System.Text.RegularExpressions;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Installs and uninstalls the CSVG server plugin in the CS2 game directory: copies the
///     platform's server plugin binary (external override path or the bundled native asset)
///     into <c>game/csgo/csvg/bin</c> with hash verification, and patches/restores
///     <c>gameinfo.gi</c> so the game loads it. All operations no-op in mock mode.
/// </summary>
/// <param name="paths">Resolved CS2 directory and plugin target paths.</param>
/// <param name="options">Options controlling mock mode and the external plugin path override.</param>
/// <param name="backupManager">Takes backups of files before they are modified.</param>
/// <param name="nativeAssetProvider">Locates the server plugin bundled with the package.</param>
/// <param name="logger">Logger for install/uninstall operations.</param>
public sealed partial class PluginInstaller(
    ICs2DirectoryPaths paths,
    IOptions<Cs2VideoGeneratorOptions> options,
    IBackupManager backupManager,
    INativeAssetProvider nativeAssetProvider,
    ILogger<PluginInstaller> logger)
    : IPluginInstaller
{
    private readonly IBackupManager _backupManager =
        backupManager ?? throw new ArgumentNullException(nameof(backupManager));

    private readonly ILogger<PluginInstaller> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly INativeAssetProvider _nativeAssetProvider =
        nativeAssetProvider ?? throw new ArgumentNullException(nameof(nativeAssetProvider));

    private readonly IOptions<Cs2VideoGeneratorOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly ICs2DirectoryPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <inheritdoc />
    /// <exception cref="CsvgServerPluginNotFoundException">
    ///     If the path provided for where to find the CSVG plugin does not
    ///     exist.
    /// </exception>
    /// <exception cref="ServerPluginHashMismatchException">
    ///     If the hash of the copied plugin file does not match the source
    ///     file.
    /// </exception>
    public void InstallPluginFiles()
    {
        Cs2VideoGeneratorOptions options = _options.Value;

        if (options.IsMockModeEffective)
        {
            LogSkippedMockMode();
            return;
        }

        string? sourcePath = options.ExternalPluginPath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            sourcePath = _nativeAssetProvider.ServerPluginPath;
        }

        using Activity? activity = CsvgTracing.StartActivity("PluginInstaller.Install");
        activity?.SetTag("csvg.plugin.source", sourcePath);
        activity?.SetTag("csvg.plugin.target", _paths.CsvgPluginServerDllPath);

        LogInstallingPluginFiles(sourcePath);

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            LogPluginSourceNotFound(sourcePath);
            throw new CsvgServerPluginNotFoundException(
                $"The Cs2VideoGenerator server plugin file was not found at the expected location: `{sourcePath}`");
        }

        string newServerDllHash = HashCalculator.HashFileContent(sourcePath);

        if (File.Exists(_paths.CsvgPluginServerDllPath))
        {
            string existingServerDllHash = HashCalculator.HashFileContent(_paths.CsvgPluginServerDllPath);
            if (existingServerDllHash == newServerDllHash)
            {
                LogExistingPluginMatches();
                activity?.SetTag("csvg.plugin.skippedCopy", true);
                _backupManager.BackupPath(_paths.CsvgPluginServerDllPath);
                return;
            }

            LogBackingUpExistingPlugin(_paths.CsvgPluginServerDllPath);
            _backupManager.BackupPath(_paths.CsvgPluginServerDllPath);

            if (!Directory.Exists(_paths.CsvgPluginBinDirectory))
            {
                Directory.CreateDirectory(_paths.CsvgPluginBinDirectory);
            }

            LogCopyingNewPlugin(_paths.CsvgPluginServerDllPath);
            File.Copy(sourcePath, _paths.CsvgPluginServerDllPath, true);
        }
        else
        {
            if (!Directory.Exists(_paths.CsvgPluginBinDirectory))
            {
                Directory.CreateDirectory(_paths.CsvgPluginBinDirectory);
            }

            LogCopyingNewPlugin(_paths.CsvgPluginServerDllPath);
            File.Copy(sourcePath, _paths.CsvgPluginServerDllPath);
        }

        string postCopyServerDllHash = HashCalculator.HashFileContent(_paths.CsvgPluginServerDllPath);
        activity?.SetTag("csvg.plugin.hash.new", newServerDllHash);
        activity?.SetTag("csvg.plugin.hash.target", postCopyServerDllHash);
        if (newServerDllHash != postCopyServerDllHash)
        {
            LogHashMismatchAfterCopy(_paths.CsvgPluginServerDllPath);
            throw new ServerPluginHashMismatchException(_paths.CsvgPluginServerDllPath, sourcePath);
        }

        LogPluginInstalled(_paths.CsvgPluginServerDllPath);
    }

    /// <inheritdoc />
    public void ModifyGameInfoFile()
    {
        if (_options.Value.IsMockModeEffective)
        {
            LogSkippedMockMode();
            return;
        }

        using Activity? activity = CsvgTracing.StartActivity("PluginInstaller.ModifyGameInfo");
        activity?.SetTag("csvg.gameinfo.path", _paths.Cs2GameInfoFilePath);

        LogModifyingGameInfo(_paths.Cs2GameInfoFilePath);
        string gameInfoContent = File.ReadAllText(_paths.Cs2GameInfoFilePath);
        Regex regex = Cs2DirectoryUtil.GameInfoSearchPathsRegex();
        Match match = regex.Match(gameInfoContent);

        if (!match.Success)
        {
            throw new GameInfoRegexException(_paths.Cs2GameInfoFilePath, match.Value);
        }

        // Replace only the matched substring once to avoid global replacements
        string currentContentToReplace = match.Value;
        string replacementLine =
            $"{match.Groups[1].Value}Game\tcsgo/csvg{match.Groups[3].Value}{currentContentToReplace}";
        string before = gameInfoContent[..match.Index];
        string after = gameInfoContent[(match.Index + match.Length)..];
        gameInfoContent = before + replacementLine + after;
        File.WriteAllText(_paths.Cs2GameInfoFilePath, gameInfoContent);
        LogGameInfoModified(_paths.Cs2GameInfoFilePath);
        activity?.AddEvent(new ActivityEvent("gameinfo.modified"));
    }

    /// <inheritdoc />
    public void UninstallPluginFiles()
    {
        if (_options.Value.IsMockModeEffective)
        {
            LogSkippedMockMode();
            return;
        }

        LogUninstallingPluginFiles(_paths.CsvgPluginDirectory);
        if (Directory.Exists(_paths.CsvgPluginDirectory))
        {
            Directory.Delete(_paths.CsvgPluginDirectory, true);
            LogPluginFilesRemoved(_paths.CsvgPluginDirectory);
        }
    }

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "Plugin operation skipped — mock mode is enabled")]
    partial void LogSkippedMockMode();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Installing plugin files from {SourcePath}")]
    partial void LogInstallingPluginFiles(string? sourcePath);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Plugin source not found at {SourcePath}")]
    partial void LogPluginSourceNotFound(string? sourcePath);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Existing plugin matches new plugin (hash) — creating backup and skipping copy")]
    partial void LogExistingPluginMatches();

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Backing up existing plugin file before overwrite: {Target}")]
    partial void LogBackingUpExistingPlugin(string target);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Copying new plugin to {Target}")]
    partial void LogCopyingNewPlugin(string target);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Error,
        Message = "Hash mismatch after copying plugin to {Target}")]
    partial void LogHashMismatchAfterCopy(string target);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Plugin installed successfully at {Target}")]
    partial void LogPluginInstalled(string target);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "Modifying gameinfo at {Path}")]
    partial void LogModifyingGameInfo(string path);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "Successfully modified gameinfo at {Path}")]
    partial void LogGameInfoModified(string path);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Uninstalling plugin files at {Path}")]
    partial void LogUninstallingPluginFiles(string path);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Plugin files removed from {Path}")]
    partial void LogPluginFilesRemoved(string path);
}
