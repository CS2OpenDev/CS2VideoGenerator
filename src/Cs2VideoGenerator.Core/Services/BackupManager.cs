#region

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Exceptions;
using Microsoft.Extensions.Logging;

#endregion

namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Manages backup and restore of files and directories.
///     Stores backups both adjacent to the original and in a central backup directory.
///     Central backups use short hash-based names with an index file to track original paths.
/// </summary>
public sealed partial class BackupManager : IBackupManager
{
    private const string BackupSuffix = ".backup";
    private const string AppFolderName = "Cs2VideoGenerator";
    private const string BackupsFolderName = "backups";
    private const string IndexFileName = "backup-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _indexLock = new();

    private readonly ILogger<BackupManager> _logger;

    /// <summary>
    ///     Creates the backup manager and eagerly ensures the central backup directory
    ///     (<see cref="CentralBackupDirectory" />) exists, creating it when missing.
    /// </summary>
    /// <param name="logger">Logger for backup/restore operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger" /> is null.</exception>
    public BackupManager(ILogger<BackupManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CentralBackupDirectory = GetCentralBackupDirectory();

        if (!Directory.Exists(CentralBackupDirectory))
        {
            Directory.CreateDirectory(CentralBackupDirectory);
        }
    }

    private string IndexFilePath => Path.Combine(CentralBackupDirectory, IndexFileName);

    /// <inheritdoc />
    public string CentralBackupDirectory { get; }

    /// <inheritdoc />
    public void BackupPath(string originalPath)
    {
        using Activity? activity = CsvgTracing.StartActivity("BackupManager.BackupPath");
        activity?.SetTag("backup.originalPath", originalPath);

        if (string.IsNullOrWhiteSpace(originalPath))
        {
            throw new ArgumentException("originalPath cannot be null or whitespace", nameof(originalPath));
        }

        originalPath = Path.GetFullPath(originalPath);

        string adjacentBackupPath = GetAdjacentBackupPath(originalPath);
        string centralBackupName = GetCentralBackupName(originalPath);
        string centralBackupPath = Path.Combine(CentralBackupDirectory, centralBackupName);

        if (Directory.Exists(originalPath))
        {
            BackupDirectory(originalPath, adjacentBackupPath, centralBackupPath, centralBackupName, activity);
        }
        else if (File.Exists(originalPath))
        {
            BackupFile(originalPath, adjacentBackupPath, centralBackupPath, centralBackupName, activity);
        }
        else
        {
            LogOriginalPathNotFound(originalPath);
            throw new FileNotFoundException("Original path not found", originalPath);
        }

        activity?.SetTag("backup.adjacentPath", adjacentBackupPath);
        activity?.SetTag("backup.centralPath", centralBackupPath);
    }

