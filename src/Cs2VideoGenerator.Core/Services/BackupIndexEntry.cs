namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Represents an entry in the backup index file.
/// </summary>
public record BackupIndexEntry
{
    /// <summary>
    ///     The original path that was backed up.
    /// </summary>
    public required string OriginalPath { get; init; }

    /// <summary>
    ///     When the backup was created.
    /// </summary>
    public DateTimeOffset BackedUpAt { get; init; }
}
