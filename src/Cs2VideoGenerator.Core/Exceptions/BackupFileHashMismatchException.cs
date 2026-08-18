namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     An Exception that is thrown when a backup file's hash does not match the original file's hash
///     after a backup file is created.
/// </summary>
/// <param name="backupFilePath">The path of the .backup file.</param>
/// <param name="originalFilePath">The path of the original file</param>
public class BackupFileHashMismatchException(string backupFilePath, string originalFilePath) : Exception(
    $"The backup file created at `{backupFilePath}` does not match the original file `{originalFilePath}`. Aborting to avoid data loss.");
