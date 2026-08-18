namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     This exception is thrown when a failure to compute the hash for a directory occurs during backup.
/// </summary>
/// <param name="directoryPath">The path to the directory whose hash calculation failed</param>
public class BackupFailedHashComputeException(string directoryPath) : Exception(
    $"A failure to compute the hash for the directory at `{directoryPath}` occurred. Aborting to avoid data lose.");