    /// <inheritdoc />
    public void RestorePath(string originalPath)
    {
        using Activity? activity = CsvgTracing.StartActivity("BackupManager.RestorePath");
        activity?.SetTag("backup.originalPath", originalPath);

        originalPath = Path.GetFullPath(originalPath);

        string adjacentBackupPath = GetAdjacentBackupPath(originalPath);
        string centralBackupName = GetCentralBackupName(originalPath);
        string centralBackupPath = Path.Combine(CentralBackupDirectory, centralBackupName);

        // Try adjacent backup first, then fall back to central backup
        string? backupPath = null;
        bool isDirectory = false;

        if (Directory.Exists(adjacentBackupPath))
        {
            backupPath = adjacentBackupPath;
            isDirectory = true;
        }
        else if (File.Exists(adjacentBackupPath))
        {
            backupPath = adjacentBackupPath;
            isDirectory = false;
        }
        else if (Directory.Exists(centralBackupPath))
        {
            LogUsingCentralBackup(centralBackupPath);
            backupPath = centralBackupPath;
            isDirectory = true;
        }
        else if (File.Exists(centralBackupPath))
        {
            LogUsingCentralBackup(centralBackupPath);
            backupPath = centralBackupPath;
            isDirectory = false;
        }

        if (backupPath == null)
        {
            LogNoBackupFound(adjacentBackupPath, originalPath);
            activity?.AddEvent(new ActivityEvent("backup.notFound"));
            throw new FileNotFoundException($"No backup found at {adjacentBackupPath} or {centralBackupPath}",
                originalPath);
        }

        LogRestoringFromBackup(originalPath, backupPath);

        if (isDirectory)
        {
            // Mirror semantics: the original may have gained files since the backup was taken
            // (CS2 writes new cfg files during a session); those must be removed so the restore
            // produces an exact copy of the backup.
            MirrorDirectory(backupPath, originalPath);
            ValidateMatchingBackupAndOriginal(backupPath, originalPath);

            // Cleanup adjacent backup (keep central for safety)
            if (backupPath == adjacentBackupPath)
            {
                Directory.Delete(adjacentBackupPath, true);
            }
        }
        else
        {
            File.Copy(backupPath, originalPath, true);
            ValidateMatchingBackupAndOriginal(backupPath, originalPath);

            // Cleanup adjacent backup (keep central for safety)
            if (backupPath == adjacentBackupPath)
            {
                File.Delete(adjacentBackupPath);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, BackupIndexEntry> GetBackupIndex()
    {
        lock (_indexLock)
        {
            return LoadIndex();
        }
    }

    /// <inheritdoc />
    public int DeleteBackups(IEnumerable<string> backupNames)
    {
        int deletedCount = 0;
        List<string> deletedNames = new();

        foreach (string name in backupNames)
        {
            string entryPath = Path.Combine(CentralBackupDirectory, name);

            try
            {
                if (Directory.Exists(entryPath))
                {
                    Directory.Delete(entryPath, true);
                    deletedNames.Add(name);
                    deletedCount++;
                }
                else if (File.Exists(entryPath))
                {
                    File.Delete(entryPath);
                    deletedNames.Add(name);
                    deletedCount++;
                }
                else
                {
                    // Physical entry missing - still clean up the index entry
                    deletedNames.Add(name);
                }
            }
            catch (Exception ex)
            {
                LogDeleteBackupError(name, ex);
            }
        }

        if (deletedNames.Count > 0)
        {
            RemoveFromIndex(deletedNames);
        }

        return deletedCount;
    }

    /// <inheritdoc />
    public void CleanupOldBackups(TimeSpan maxAge)
    {
        using Activity? activity = CsvgTracing.StartActivity("BackupManager.CleanupOldBackups");
        activity?.SetTag("backup.maxAge", maxAge.ToString());

        if (!Directory.Exists(CentralBackupDirectory))
        {
            return;
        }

        DateTime cutoffTime = DateTime.UtcNow - maxAge;
        int deletedCount = 0;
        List<string> deletedNames = new();

        try
        {
            foreach (string entry in Directory.GetFileSystemEntries(CentralBackupDirectory))
            {
                string name = Path.GetFileName(entry);

                if (name == IndexFileName)
                {
                    continue;
                }

                DateTime lastWrite = Directory.Exists(entry)
                    ? Directory.GetLastWriteTimeUtc(entry)
                    : File.GetLastWriteTimeUtc(entry);

                if (lastWrite < cutoffTime)
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    deletedNames.Add(name);
                    deletedCount++;
                }
            }

            if (deletedNames.Count > 0)
            {
                RemoveFromIndex(deletedNames);
            }

            LogCleanupComplete(deletedCount, maxAge);
        }
        catch (Exception ex)
        {
            LogCleanupError(ex);
        }
    }

    private void BackupDirectory(string originalPath, string adjacentBackupPath, string centralBackupPath,
        string centralBackupName, Activity? activity)
    {
        LogBackingUpDirectory(originalPath, adjacentBackupPath);

        if (Directory.Exists(adjacentBackupPath))
        {
            LogExistingDirectoryBackupFound(adjacentBackupPath);
            activity?.AddEvent(new ActivityEvent("backup.directory.restore"));

            // A stale backup exists (a previous session did not restore cleanly). The backup
            // represents the user's pre-CSVG state and is authoritative: mirror it onto the
            // original directory (copy + delete extraneous files/dirs) so they match exactly.
            MirrorDirectory(adjacentBackupPath, originalPath);

            try
            {
                ValidateMatchingBackupAndOriginal(adjacentBackupPath, originalPath);
            }
            catch (BackupFileHashMismatchException ex)
            {
                // Self-heal instead of permanently blocking every subsequent launch: the mirror
                // restore above brought the original as close to the backup as possible, so
                // rebuild the backup from the current original state and continue.
                LogStaleBackupSelfHeal(adjacentBackupPath, originalPath, ex);
                activity?.AddEvent(new ActivityEvent("backup.directory.selfHeal"));

                Directory.Delete(adjacentBackupPath, true);
                CopyDirectory(originalPath, adjacentBackupPath, false);
                CreateCentralBackupCopy(originalPath, centralBackupPath, centralBackupName, true);
                ValidateMatchingBackupAndOriginal(adjacentBackupPath, originalPath);
            }

            return;
        }
        else if (File.Exists(adjacentBackupPath))
        {
            LogBackupPathFileForDirectory(adjacentBackupPath, originalPath);
            FileAttributes backupAttributes = File.GetAttributes(adjacentBackupPath);
            FileAttributes originalAttributes = File.GetAttributes(originalPath);
            throw new BackupFileAttributeMismatchException(adjacentBackupPath, backupAttributes, originalPath,
                originalAttributes);
        }
        else
        {
            LogCreatingDirectoryBackup(originalPath, adjacentBackupPath);
            activity?.AddEvent(new ActivityEvent("backup.directory.create"));

            CopyDirectory(originalPath, adjacentBackupPath, false);
            CreateCentralBackupCopy(originalPath, centralBackupPath, centralBackupName, true);
        }

        ValidateMatchingBackupAndOriginal(adjacentBackupPath, originalPath);
    }

    private void BackupFile(string originalPath, string adjacentBackupPath, string centralBackupPath,
        string centralBackupName, Activity? activity)
    {
        LogBackingUpFile(originalPath, adjacentBackupPath);

        if (File.Exists(adjacentBackupPath))
        {
            LogExistingFileBackupFound(adjacentBackupPath);
            activity?.AddEvent(new ActivityEvent("backup.file.restore"));
            File.Copy(adjacentBackupPath, originalPath, true);
        }
        else if (Directory.Exists(adjacentBackupPath))
        {
            LogBackupPathDirectoryForFile(adjacentBackupPath, originalPath);
            activity?.AddEvent(new ActivityEvent("backup.path.typeMismatch"));
            FileAttributes backupAttributes = File.GetAttributes(adjacentBackupPath);
            FileAttributes originalAttributes = File.GetAttributes(originalPath);
            throw new BackupFileAttributeMismatchException(adjacentBackupPath, backupAttributes, originalPath,
                originalAttributes);
        }
        else
        {
            File.Copy(originalPath, adjacentBackupPath);
            CreateCentralBackupCopy(originalPath, centralBackupPath, centralBackupName, false);
        }

        ValidateMatchingBackupAndOriginal(adjacentBackupPath, originalPath);
    }

    private void CreateCentralBackupCopy(string originalPath, string centralBackupPath, string centralBackupName,
        bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                CopyDirectory(originalPath, centralBackupPath, true);
            }
            else
            {
                File.Copy(originalPath, centralBackupPath, true);
            }

            UpdateIndex(centralBackupName, originalPath);

            LogCentralBackupCreated(centralBackupPath);
        }
        catch (Exception ex)
        {
            // Log but don't fail - central backup is a safety net, not critical
            LogCentralBackupError(centralBackupPath, ex);
        }
    }

