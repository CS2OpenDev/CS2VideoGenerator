#region

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Platform;
using Cs2VideoGenerator.Core.Steam;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     Represents a CS2 path that may be modified along with whether it should be backed up.
/// </summary>
public record Cs2GameFileChanges(string Path, bool ShouldBeBackedUp);

/// <summary>
///     Resolves CS2 installation paths and plugin target directories for the current platform.
///     Uses provided overrides when supplied and otherwise searches
///     <see cref="IPlatformPathProvider.DefaultInstallationRoots" />.
/// </summary>
public partial class Cs2DirectoryUtil : ICs2DirectoryPaths
{
    private const int Cs2SteamAppId = 730;

    private readonly Cs2VideoGeneratorOptions _options;
    private readonly IPlatformPathProvider _provider;
    private SteamUserInfo? _currentSteamUser;

    private string? _steamInstallationDirectory;
    private string? _userConfigDirectory;
    private bool _userSettingsResolved;

    /// <summary>
    ///     Auto-discovers the Counter-Strike Global Offensive installation by checking the usual
    ///     install locations for the current operating system.
    /// </summary>
    /// <exception cref="CounterStrikeDirectoryNotFoundException">
    ///     None of them exist.
    /// </exception>
    public Cs2DirectoryUtil(Cs2VideoGeneratorOptions options)
        : this(options,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new WindowsPlatformPathProvider()
                : new LinuxPlatformPathProvider())
    {
    }

    /// <summary>
    ///     Takes an explicit platform provider, for dependency injection and for tests.
    /// </summary>
    public Cs2DirectoryUtil(Cs2VideoGeneratorOptions options, IPlatformPathProvider provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        // mock mode never touches a real CS2 install, so there is nothing to validate. temp is the
        // placeholder when no mock server path was given.
        if (options.IsMockModeEffective)
        {
            if (!string.IsNullOrWhiteSpace(options.ExternalMockServerPath))
            {
                CounterStrikeGlobalOffensiveDirectory = Path.GetDirectoryName(options.ExternalMockServerPath)
                                                        ?? Path.GetTempPath();
            }
            else
            {
                CounterStrikeGlobalOffensiveDirectory = Path.GetTempPath();
            }

            return;
        }

        string? explicitRoot = string.IsNullOrWhiteSpace(options.Cs2RootInstallationDirectory)
            ? null
            : options.Cs2RootInstallationDirectory;

        CounterStrikeGlobalOffensiveDirectory =
            explicitRoot ??
            provider.DefaultInstallationRoots.FirstOrDefault(Directory.Exists)
            ?? throw new CounterStrikeDirectoryNotFoundException(provider.DefaultInstallationRoots);
    }

    /// <summary>
    ///     The root installation directory for Counter-Strike 2/Counter-Strike Global Offensive.
    ///     When unset, this is resolved by scanning <see cref="IPlatformPathProvider.DefaultInstallationRoots" /> for an
    ///     existing path.
    /// </summary>
    /// <remarks>
    ///     On Windows this is typically "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global
    ///     Offensive".
    /// </remarks>
    public string CounterStrikeGlobalOffensiveDirectory { get; }

    /// <summary>
    ///     The full path to the cs2 executable.
    /// </summary>
    /// <remarks>
    ///     On Windows this is "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global
    ///     Offensive\\game\\bin\\win64\\cs2.exe" by default.
    ///     In mock mode this returns the mock server path, kept for compatibility. Prefer
    ///     MockCs2ProcessManager directly there.
    /// </remarks>
    /// <exception cref="InvalidOperationException">When executable path cannot be determined.</exception>
    public string Cs2ExecutablePath =>
        _options.ExternalMockServerPath ??
        (!string.IsNullOrWhiteSpace(_provider.ExecutableRelativePath)
            ? Path.Combine(CounterStrikeGlobalOffensiveDirectory, _provider.ExecutableRelativePath)
            : throw new InvalidOperationException(
                "Executable path is not available for the current platform."));

    /// <summary>
    ///     The path to the core cfg directory containing game-wide configuration files.
    /// </summary>
    /// <remarks>On Windows this is "...\Counter-Strike Global Offensive\game\core\cfg" by default</remarks>
    public string CoreCfgDirectory => Path.Combine(CounterStrikeGlobalOffensiveDirectory, "game", "core", "cfg");

    /// <summary>
    ///     Everything the plugin installation touches.
    /// </summary>
    public ICollection<Cs2GameFileChanges> PathsThatGetModified =>
    [
        new(Cs2GameInfoFilePath, true),
        new(CoreCfgDirectory, true),
        new(CsvgPluginDirectory, false)
    ];

