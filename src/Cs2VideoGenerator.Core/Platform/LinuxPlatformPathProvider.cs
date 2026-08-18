namespace Cs2VideoGenerator.Core.Platform;

/// <summary>
///     Path fragments and discovery hints for CS2 installations on Linux.
///     Covers the three common Steam layouts: native Steam under <c>~/.local/share/Steam</c>, the
///     legacy <c>~/.steam/steam</c> symlink layout, and the Flatpak Steam sandbox. Assumes a
///     case-sensitive filesystem.
/// </summary>
public sealed class LinuxPlatformPathProvider : IPlatformPathProvider
{
    // $HOME when set, so containers and sandboxes are respected. Otherwise the resolved user
    // profile folder.
    private static readonly string Home =
        Environment.GetEnvironmentVariable("HOME") ??
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc />
    /// <remarks>
    ///     Fallback candidates probed in order (first existing directory wins) when neither an
    ///     explicit root override nor Steam library discovery (<c>libraryfolders.vdf</c>)
    ///     locates the game:
    ///     the default native Steam library (<c>~/.local/share/Steam</c>, legacy
    ///     "Counter-Strike Global Offensive" folder name), the same library under the newer
    ///     "Counter-Strike 2" folder name, the legacy <c>~/.steam/steam</c> symlink layout,
    ///     and finally the Flatpak Steam installation under
    ///     <c>~/.var/app/com.valvesoftware.Steam</c>. All entries resolve against $HOME when
    ///     set, so sandboxed/containerized environments are respected.
    /// </remarks>
    public string[] DefaultInstallationRoots { get; } =
    [
        Path.Combine(Home, ".local", "share", "Steam", "steamapps",
            "common", "Counter-Strike Global Offensive"),
        Path.Combine(Home, ".local", "share", "Steam", "steamapps",
            "common", "Counter-Strike 2"),
        Path.Combine(Home, ".steam", "steam", "steamapps", "common",
            "Counter-Strike Global Offensive"),
        Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam",
            ".local", "share", "Steam", "steamapps", "common", "Counter-Strike Global Offensive")
    ];

    /// <inheritdoc />
    /// <remarks>Always <c>game/csgo</c> on Linux.</remarks>
    public string CsgoRelativePath { get; } = Path.Combine("game", "csgo");

    /// <inheritdoc />
    /// <remarks>Always <c>csvg</c>; the plugin lives at <c>game/csgo/csvg</c>.</remarks>
    public string PluginRelativeDirectory { get; } = "csvg";

    /// <inheritdoc />
    /// <remarks>Always <c>bin</c>; combined with the plugin directory: <c>game/csgo/csvg/bin</c>.</remarks>
    public string PluginBinDirectoryName { get; } = "bin";

    /// <inheritdoc />
    /// <remarks>The Linux server plugin is a shared object: <c>server.so</c>.</remarks>
    public string ServerPluginFileName { get; } = "server.so";

    /// <inheritdoc />
    /// <remarks>
    ///     Null: launching the CS2 client executable directly is not supported on Linux, so no
    ///     executable path is published (CS2 on Linux runs through Steam/Proton tooling).
    /// </remarks>
    public string? ExecutableRelativePath { get; } = null;

    /// <inheritdoc />
    /// <remarks>
    ///     True: Linux filesystems are case-sensitive, so path comparisons must use ordinal
    ///     (case-sensitive) semantics.
    /// </remarks>
    public bool IsCaseSensitiveFileSystem { get; } = true;
}