    /// <summary>
    ///     Generates a short, unique backup name from the original path.
    ///     Format: {8-char-hash}_{filename} (e.g., "a1b2c3d4_gameinfo.gi" or "e5f6g7h8_cfg")
    /// </summary>
    private static string GetCentralBackupName(string originalPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(originalPath);

        string lastComponent = Path.GetFileName(trimmed);
        string hash = ComputeShortHash(trimmed);

        return $"{hash}_{lastComponent}";
    }

    private static string ComputeShortHash(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input.ToLowerInvariant());
        byte[] hashBytes = SHA256.HashData(bytes);

        // Take first 4 bytes (8 hex chars)
        return Convert.ToHexString(hashBytes, 0, 4).ToLowerInvariant();
    }

    private static string GetAdjacentBackupPath(string originalPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(originalPath);
        return $"{trimmed}{BackupSuffix}";
    }

    private static string GetCentralBackupDirectory()
    {
        string baseDirectory;

        if (OperatingSystem.IsWindows())
        {
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            baseDirectory = string.IsNullOrEmpty(configHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : configHome;
        }

        return Path.Combine(baseDirectory, AppFolderName, BackupsFolderName);
    }

    #region Index Management

    private void UpdateIndex(string backupName, string originalPath)
    {
        lock (_indexLock)
        {
            Dictionary<string, BackupIndexEntry> index = LoadIndex();
            index[backupName] = new BackupIndexEntry
            {
                OriginalPath = originalPath, BackedUpAt = DateTimeOffset.UtcNow
            };
            SaveIndex(index);
        }
    }

    private void RemoveFromIndex(IEnumerable<string> backupNames)
    {
        lock (_indexLock)
        {
            Dictionary<string, BackupIndexEntry> index = LoadIndex();
            bool modified = false;

            foreach (string name in backupNames)
            {
                if (index.Remove(name))
                {
                    modified = true;
                }
            }

            if (modified)
            {
                SaveIndex(index);
            }
        }
    }

    private Dictionary<string, BackupIndexEntry> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexFilePath))
            {
                string json = File.ReadAllText(IndexFilePath);
                return JsonSerializer.Deserialize<Dictionary<string, BackupIndexEntry>>(json, JsonOptions)
                       ?? new Dictionary<string, BackupIndexEntry>();
            }
        }
        catch (Exception ex)
        {
            LogIndexLoadError(ex);
        }

        return new Dictionary<string, BackupIndexEntry>();
    }

    private void SaveIndex(Dictionary<string, BackupIndexEntry> index)
    {
        try
        {
            string json = JsonSerializer.Serialize(index, JsonOptions);
            File.WriteAllText(IndexFilePath, json);
        }
        catch (Exception ex)
        {
            LogIndexSaveError(ex);
        }
    }

    #endregion

    #region Validation and Copy

    private void ValidateMatchingBackupAndOriginal(string backupPath, string originalPath)
    {
        using Activity? activity = CsvgTracing.StartActivity("BackupManager.Validate");
        activity?.SetTag("backup.path", backupPath);
        activity?.SetTag("backup.originalPath", originalPath);

        FileAttributes backupAttributes = File.GetAttributes(backupPath);
        FileAttributes originalAttributes = File.GetAttributes(originalPath);

        bool backupIsDirectory = backupAttributes.HasFlag(FileAttributes.Directory);
        bool originalIsDirectory = originalAttributes.HasFlag(FileAttributes.Directory);

        if (backupIsDirectory != originalIsDirectory)
        {
            LogBackupAttributeMismatch(backupPath, originalPath);
            activity?.AddEvent(new ActivityEvent("backup.attributeMismatch"));
            throw new BackupFileAttributeMismatchException(backupPath, backupAttributes, originalPath,
                originalAttributes);
        }

        if (originalIsDirectory)
        {
            string b = HashCalculator.HashDirectory(backupPath);
            string o = HashCalculator.HashDirectory(originalPath);
            if (b != o)
            {
                LogDirectoryHashMismatch(backupPath, originalPath);
                activity?.AddEvent(new ActivityEvent("backup.hashMismatch.directory"));
                throw new BackupFileHashMismatchException(backupPath, originalPath);
            }
        }
        else
        {
            string b = HashCalculator.HashFileContent(backupPath);
            string o = HashCalculator.HashFileContent(originalPath);
            if (b != o)
            {
                LogFileHashMismatch(backupPath, originalPath);
                activity?.AddEvent(new ActivityEvent("backup.hashMismatch.file"));
                throw new BackupFileHashMismatchException(backupPath, originalPath);
            }
        }
    }

    /// <summary>
    ///     Makes <paramref name="targetDir" /> an exact copy of <paramref name="sourceDir" />:
    ///     copies all files from the source (overwriting) and deletes files/directories that
    ///     exist only in the target.
    /// </summary>
    private void MirrorDirectory(string sourceDir, string targetDir)
    {
        using Activity? activity = CsvgTracing.StartActivity("BackupManager.MirrorDirectory");
        activity?.SetTag("backup.sourceDir", sourceDir);
        activity?.SetTag("backup.targetDir", targetDir);

        CopyDirectory(sourceDir, targetDir, true);

        // Delete files present in the target but not in the source
        foreach (string file in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(targetDir, file);
            if (!File.Exists(Path.Combine(sourceDir, relative)))
            {
                File.Delete(file);
                LogExtraneousFileDeleted(file);
            }
        }

        // Delete directories present in the target but not in the source (deepest first)
        foreach (string dir in Directory.GetDirectories(targetDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.Exists(dir))
            {
                // Already removed as part of a deleted parent
                continue;
            }

            string relative = Path.GetRelativePath(targetDir, dir);
            if (!Directory.Exists(Path.Combine(sourceDir, relative)))
            {
                Directory.Delete(dir, true);
                LogExtraneousDirectoryDeleted(dir);
            }
        }
    }

    private void CopyDirectory(string sourceDir, string targetDir, bool overwrite)
    {
        using Activity? activity = CsvgTracing.StartActivity("BackupManager.CopyDirectory");
        activity?.SetTag("backup.sourceDir", sourceDir);
        activity?.SetTag("backup.targetDir", targetDir);
        activity?.SetTag("backup.overwrite", overwrite);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, dir);
            string dest = Path.Combine(targetDir, relative);
            if (!Directory.Exists(dest))
            {
                Directory.CreateDirectory(dest);
            }
        }

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(targetDir, relative);
            string destDir = Path.GetDirectoryName(dest)!;
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(file, dest, overwrite);
            LogFileCopied(file, dest);
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Backing up directory {OriginalPath} to {BackupPath}")]
    partial void LogBackingUpDirectory(string originalPath, string backupPath);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Existing directory backup found at {BackupPath}, restoring onto original")]
    partial void LogExistingDirectoryBackupFound(string backupPath);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Backup path {BackupPath} exists as a file but original {OriginalPath} is a directory")]
    partial void LogBackupPathFileForDirectory(string backupPath, string originalPath);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Creating directory backup from {OriginalPath} to {BackupPath}")]
    partial void LogCreatingDirectoryBackup(string originalPath, string backupPath);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error,
        Message = "Original path for backup not found: {OriginalPath}")]
    partial void LogOriginalPathNotFound(string originalPath);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Backing up file {OriginalPath} to {BackupPath}")]
    partial void LogBackingUpFile(string originalPath, string backupPath);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Existing file backup found at {BackupPath}, restoring onto original")]
    partial void LogExistingFileBackupFound(string backupPath);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error,
        Message = "Backup path {BackupPath} exists as a directory but original {OriginalPath} is a file")]
    partial void LogBackupPathDirectoryForFile(string backupPath, string originalPath);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Restoring {OriginalPath} from backup {BackupPath}")]
    partial void LogRestoringFromBackup(string originalPath, string backupPath);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "No backup found at {BackupPath} for original {OriginalPath}")]
    partial void LogNoBackupFound(string backupPath, string originalPath);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "Backup attribute mismatch: backup={BackupPath} original={OriginalPath}")]
    partial void LogBackupAttributeMismatch(string backupPath, string originalPath);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error,
        Message = "Directory hash mismatch between backup {BackupPath} and original {OriginalPath}")]
    partial void LogDirectoryHashMismatch(string backupPath, string originalPath);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "File hash mismatch between backup {BackupPath} and original {OriginalPath}")]
    partial void LogFileHashMismatch(string backupPath, string originalPath);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
        Message = "Copied {SourceFile} -> {DestFile}")]
    partial void LogFileCopied(string sourceFile, string destFile);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message = "Central backup created at {CentralBackupPath}")]
    partial void LogCentralBackupCreated(string centralBackupPath);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning,
        Message = "Failed to create central backup at {CentralBackupPath}")]
    partial void LogCentralBackupError(string centralBackupPath, Exception ex);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information,
        Message = "Using central backup from {CentralBackupPath}")]
    partial void LogUsingCentralBackup(string centralBackupPath);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information,
        Message = "Backup cleanup complete: deleted {DeletedCount} old backups older than {MaxAge}")]
    partial void LogCleanupComplete(int deletedCount, TimeSpan maxAge);

    [LoggerMessage(EventId = 19, Level = LogLevel.Warning,
        Message = "Error during backup cleanup")]
    partial void LogCleanupError(Exception ex);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "Error loading backup index")]
    partial void LogIndexLoadError(Exception ex);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning,
        Message = "Error saving backup index")]
    partial void LogIndexSaveError(Exception ex);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning,
        Message = "Error deleting backup {BackupName}")]
    partial void LogDeleteBackupError(string backupName, Exception ex);

    [LoggerMessage(EventId = 23, Level = LogLevel.Debug,
        Message = "Deleted extraneous file {FilePath} not present in mirror source")]
    partial void LogExtraneousFileDeleted(string filePath);

    [LoggerMessage(EventId = 24, Level = LogLevel.Debug,
        Message = "Deleted extraneous directory {DirectoryPath} not present in mirror source")]
    partial void LogExtraneousDirectoryDeleted(string directoryPath);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
        Message =
            "Stale backup {BackupPath} still mismatched original {OriginalPath} after mirror restore; rebuilding backup from current state to self-heal")]
    partial void LogStaleBackupSelfHeal(string backupPath, string originalPath, Exception ex);

    #endregion
}