    /// <summary>
    ///     Gets the Steam installation directory, if available.
    ///     Uses explicit configuration if set, otherwise auto-discovers.
    /// </summary>
    public string? SteamInstallationDirectory
    {
        get
        {
            if (_steamInstallationDirectory == null)
            {
                _steamInstallationDirectory = !string.IsNullOrWhiteSpace(_options.SteamInstallationDirectory)
                    ? _options.SteamInstallationDirectory
                    : SteamUtils.FindSteamInstallation();
            }

            return _steamInstallationDirectory;
        }
    }

    /// <summary>
    ///     Gets the current (most recently logged in) Steam user, if available.
    /// </summary>
    public SteamUserInfo? CurrentSteamUser
    {
        get
        {
            ResolveUserSettings();
            return _currentSteamUser;
        }
    }

    /// <summary>
    ///     Gets the path to the current user's CS2 config directory, if available.
    ///     This is where user-specific settings like convars and video settings are stored.
    /// </summary>
    /// <remarks>
    ///     The path is typically: Steam/userdata/{AccountId32}/730/local/cfg/
    ///     where 730 is CS2's Steam App ID.
    /// </remarks>
    public string? UserConfigDirectory
    {
        get
        {
            ResolveUserSettings();
            return _userConfigDirectory;
        }
    }

    /// <summary>
    ///     Gets whether user settings can be backed up (i.e., the user config directory exists).
    /// </summary>
    public bool CanBackupUserSettings =>
        !string.IsNullOrWhiteSpace(UserConfigDirectory) && Directory.Exists(UserConfigDirectory);

    /// <summary>
    ///     The platform/system specific path to the CS2 game files directory
    /// </summary>
    /// <remarks>
    ///     On Windows this is "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global
    ///     Offensive\\game\\csgo" by default
    /// </remarks>
    public string CsgoGameFilesDirectory =>
        Path.Combine(CounterStrikeGlobalOffensiveDirectory, _provider.CsgoRelativePath);

    /// <summary>
    ///     The platform/system specific path where the Cs2VideoGenerator plugin files should be placed.
    /// </summary>
    /// <remarks>
    ///     On Windows this is "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global
    ///     Offensive\\game\\csgo\\csvg" by default
    /// </remarks>
    public string CsvgPluginBinDirectory => Path.Combine(CsvgPluginDirectory, _provider.PluginBinDirectoryName);

    /// <summary>
    ///     The directory under the CS2 game files where CSVG plugin assets are placed.
    /// </summary>
    public string CsvgPluginDirectory => Path.Combine(CsgoGameFilesDirectory, _provider.PluginRelativeDirectory);

    /// <summary>
    ///     The path to the gameinfo.gi file that determines the load order of server instances
    ///     We modify this to force the Cs2VideoGenerator plugin to load.
    /// </summary>
    public string Cs2GameInfoFilePath => Path.Combine(CsgoGameFilesDirectory, "gameinfo.gi");

    /// <summary>
    ///     The full path of the server.dll or server.so file that is the core of the Cs2VideoGenerator plugin.
    /// </summary>
    public string CsvgPluginServerDllPath => Path.Combine(CsvgPluginBinDirectory, _provider.ServerPluginFileName);

    // Finds the line in gameinfo.gi's SearchPaths block that gets rewritten so the plugin loads.
    [GeneratedRegex(@"(\t*)(Game\s*csgo)(\s*\n)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    internal static partial Regex GameInfoSearchPathsRegex();

    private void ResolveUserSettings()
    {
        if (_userSettingsResolved)
        {
            return;
        }

        _userSettingsResolved = true;

        string? steamPath = SteamInstallationDirectory;
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return;
        }

        _currentSteamUser = SteamUtils.GetMostRecentUser(steamPath);
        if (_currentSteamUser == null)
        {
            return;
        }

        _userConfigDirectory = SteamUtils.GetCs2UserConfigDirectory(_currentSteamUser, steamPath);
    }

    /// <summary>
    ///     Appends ".backup" to <paramref name="originalFilePath" />. Works for directories as well as
    ///     files.
    /// </summary>
    /// <param name="originalFilePath">The path to the original unmodified file.</param>
    /// <returns>
    ///     The <paramref name="originalFilePath" /> with ".backup" appended to the filename+extension,
    ///     or to the last directory component
    /// </returns>
    /// <throws cref="ArgumentException">
    ///     Thrown if the <paramref name="originalFilePath" /> is null, empty, or whitespace, or if
    ///     trimming trailing directory separators leaves it empty
    /// </throws>
    public static string GetBackupFilePath(string originalFilePath)
    {
        if (string.IsNullOrWhiteSpace(originalFilePath))
        {
            throw new ArgumentException("originalFilePath cannot be null or whitespace", nameof(originalFilePath));
        }

        // drop trailing separators so directories and files are treated the same
        string trimmed = Path.TrimEndingDirectorySeparator(originalFilePath);

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("originalFilePath cannot be empty after trimming directory separators",
                nameof(originalFilePath));
        }

        return $"{trimmed}.backup";
    }
}
