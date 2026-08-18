#region

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Cache;

/// <summary>
///     JSON file-based implementation of <see cref="IPathDiscoveryCache" />.
///     Stores discovered paths in a settings.json file in the user's application data folder.
/// </summary>
public partial class PathDiscoveryCache(ILogger<PathDiscoveryCache>? logger = null) : IPathDiscoveryCache
{
    // Non-nullable logger backing field so source-generated log calls never dereference null.
    private readonly ILogger<PathDiscoveryCache> _logger = logger ?? NullLogger<PathDiscoveryCache>.Instance;

    private const string AppFolderName = "Cs2VideoGenerator";
    private const string CacheFileName = "settings.json";
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    /// <remarks>
    ///     Resolved once per process: <c>%LOCALAPPDATA%\Cs2VideoGenerator\settings.json</c> on
    ///     Windows, <c>$XDG_CONFIG_HOME/Cs2VideoGenerator/settings.json</c> (defaulting to
    ///     <c>~/.config</c>) elsewhere.
    /// </remarks>
    public string CacheFilePath { get; } = GetCacheFilePath();

    /// <summary>
    ///     Loads cached path data from disk, validating that cached paths still exist.
    /// </summary>
    /// <returns>The cached data if valid, or null if cache is missing/invalid/stale.</returns>
    public CachedPathData? Load()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                LogCacheFileNotFound();
                return null;
            }

            string json = File.ReadAllText(CacheFilePath);
            CachedPathData? data = JsonSerializer.Deserialize<CachedPathData>(json, JsonOptions);

            if (data == null)
            {
                LogCacheDeserializationFailed();
                return null;
            }

            if (data.SchemaVersion != CurrentSchemaVersion)
            {
                LogCacheSchemaVersionMismatch(data.SchemaVersion, CurrentSchemaVersion);
                Invalidate();
                return null;
            }

            // a path that has since moved makes the whole entry suspect, so the cache goes
            if (!string.IsNullOrEmpty(data.Cs2RootDirectory) && !Directory.Exists(data.Cs2RootDirectory))
            {
                LogCachedPathNoLongerExists("Cs2RootDirectory", data.Cs2RootDirectory);
                Invalidate();
                return null;
            }

            if (!string.IsNullOrEmpty(data.ObsExecutablePath) && !File.Exists(data.ObsExecutablePath))
            {
                LogCachedPathNoLongerExists("ObsExecutablePath", data.ObsExecutablePath);
                Invalidate();
                return null;
            }

            if (!string.IsNullOrEmpty(data.SteamInstallationPath) && !Directory.Exists(data.SteamInstallationPath))
            {
                LogCachedPathNoLongerExists("SteamInstallationPath", data.SteamInstallationPath);
                Invalidate();
                return null;
            }

            LogCacheLoaded(data.Cs2RootDirectory, data.ObsExecutablePath, data.SteamInstallationPath,
                data.DiscoveredAt);
            return data;
        }
        catch (Exception ex)
        {
            LogCacheLoadError(ex);
            return null;
        }
    }

    /// <summary>
    ///     Saves path data to the cache file, creating the directory if necessary.
    /// </summary>
    public void Save(CachedPathData data)
    {
        try
        {
            string? directory = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(CacheFilePath, json);

            LogCacheSaved(CacheFilePath);
        }
        catch (Exception ex)
        {
            LogCacheSaveError(ex);
        }
    }

    /// <summary>
    ///     Deletes the cache file if it exists.
    /// </summary>
    public void Invalidate()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                File.Delete(CacheFilePath);
                LogCacheInvalidated(CacheFilePath);
            }
        }
        catch (Exception ex)
        {
            LogCacheInvalidationError(ex);
        }
    }

    private static string GetCacheFilePath()
    {
        string baseDirectory;

        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA%\Cs2VideoGenerator\settings.json
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            // ~/.config/Cs2VideoGenerator/settings.json
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            baseDirectory = string.IsNullOrEmpty(configHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : configHome;
        }

        return Path.Combine(baseDirectory, AppFolderName, CacheFileName);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Path discovery cache file not found")]
    partial void LogCacheFileNotFound();

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Failed to deserialize path discovery cache")]
    partial void LogCacheDeserializationFailed();

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message =
            "Cache schema version mismatch (found {FoundVersion}, expected {ExpectedVersion}), invalidating cache")]
    partial void LogCacheSchemaVersionMismatch(int foundVersion, int expectedVersion);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Cached path no longer exists: {PathType} = {Path}")]
    partial void LogCachedPathNoLongerExists(string pathType, string path);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message =
            "Path discovery cache loaded: Cs2Root={Cs2Root}, ObsPath={ObsPath}, SteamPath={SteamPath}, DiscoveredAt={DiscoveredAt}")]
    partial void LogCacheLoaded(string? cs2Root, string? obsPath, string? steamPath, DateTimeOffset discoveredAt);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Error loading path discovery cache")]
    partial void LogCacheLoadError(Exception ex);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Debug,
        Message = "Path discovery cache saved to {Path}")]
    partial void LogCacheSaved(string path);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Error saving path discovery cache")]
    partial void LogCacheSaveError(Exception ex);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "Path discovery cache invalidated: {Path}")]
    partial void LogCacheInvalidated(string path);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Error invalidating path discovery cache")]
    partial void LogCacheInvalidationError(Exception ex);
}
