namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Manages backup and restore of files and directories.
///     Backups are stored both adjacent to the original (with .backup suffix)
///     and in a central backup directory for redundancy.
/// </summary>
/// <remarks>
///     Synchronous on purpose: these are short local file operations run during session setup, where
///     async I/O buys nothing.
/// </remarks>
public interface IBackupManager
{
    /// <summary>
    ///     Gets the path to the central backup directory where copies of all backups are stored.
    /// </summary>
    string CentralBackupDirectory { get; }

    /// <summary>
    ///     Creates a backup of the specified path.
    ///     The backup is stored both adjacent to the original and in the central backup directory.
    /// </summary>
    /// <param name="originalPath">The file or directory to backup.</param>
    void BackupPath(string originalPath);

    /// <summary>
    ///     Restores a file or directory from its backup.
    ///     Prefers the adjacent backup, falls back to central backup if not found.
    /// </summary>
    /// <param name="originalPath">The original path to restore to.</param>
    void RestorePath(string originalPath);

    /// <summary>
    ///     Cleans up old backups from the central backup directory that are older than the specified age.
    /// </summary>
    /// <param name="maxAge">Maximum age of backups to keep.</param>
    void CleanupOldBackups(TimeSpan maxAge);

    /// <summary>
    ///     Returns a snapshot of the backup index mapping backup names to their index entries.
    /// </summary>
    IReadOnlyDictionary<string, BackupIndexEntry> GetBackupIndex();

    /// <summary>
    ///     Deletes specific backups by name, removing both the physical files/directories and index entries.
    /// </summary>
    /// <param name="backupNames">The backup names to delete.</param>
    /// <returns>The number of backups successfully deleted.</returns>
    int DeleteBackups(IEnumerable<string> backupNames);
}
