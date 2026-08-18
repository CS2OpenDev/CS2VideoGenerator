#region

using System.Text.RegularExpressions;

#endregion

namespace Cs2VideoGenerator.Core.Steam;

/// <summary>
///     Utilities for interacting with the local Steam installation.
/// </summary>
public static partial class SteamUtils
{
    /// <summary>
    ///     Gets the default Steam installation directories to search for the current platform.
    /// </summary>
    public static IReadOnlyList<string> DefaultSteamPaths
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return
                [
                    @"C:\Program Files (x86)\Steam",
                    @"D:\Program Files (x86)\Steam",
                    @"E:\Program Files (x86)\Steam",
                    @"C:\Steam",
                    @"D:\Steam"
                ];
            }

            if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetEnvironmentVariable("HOME")
                              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return
                [
                    Path.Combine(home, ".local", "share", "Steam"),
                    Path.Combine(home, ".steam", "steam"),
                    Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
                ];
            }

            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return
                [
                    Path.Combine(home, "Library", "Application Support", "Steam")
                ];
            }

            return [];
        }
    }

    private const string Cs2InstallDirectoryName = "Counter-Strike Global Offensive";

    /// <summary>
    ///     Attempts to find the Steam installation directory.
    ///     Consults the OS-registered path first (Windows registry), then the default locations.
    /// </summary>
    /// <param name="registryReader">Optional registry reader for the OS-registered Steam path.</param>
    /// <returns>The Steam installation path, or null if not found.</returns>
    public static string? FindSteamInstallation(ISteamRegistryReader? registryReader = null)
    {
        string? registryPath = registryReader?.GetSteamInstallPath();
        if (!string.IsNullOrWhiteSpace(registryPath) && Directory.Exists(registryPath))
        {
            return registryPath;
        }

        return DefaultSteamPaths.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    ///     Attempts to find the CS2 installation directory by scanning all Steam library folders.
    ///     Discovery order: the OS-registered Steam path (Windows registry), then the default Steam
    ///     locations; each Steam root is expanded into its library folders via
    ///     steamapps/libraryfolders.vdf, and each library is probed for
    ///     steamapps/common/Counter-Strike Global Offensive. Finally the provided fallback roots
    ///     are probed directly.
    /// </summary>
    /// <param name="registryReader">Optional registry reader for the OS-registered Steam path.</param>
    /// <param name="fallbackRoots">Optional CS2 root candidates probed when no library contains CS2.</param>
    /// <param name="steamRootCandidates">
    ///     Steam installation candidates scanned after the registry path.
    ///     Defaults to <see cref="DefaultSteamPaths" />; overridable for tests.
    /// </param>
    /// <returns>The CS2 installation root directory, or null if not found.</returns>
    public static string? FindCs2InstallationDirectory(
        ISteamRegistryReader? registryReader = null,
        IEnumerable<string>? fallbackRoots = null,
        IEnumerable<string>? steamRootCandidates = null)
    {
        List<string> steamRoots = new();

        string? registryPath = registryReader?.GetSteamInstallPath();
        if (!string.IsNullOrWhiteSpace(registryPath))
        {
            steamRoots.Add(registryPath);
        }

        steamRoots.AddRange(steamRootCandidates ?? DefaultSteamPaths);

        foreach (string steamRoot in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(Directory.Exists))
        {
            foreach (string library in GetLibraryFolders(steamRoot))
            {
                string candidate = Path.Combine(library, "steamapps", "common", Cs2InstallDirectoryName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fallbackRoots?.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    ///     Gets all Steam library folders for a Steam installation: the installation itself plus
    ///     any additional libraries listed in its steamapps/libraryfolders.vdf.
    ///     Missing or malformed files are tolerated (the Steam root itself is always returned).
    /// </summary>
    /// <param name="steamPath">The Steam installation directory.</param>
    /// <returns>The library folder paths, starting with <paramref name="steamPath" />.</returns>
    public static IReadOnlyList<string> GetLibraryFolders(string steamPath)
    {
        List<string> libraries = new() { steamPath };

        try
        {
            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                foreach (string library in ParseLibraryFoldersVdf(File.ReadAllText(vdfPath)))
                {
                    if (!libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                    {
                        libraries.Add(library);
                    }
                }
            }
        }
        catch
        {
            // Tolerate unreadable/malformed libraryfolders.vdf — the Steam root itself still counts.
        }

        return libraries;
    }

    /// <summary>
    ///     Parses the content of a Steam libraryfolders.vdf file and extracts the library paths.
    ///     Supports the modern format ("path" keys inside numbered blocks) and the legacy format
    ///     (numbered keys mapping directly to path strings). Malformed content yields an empty list.
    /// </summary>
    /// <param name="content">The raw VDF file content.</param>
    /// <returns>The library paths found, in file order, de-duplicated.</returns>
    public static IReadOnlyList<string> ParseLibraryFoldersVdf(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        List<string> paths = new();

        // Modern format (Steam 2021+): numbered blocks containing a "path" key:
        //   "0" { "path" "C:\\Program Files (x86)\\Steam" ... }
        foreach (Match match in LibraryPathKeyRegex().Matches(content))
        {
            AddLibraryPath(paths, match.Groups["value"].Value);
        }

        // Legacy format: numbered keys mapping directly to a path string:
        //   "1"  "D:\\SteamLibrary"
        // Only used when no modern "path" keys were found — modern files contain numeric
        // app-id-to-size entries (e.g. "731" "550912") that would otherwise false-match.
        if (paths.Count == 0)
        {
            foreach (Match match in LegacyLibraryEntryRegex().Matches(content))
            {
                AddLibraryPath(paths, match.Groups["value"].Value);
            }
        }

        return paths;
    }

    private static void AddLibraryPath(List<string> paths, string rawValue)
    {
        string unescaped = rawValue.Replace(@"\\", @"\").Replace("\\\"", "\"");
        if (!string.IsNullOrWhiteSpace(unescaped) &&
            !paths.Contains(unescaped, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(unescaped);
        }
    }

    /// <summary>
    ///     Gets information about all Steam users that have logged in on this machine.
    /// </summary>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>A list of Steam users, or an empty list if none found.</returns>
    public static IReadOnlyList<SteamUserInfo> GetLoggedInUsers(string? steamPath = null)
    {
        steamPath ??= FindSteamInstallation();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return [];
        }

        string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(loginUsersPath))
        {
            return [];
        }

        try
        {
            string content = File.ReadAllText(loginUsersPath);
            return ParseLoginUsersVdf(content);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Gets the most recently logged in Steam user.
    /// </summary>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The most recent user, or null if none found.</returns>
    public static SteamUserInfo? GetMostRecentUser(string? steamPath = null)
    {
        IReadOnlyList<SteamUserInfo> users = GetLoggedInUsers(steamPath);

        SteamUserInfo? mostRecent = users.FirstOrDefault(u => u.IsMostRecent);
        if (mostRecent != null)
        {
            return mostRecent;
        }

        // no MostRecent flag anywhere: fall back on the latest timestamp
        return users
                   .Where(u => u.LastLoginTimestamp.HasValue)
                   .OrderByDescending(u => u.LastLoginTimestamp)
                   .FirstOrDefault()
               ?? users.FirstOrDefault();
    }

    /// <summary>
    ///     Gets a Steam user by their SteamID64.
    /// </summary>
    /// <param name="steamId64">The 64-bit Steam ID to search for.</param>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The user info, or null if not found.</returns>
    public static SteamUserInfo? GetUserBySteamId(long steamId64, string? steamPath = null)
    {
        IReadOnlyList<SteamUserInfo> users = GetLoggedInUsers(steamPath);
        return users.FirstOrDefault(u => u.SteamId64 == steamId64);
    }

    /// <summary>
    ///     Gets a Steam user by their account name.
    /// </summary>
    /// <param name="accountName">The account name to search for (case-insensitive).</param>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The user info, or null if not found.</returns>
    public static SteamUserInfo? GetUserByAccountName(string accountName, string? steamPath = null)
    {
        IReadOnlyList<SteamUserInfo> users = GetLoggedInUsers(steamPath);
        return users.FirstOrDefault(u =>
            string.Equals(u.AccountName, accountName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Gets the path to the Steam userdata directory.
    /// </summary>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The userdata directory path, or null if Steam is not found.</returns>
    public static string? GetUserdataDirectory(string? steamPath = null)
    {
        steamPath ??= FindSteamInstallation();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        string userdataPath = Path.Combine(steamPath, "userdata");
        return Directory.Exists(userdataPath) ? userdataPath : null;
    }

    /// <summary>
    ///     Gets the path to a specific user's data directory.
    /// </summary>
    /// <param name="user">The Steam user.</param>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The user's data directory path, or null if not found.</returns>
    public static string? GetUserDataDirectory(SteamUserInfo user, string? steamPath = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        string? userdataPath = GetUserdataDirectory(steamPath);
        if (string.IsNullOrWhiteSpace(userdataPath))
        {
            return null;
        }

        string userPath = Path.Combine(userdataPath, user.AccountId32.ToString());
        return Directory.Exists(userPath) ? userPath : null;
    }

    /// <summary>
    ///     Gets the path to CS2's user configuration directory for a specific user.
    ///     This is where user-specific settings like convars and video settings are stored.
    /// </summary>
    /// <param name="user">The Steam user.</param>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The CS2 config directory path, or null if not found.</returns>
    /// <remarks>
    ///     The path is typically: Steam/userdata/{AccountId32}/730/local/cfg/
    ///     where 730 is CS2's Steam App ID.
    /// </remarks>
    public static string? GetCs2UserConfigDirectory(SteamUserInfo user, string? steamPath = null)
    {
        string? userDataPath = GetUserDataDirectory(user, steamPath);
        if (string.IsNullOrWhiteSpace(userDataPath))
        {
            return null;
        }

        // 730 is CS2's Steam App ID
        string cs2ConfigPath = Path.Combine(userDataPath, "730", "local", "cfg");
        return Directory.Exists(cs2ConfigPath) ? cs2ConfigPath : null;
    }

    /// <summary>
    ///     Gets the CS2 user config directory for the most recently logged in user.
    /// </summary>
    /// <param name="steamPath">Optional explicit Steam installation path. If null, auto-discovers.</param>
    /// <returns>The CS2 config directory path for the most recent user, or null if not found.</returns>
    public static string? GetCs2UserConfigDirectoryForCurrentUser(string? steamPath = null)
    {
        SteamUserInfo? user = GetMostRecentUser(steamPath);
        if (user == null)
        {
            return null;
        }

        return GetCs2UserConfigDirectory(user, steamPath);
    }

    private static List<SteamUserInfo> ParseLoginUsersVdf(string content)
    {
        List<SteamUserInfo> users = new();

        // a user block opens with a 17-digit Steam ID
        MatchCollection userBlockMatches = UserBlockRegex().Matches(content);

        foreach (Match match in userBlockMatches)
        {
            if (!long.TryParse(match.Groups["steamid"].Value, out long steamId64))
            {
                continue;
            }

            string blockContent = match.Groups["content"].Value;

            string? accountName = ExtractVdfValue(blockContent, "AccountName");
            if (string.IsNullOrWhiteSpace(accountName))
            {
                continue; // AccountName is required
            }

            string? personaName = ExtractVdfValue(blockContent, "PersonaName");
            string? mostRecentStr = ExtractVdfValue(blockContent, "MostRecent");
            string? timestampStr = ExtractVdfValue(blockContent, "Timestamp");

            DateTimeOffset? timestamp = null;
            if (long.TryParse(timestampStr, out long unixTimestamp) && unixTimestamp > 0)
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
            }

            users.Add(new SteamUserInfo
            {
                SteamId64 = steamId64,
                AccountName = accountName,
                PersonaName = personaName,
                IsMostRecent = mostRecentStr == "1",
                LastLoginTimestamp = timestamp
            });
        }

        return users;
    }

    private static string? ExtractVdfValue(string content, string key)
    {
        // "key"  "value", with flexible whitespace between them
        string pattern = $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"";
        Match match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    // "steamid64" { ... } blocks in loginusers.vdf
    [GeneratedRegex("""
                    "(?<steamid>\d{17})"
                    \s*
                    \{
                    (?<content>[^{}]*(?:\{[^{}]*\}[^{}]*)*)
                    \}
                    """, RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline)]
    private static partial Regex UserBlockRegex();

    // modern libraryfolders.vdf entries: "path"  "value", VDF-escaped
    [GeneratedRegex("""
                    "path"
                    \s+
                    "(?<value>(?:[^"\\]|\\.)*)"
                    """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathKeyRegex();

    // Legacy libraryfolders.vdf entries: "1"  "D:\\SteamLibrary". Numbered keys in the modern
    // format open a block ({) instead of a string value, so only the legacy form matches here.
    [GeneratedRegex("""
                    "\d+"
                    [ \t]+
                    "(?<value>(?:[^"\\]|\\.)*)"
                    """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LegacyLibraryEntryRegex();
}
