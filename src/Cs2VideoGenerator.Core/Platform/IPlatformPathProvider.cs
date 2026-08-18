namespace Cs2VideoGenerator.Core.Platform;

/// <summary>
///     Provides platform-specific path fragments and discovery hints for CS2 installations.
/// </summary>
public interface IPlatformPathProvider
{
    /// <summary>
    ///     Typical installation root candidates for the platform (used for auto-discovery).
    /// </summary>
    string[] DefaultInstallationRoots { get; }

    /// <summary>
    ///     Relative path from the installation root to the csgo game files directory (e.g. "game/csgo").
    /// </summary>
    string CsgoRelativePath { get; }

    /// <summary>
    ///     Relative path component for the csvg plugin directory under the csgo game files directory (e.g. "csvg").
    /// </summary>
    string PluginRelativeDirectory { get; }

    /// <summary>
    ///     The name of the plugin "bin" directory (typically "bin").
    /// </summary>
    string PluginBinDirectoryName { get; }

    /// <summary>
    ///     The filename of the server plugin shipped for this platform (e.g. "server.dll" or "server.so").
    /// </summary>
    string ServerPluginFileName { get; }

    /// <summary>
    ///     Relative path to the game executable from the install root (may be null or empty when unsupported).
    /// </summary>
    string? ExecutableRelativePath { get; }

    /// <summary>
    ///     Whether the underlying filesystem should be treated as case-sensitive.
    /// </summary>
    bool IsCaseSensitiveFileSystem { get; }
}
