namespace Cs2VideoGenerator.Core.Platform;

/// <summary>
///     Path fragments and discovery hints for CS2 installations on Windows.
///     Assumes the standard Steam layout under <c>Program Files (x86)\Steam</c>, a
///     case-insensitive filesystem, and the 64-bit game binaries under <c>game\bin\win64</c>.
/// </summary>
public sealed class WindowsPlatformPathProvider : IPlatformPathProvider
{
    /// <inheritdoc />
    /// <remarks>
    ///     Fallback candidates probed in order (first existing directory wins) when neither an
    ///     explicit root override nor Steam library discovery (registry +
    ///     <c>libraryfolders.vdf</c>) locates the game: the default Steam library on the
    ///     <c>C:</c> drive, then the same <c>Program Files (x86)\Steam</c> layout on
    ///     <c>D:</c> and <c>E:</c> for secondary-drive installs.
    /// </remarks>
    public string[] DefaultInstallationRoots { get; } =
    [
        @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive",
        @"D:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive",
        @"E:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
    ];

    /// <inheritdoc />
    /// <remarks>Always <c>game\csgo</c> on Windows.</remarks>
    public string CsgoRelativePath { get; } = Path.Combine("game", "csgo");

    /// <inheritdoc />
    /// <remarks>Always <c>csvg</c>; the plugin lives at <c>game\csgo\csvg</c>.</remarks>
    public string PluginRelativeDirectory { get; } = "csvg";

    /// <inheritdoc />
    /// <remarks>Always <c>bin</c>; combined with the plugin directory: <c>game\csgo\csvg\bin</c>.</remarks>
    public string PluginBinDirectoryName { get; } = "bin";

    /// <inheritdoc />
    /// <remarks>The Windows server plugin is a native DLL: <c>server.dll</c>.</remarks>
    public string ServerPluginFileName { get; } = "server.dll";

    /// <inheritdoc />
    /// <remarks>The 64-bit game executable at <c>game\bin\win64\cs2.exe</c>.</remarks>
    public string? ExecutableRelativePath { get; } = Path.Combine("game", "bin", "win64", "cs2.exe");

    /// <inheritdoc />
    /// <remarks>
    ///     False: NTFS is case-insensitive by default, so path comparisons ignore case on Windows.
    /// </remarks>
    public bool IsCaseSensitiveFileSystem { get; } = false;
}
