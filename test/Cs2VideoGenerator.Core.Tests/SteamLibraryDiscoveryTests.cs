#region

using Cs2VideoGenerator.Core.Steam;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Steam library discovery: the libraryfolders.vdf parser and the registry-first /
///     vdf-expanded / hardcoded-fallback discovery order.
/// </summary>
public class SteamLibraryDiscoveryTests
{
    private const string Cs2DirName = "Counter-Strike Global Offensive";

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static Mock<ISteamRegistryReader> RegistryReturning(string? path)
    {
        Mock<ISteamRegistryReader> registry = new();
        registry.Setup(r => r.GetSteamInstallPath()).Returns(path);
        return registry;
    }

    /// <summary>
    ///     Creates steamapps/common/Counter-Strike Global Offensive under the given library root
    ///     and returns the CS2 directory path.
    /// </summary>
    private static string CreateCs2Install(string libraryRoot)
    {
        string cs2Dir = Path.Combine(libraryRoot, "steamapps", "common", Cs2DirName);
        Directory.CreateDirectory(cs2Dir);
        return cs2Dir;
    }

    private static void WriteLibraryFoldersVdf(string steamRoot, params string[] libraryPaths)
    {
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        string entries = string.Join("\n", libraryPaths.Select((p, i) =>
            $"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{p.Replace(@"\", @"\\")}\"\n\t}}"));
        File.WriteAllText(
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n{entries}\n}}\n");
    }

    #region VDF parser (fixture-based)

    [Fact]
    public void ParseLibraryFoldersVdf_ModernFormat_ReturnsAllLibraryPaths()
    {
        string content = File.ReadAllText(Fixture("libraryfolders_multiple.vdf"));

        IReadOnlyList<string> paths = SteamUtils.ParseLibraryFoldersVdf(content);

        Assert.Equal(
            [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\Games\Steam Library"],
            paths);
    }

    [Fact]
    public void ParseLibraryFoldersVdf_LegacyFormat_ReturnsNumberedPaths()
    {
        string content = File.ReadAllText(Fixture("libraryfolders_legacy.vdf"));

        IReadOnlyList<string> paths = SteamUtils.ParseLibraryFoldersVdf(content);

        Assert.Equal([@"D:\SteamLibrary", @"E:\Games\Steam Library"], paths);
    }

    [Fact]
    public void ParseLibraryFoldersVdf_ModernFormat_DoesNotMistakeAppSizesForLegacyPaths()
    {
        // The fixture contains app-id entries like "730" "64163216463" which must not be
        // parsed as legacy library paths.
        string content = File.ReadAllText(Fixture("libraryfolders_multiple.vdf"));

        IReadOnlyList<string> paths = SteamUtils.ParseLibraryFoldersVdf(content);

        Assert.DoesNotContain(paths, p => p.All(char.IsDigit));
    }

    [Fact]
    public void ParseLibraryFoldersVdf_MalformedContent_ReturnsEmpty()
    {
        string content = File.ReadAllText(Fixture("libraryfolders_malformed.vdf"));

        IReadOnlyList<string> paths = SteamUtils.ParseLibraryFoldersVdf(content);

        Assert.Empty(paths);
    }

    [Fact]
    public void ParseLibraryFoldersVdf_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(SteamUtils.ParseLibraryFoldersVdf(null));
        Assert.Empty(SteamUtils.ParseLibraryFoldersVdf(""));
        Assert.Empty(SteamUtils.ParseLibraryFoldersVdf("   \n\t"));
    }

    #endregion

    #region GetLibraryFolders

    [Fact]
    public void GetLibraryFolders_MissingVdf_ReturnsSteamRootOnly()
    {
        using TempDir steamRoot = new();

        IReadOnlyList<string> libraries = SteamUtils.GetLibraryFolders(steamRoot.Path);

        Assert.Equal([steamRoot.Path], libraries);
    }

    [Fact]
    public void GetLibraryFolders_WithVdf_ReturnsRootPlusLibrariesDeduplicated()
    {
        using TempDir steamRoot = new();
        using TempDir library = new();
        // The vdf lists the steam root itself (as Steam does) plus a secondary library.
        WriteLibraryFoldersVdf(steamRoot.Path, steamRoot.Path, library.Path);

        IReadOnlyList<string> libraries = SteamUtils.GetLibraryFolders(steamRoot.Path);

        Assert.Equal([steamRoot.Path, library.Path], libraries);
    }

    [Fact]
    public void GetLibraryFolders_MalformedVdf_ReturnsSteamRootOnly()
    {
        using TempDir steamRoot = new();
        Directory.CreateDirectory(Path.Combine(steamRoot.Path, "steamapps"));
        File.Copy(
            Fixture("libraryfolders_malformed.vdf"),
            Path.Combine(steamRoot.Path, "steamapps", "libraryfolders.vdf"));

        IReadOnlyList<string> libraries = SteamUtils.GetLibraryFolders(steamRoot.Path);

        Assert.Equal([steamRoot.Path], libraries);
    }

    #endregion

    #region Discovery order

    [Fact]
    public void FindCs2InstallationDirectory_FindsCs2InSecondaryLibraryFromVdf()
    {
        using TempDir steamRoot = new();
        using TempDir library = new();
        string cs2Dir = CreateCs2Install(library.Path);
        WriteLibraryFoldersVdf(steamRoot.Path, steamRoot.Path, library.Path);

        string? result = SteamUtils.FindCs2InstallationDirectory(
            RegistryReturning(steamRoot.Path).Object,
            fallbackRoots: [],
            steamRootCandidates: []);

        Assert.Equal(cs2Dir, result);
    }

    [Fact]
    public void FindCs2InstallationDirectory_FindsCs2InRegistrySteamRootItself()
    {
        using TempDir steamRoot = new();
        string cs2Dir = CreateCs2Install(steamRoot.Path);

        string? result = SteamUtils.FindCs2InstallationDirectory(
            RegistryReturning(steamRoot.Path).Object,
            fallbackRoots: [],
            steamRootCandidates: []);

        Assert.Equal(cs2Dir, result);
    }

    [Fact]
    public void FindCs2InstallationDirectory_RegistryRootPreferredOverFallbackRoots()
    {
        using TempDir steamRoot = new();
        using TempDir fallback = new();
        string cs2Dir = CreateCs2Install(steamRoot.Path);

        string? result = SteamUtils.FindCs2InstallationDirectory(
            RegistryReturning(steamRoot.Path).Object,
            fallbackRoots: [fallback.Path],
            steamRootCandidates: []);

        Assert.Equal(cs2Dir, result);
    }

    [Fact]
    public void FindCs2InstallationDirectory_NoSteamRoots_FallsBackToHardcodedRoots()
    {
        using TempDir fallback = new();
        string nonExistent = Path.Combine(Path.GetTempPath(), "csvg-does-not-exist-" + Guid.NewGuid().ToString("N"));

        string? result = SteamUtils.FindCs2InstallationDirectory(
            RegistryReturning(null).Object,
            fallbackRoots: [nonExistent, fallback.Path],
            steamRootCandidates: []);

        Assert.Equal(fallback.Path, result);
    }

    [Fact]
    public void FindCs2InstallationDirectory_MalformedVdfAndNoCs2_FallsBackWithoutThrowing()
    {
        using TempDir steamRoot = new();
        using TempDir fallback = new();
        Directory.CreateDirectory(Path.Combine(steamRoot.Path, "steamapps"));
        File.Copy(
            Fixture("libraryfolders_malformed.vdf"),
            Path.Combine(steamRoot.Path, "steamapps", "libraryfolders.vdf"));

        string? result = SteamUtils.FindCs2InstallationDirectory(
            RegistryReturning(steamRoot.Path).Object,
            fallbackRoots: [fallback.Path],
            steamRootCandidates: []);

        Assert.Equal(fallback.Path, result);
    }

    [Fact]
    public void FindCs2InstallationDirectory_NothingFound_ReturnsNull()
    {
        string? result = SteamUtils.FindCs2InstallationDirectory(
            RegistryReturning(null).Object,
            fallbackRoots: [],
            steamRootCandidates: []);

        Assert.Null(result);
    }

    [Fact]
    public void FindSteamInstallation_PrefersRegistryPath()
    {
        using TempDir steamRoot = new();

        string? result = SteamUtils.FindSteamInstallation(RegistryReturning(steamRoot.Path).Object);

        Assert.Equal(steamRoot.Path, result);
    }

    [Fact]
    public void FindSteamInstallation_RegistryPathToMissingDirectory_IsIgnored()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), "csvg-does-not-exist-" + Guid.NewGuid().ToString("N"));

        string? result = SteamUtils.FindSteamInstallation(RegistryReturning(nonExistent).Object);

        Assert.NotEqual(nonExistent, result);
    }

    #endregion
}
