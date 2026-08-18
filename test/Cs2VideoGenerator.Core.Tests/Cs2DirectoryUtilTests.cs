#region

using Cs2VideoGenerator.Core.Platform;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

public class Cs2DirectoryUtilTests
{
    [Fact]
    public void GetBackupFilePath_AppendsBackupAndValidatesInput()
    {
        Assert.Throws<ArgumentException>(() => Cs2DirectoryUtil.GetBackupFilePath(null!));
        Assert.Throws<ArgumentException>(() => Cs2DirectoryUtil.GetBackupFilePath("   "));

        string p1 = "/tmp/somefile.txt";
        Assert.Equal("/tmp/somefile.txt.backup", Cs2DirectoryUtil.GetBackupFilePath(p1));

        string p2 = "/tmp/somedirectory/";
        string expected2 = Path.TrimEndingDirectorySeparator(p2) + ".backup";
        Assert.Equal(expected2, Cs2DirectoryUtil.GetBackupFilePath(p2));
    }

    /// <summary>With a mock server path set, construction must not require a real CS2 install.</summary>
    [Fact]
    public void Constructor_WithMockPath_SkipsValidation()
    {
        string mockPath = Path.Combine(Path.GetTempPath(), "mock_cs2", "mock_server.exe");
        Cs2VideoGeneratorOptions options = new()
        {
            ExternalMockServerPath = mockPath, ExternalPluginPath = "/dev/null"
        };

        WindowsPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        Assert.NotNull(util.CounterStrikeGlobalOffensiveDirectory);
        Assert.Equal(mockPath, util.Cs2ExecutablePath);
    }

    [Fact]
    public void Cs2ExecutablePath_WithMockPath_ReturnsMockPath()
    {
        string mockPath = "/tmp/mock_cs2/bin/mock_server";
        Cs2VideoGeneratorOptions options = new()
        {
            ExternalMockServerPath = mockPath, ExternalPluginPath = "/dev/null"
        };

        LinuxPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        Assert.Equal(mockPath, util.Cs2ExecutablePath);
    }

    [Fact]
    public void Constructor_WithMockModeEnabled_SkipsValidation()
    {
        Cs2VideoGeneratorOptions options = new()
        {
            MockMode = true, ExternalPluginPath = "/dev/null"
        };

        LinuxPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        // in mock mode the temp dir stands in for the game directory
        Assert.NotNull(util.CounterStrikeGlobalOffensiveDirectory);
        Assert.Equal(Path.GetTempPath(), util.CounterStrikeGlobalOffensiveDirectory);
    }

    [Fact]
    public void PathsThatGetModified_ReturnsExpectedPaths()
    {
        string mockPath = Path.Combine(Path.GetTempPath(), "mock_cs2", "mock_server.exe");
        Cs2VideoGeneratorOptions options = new()
        {
            ExternalMockServerPath = mockPath, ExternalPluginPath = "/dev/null"
        };

        WindowsPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        ICollection<Cs2GameFileChanges> paths = util.PathsThatGetModified;

        Assert.NotNull(paths);
        Assert.NotEmpty(paths);
        // gameinfo.gi, core cfg, plugin directory
        Assert.True(paths.Count >= 3);
        Assert.Contains(paths, p => p.ShouldBeBackedUp);
    }

    [Fact]
    public void CoreCfgDirectory_ReturnsExpectedPath()
    {
        string mockPath = Path.Combine(Path.GetTempPath(), "mock_cs2", "mock_server.exe");
        Cs2VideoGeneratorOptions options = new()
        {
            ExternalMockServerPath = mockPath, ExternalPluginPath = "/dev/null"
        };

        WindowsPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        string cfgDir = util.CoreCfgDirectory;

        Assert.NotNull(cfgDir);
        Assert.Contains("core", cfgDir);
        Assert.Contains("cfg", cfgDir);
    }

    [Fact]
    public void CanBackupUserSettings_ReturnsBoolean()
    {
        string mockPath = Path.Combine(Path.GetTempPath(), "mock_cs2", "mock_server.exe");
        Cs2VideoGeneratorOptions options = new()
        {
            ExternalMockServerPath = mockPath, ExternalPluginPath = "/dev/null"
        };

        WindowsPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        // true iff the user config directory resolved and exists on disk
        bool canBackup = util.CanBackupUserSettings;
        string? userConfigDirectory = util.UserConfigDirectory;
        bool expected = !string.IsNullOrWhiteSpace(userConfigDirectory) && Directory.Exists(userConfigDirectory);
        Assert.Equal(expected, canBackup);
    }

    [Fact]
    public void CsvgPluginDirectory_ContainsExpectedPath()
    {
        string mockPath = Path.Combine(Path.GetTempPath(), "mock_cs2", "mock_server.exe");
        Cs2VideoGeneratorOptions options = new()
        {
            ExternalMockServerPath = mockPath, ExternalPluginPath = "/dev/null"
        };

        WindowsPlatformPathProvider provider = new();
        Cs2DirectoryUtil util = new(options, provider);

        string pluginDir = util.CsvgPluginDirectory;

        Assert.NotNull(pluginDir);
        Assert.Contains("csvg", pluginDir, StringComparison.OrdinalIgnoreCase);
    }
}
