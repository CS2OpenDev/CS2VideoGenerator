namespace Cs2VideoGenerator.Core.Cache;

/// <summary>
///     Discovers well-known external paths (CS2 root directory, OBS executable, Steam installation),
///     caching results between runs.
///     <see cref="CachedPathDiscoveryService" /> is the single implementation — all CS2/OBS/Steam
///     path discovery must go through this service rather than duplicating path lists elsewhere.
/// </summary>
public interface IPathDiscoveryService
{
    /// <summary>
    ///     Gets the CS2 root directory, using cache if available or discovering and caching if not.
    /// </summary>
    /// <returns>The CS2 root directory path, or null if not found.</returns>
    string? GetCs2RootDirectory();

    /// <summary>
    ///     Gets the OBS executable path, using cache if available or discovering and caching if not.
    /// </summary>
    /// <returns>The OBS executable path, or null if not found.</returns>
    string? GetObsExecutablePath();

    /// <summary>
    ///     Gets the Steam installation path, using cache if available or discovering and caching if not.
    /// </summary>
    /// <returns>The Steam installation path, or null if not found.</returns>
    string? GetSteamInstallationPath();

    /// <summary>
    ///     Invalidates the cache, forcing re-discovery on next access.
    /// </summary>
    void InvalidateCache();
}
