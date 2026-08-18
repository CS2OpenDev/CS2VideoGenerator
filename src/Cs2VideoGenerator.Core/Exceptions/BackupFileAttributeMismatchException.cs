namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     This exception is thrown when the FileAttributes of a backup file do not match the original file.
/// </summary>
/// <param name="backupPath">The path to the backup directory or file</param>
/// <param name="backupAttributes">The attributes of the backup directory or file</param>
/// <param name="originalPath">The path to the original directory or file</param>
/// <param name="originalAttributes">The attributes of the original directory or file</param>
public class BackupFileAttributeMismatchException(
    string backupPath,
    FileAttributes backupAttributes,
    string originalPath,
    FileAttributes originalAttributes)
    : Exception(
        $"The FileAttributes of the backup file `{backupPath}` ({backupAttributes}) do not match the original file `{originalPath}` ({originalAttributes}). Aborting to avoid data loss.");
