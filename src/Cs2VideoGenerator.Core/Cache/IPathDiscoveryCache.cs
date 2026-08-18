namespace Cs2VideoGenerator.Core.Cache;

/// <summary>
///     Represents cached path discovery data persisted between application runs.
/// </summary>
public record CachedPathData
{
    /// <summary>
    ///     The cached CS2 root installation directory path.
    /// </summary>
    public string? Cs2RootDirectory { get; init; }

    /// <summary>
    ///     The cached OBS executable path.
    /// </summary>
    public string? ObsExecutablePath { get; init; }

    /// <summary>
    ///     The cached Steam installation directory path.
    /// </summary>
    public string? SteamInstallationPath { get; init; }

    /// <summary>
    ///     When the paths were discovered.
    /// </summary>
    public DateTimeOffset DiscoveredAt { get; init; }

    /// <summary>
    ///     Schema version for future migrations.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
///     Interface for persisting and loading discovered paths to avoid repeated discovery on each startup.
/// </summary>
public interface IPathDiscoveryCache
{
    /// <summary>
    ///     The full path to the cache file.
    /// </summary>
    string CacheFilePath { get; }

    /// <summary>
    ///     Loads cached path data from storage. Returns null if cache doesn't exist or is invalid.
    /// </summary>
    CachedPathData? Load();

    /// <summary>
    ///     Saves path data to cache storage.
    /// </summary>
    void Save(CachedPathData data);

    /// <summary>
    ///     Invalidates the cache by deleting the cache file.
    /// </summary>
    void Invalidate();
}
