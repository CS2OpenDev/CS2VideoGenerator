#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Platform;
using Cs2VideoGenerator.Core.Steam;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Cache;

/// <summary>
///     Orchestrates path discovery with caching. Checks cache first, then falls back to fresh discovery.
///     This is the single implementation of CS2/OBS/Steam path discovery
///     (see <see cref="IPathDiscoveryService" />).
/// </summary>
public partial class CachedPathDiscoveryService(
    IPathDiscoveryCache cache,
    IPlatformPathProvider platformPathProvider,
    ILogger<CachedPathDiscoveryService>? logger = null,
    ISteamRegistryReader? steamRegistryReader = null) : IPathDiscoveryService
{
    private readonly IPathDiscoveryCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly object _cacheLock = new();

    // Non-nullable logger backing field so source-generated log calls never dereference null.
    private readonly ILogger<CachedPathDiscoveryService> _logger =
        logger ?? NullLogger<CachedPathDiscoveryService>.Instance;

    private readonly IPlatformPathProvider _platformPathProvider =
        platformPathProvider ?? throw new ArgumentNullException(nameof(platformPathProvider));

    private readonly ISteamRegistryReader _steamRegistryReader =
        steamRegistryReader ?? new WindowsSteamRegistryReader();

    private bool _cacheLoaded;

    private CachedPathData? _loadedCache;

    // Both of these exist so unit tests can stand in for the filesystem during OBS discovery.
    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    internal Func<string?> LinuxObsLocator { get; set; } = FindObsOnLinuxViaWhich;

    /// <summary>
    ///     Creates a discovery service with the default file-backed cache and OS-appropriate
    ///     platform paths. Used when constructing components without dependency injection.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for diagnostics.</param>
    /// <returns>A ready-to-use discovery service.</returns>
    public static CachedPathDiscoveryService CreateDefault(ILoggerFactory? loggerFactory = null) =>
        new(new PathDiscoveryCache(loggerFactory?.CreateLogger<PathDiscoveryCache>()),
            OperatingSystem.IsWindows()
                ? new WindowsPlatformPathProvider()
                : new LinuxPlatformPathProvider(),
            loggerFactory?.CreateLogger<CachedPathDiscoveryService>());

    /// <summary>
    ///     Gets the CS2 root directory, using cache if available or discovering and caching if not.
    /// </summary>
    /// <returns>The CS2 root directory path, or null if not found.</returns>
    public string? GetCs2RootDirectory()
    {
        CachedPathData? cached = GetCachedData();

        if (!string.IsNullOrEmpty(cached?.Cs2RootDirectory))
        {
            LogUsingCachedPath("Cs2RootDirectory", cached.Cs2RootDirectory);
            return cached.Cs2RootDirectory;
        }

        string? discovered = DiscoverCs2RootDirectory();
        if (!string.IsNullOrEmpty(discovered))
        {
            UpdateCache(discovered);
        }

        return discovered;
    }

    /// <summary>
    ///     Gets the OBS executable path, using cache if available or discovering and caching if not.
    /// </summary>
    /// <returns>The OBS executable path, or null if not found.</returns>
    public string? GetObsExecutablePath()
    {
        CachedPathData? cached = GetCachedData();

        if (!string.IsNullOrEmpty(cached?.ObsExecutablePath))
        {
            LogUsingCachedPath("ObsExecutablePath", cached.ObsExecutablePath);
            return cached.ObsExecutablePath;
        }

        string? discovered = DiscoverObsExecutablePath();
        if (!string.IsNullOrEmpty(discovered))
        {
            UpdateCache(obsPath: discovered);
        }

        return discovered;
    }

    /// <summary>
    ///     Gets the Steam installation path, using cache if available or discovering and caching if not.
    /// </summary>
    /// <returns>The Steam installation path, or null if not found.</returns>
    public string? GetSteamInstallationPath()
    {
        CachedPathData? cached = GetCachedData();

        if (!string.IsNullOrEmpty(cached?.SteamInstallationPath))
        {
            LogUsingCachedPath("SteamInstallationPath", cached.SteamInstallationPath);
            return cached.SteamInstallationPath;
        }

        // registry-aware on Windows
        string? discovered = SteamUtils.FindSteamInstallation(_steamRegistryReader);
        if (!string.IsNullOrEmpty(discovered))
        {
            LogPathDiscovered("SteamInstallationPath", discovered);
            UpdateCache(steamPath: discovered);
        }
        else
        {
            LogPathNotFound("SteamInstallationPath");
        }

        return discovered;
    }

    /// <summary>
    ///     Invalidates the cache, forcing re-discovery on next access.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cache.Invalidate();
            _loadedCache = null;
            _cacheLoaded = false;
        }

        LogCacheInvalidated();
    }

    private CachedPathData? GetCachedData()
    {
        lock (_cacheLock)
        {
            if (!_cacheLoaded)
            {
                _loadedCache = _cache.Load();
                _cacheLoaded = true;
            }

            return _loadedCache;
        }
    }

    private void UpdateCache(string? cs2Root = null, string? obsPath = null, string? steamPath = null)
    {
        lock (_cacheLock)
        {
            CachedPathData? existing = _loadedCache;
            CachedPathData newData = new()
            {
                Cs2RootDirectory = cs2Root ?? existing?.Cs2RootDirectory,
                ObsExecutablePath = obsPath ?? existing?.ObsExecutablePath,
                SteamInstallationPath = steamPath ?? existing?.SteamInstallationPath,
                DiscoveredAt = DateTimeOffset.UtcNow,
                SchemaVersion = 1
            };

            _cache.Save(newData);
            _loadedCache = newData;
            _cacheLoaded = true;

            LogCacheUpdated(newData.Cs2RootDirectory, newData.ObsExecutablePath, newData.SteamInstallationPath);
        }
    }

    private string? DiscoverCs2RootDirectory()
    {
        LogDiscoveringPath("Cs2RootDirectory");

        // Registry/default Steam roots expanded via libraryfolders.vdf, then hardcoded fallbacks.
        string? discovered = SteamUtils.FindCs2InstallationDirectory(
            _steamRegistryReader,
            _platformPathProvider.DefaultInstallationRoots);

        if (discovered != null)
        {
            LogPathDiscovered("Cs2RootDirectory", discovered);
        }
        else
        {
            LogPathNotFound("Cs2RootDirectory");
        }

        return discovered;
    }

    internal static IReadOnlyList<string> GetObsCandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin",
                    "64bit", "obs64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio",
                    "bin", "64bit", "obs64.exe")
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/Applications/OBS.app/Contents/MacOS/OBS",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications",
                    "OBS.app", "Contents", "MacOS", "OBS")
            ];
        }

        if (OperatingSystem.IsLinux())
        {
            return ["/usr/bin/obs", "/usr/local/bin/obs"];
        }

        return [];
    }

    private string? DiscoverObsExecutablePath()
    {
        LogDiscoveringPath("ObsExecutablePath");

        string? discovered = GetObsCandidatePaths().FirstOrDefault(path => FileExists(path));

        if (discovered == null && OperatingSystem.IsLinux())
        {
            discovered = LinuxObsLocator();
        }

        if (discovered != null)
        {
            LogPathDiscovered("ObsExecutablePath", discovered);
        }
        else
        {
            LogPathNotFound("ObsExecutablePath");
        }

        return discovered;
    }

    private static string? FindObsOnLinuxViaWhich()
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "obs",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output.Trim();
            }
        }
        catch
        {
            // no which, no obs; caller treats null as not found
        }

        return null;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Using cached path for {PathType}: {Path}")]
    partial void LogUsingCachedPath(string pathType, string path);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Discovering path for {PathType}")]
    partial void LogDiscoveringPath(string pathType);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Discovered {PathType}: {Path}")]
    partial void LogPathDiscovered(string pathType, string path);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "{PathType} not found during discovery")]
    partial void LogPathNotFound(string pathType);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Path discovery cache updated: Cs2Root={Cs2Root}, ObsPath={ObsPath}, SteamPath={SteamPath}")]
    partial void LogCacheUpdated(string? cs2Root, string? obsPath, string? steamPath);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Path discovery cache invalidated")]
    partial void LogCacheInvalidated();
}
