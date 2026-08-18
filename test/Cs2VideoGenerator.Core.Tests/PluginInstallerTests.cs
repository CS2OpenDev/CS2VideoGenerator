#region

using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Platform;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

public class PluginInstallerTests
{
    /// <summary>
    ///     A fake CS2 layout in a temp directory and a <see cref="PluginInstaller" /> wired against
    ///     it: source plugin at <see cref="SourcePath" />, plugin dir <c>csvg/bin/server.dll</c>,
    ///     gameinfo at <see cref="GameInfoPath" />. Tests create the files they need first.
    /// </summary>
    private sealed class InstallerHarness
    {
        public InstallerHarness(TempDir tempDir)
        {
            SourcePath = tempDir.Combine("server_source.dll");
            PluginDirectory = tempDir.Combine("csvg");
            PluginBinDirectory = Path.Combine(PluginDirectory, "bin");
            TargetDllPath = Path.Combine(PluginBinDirectory, "server.dll");
            GameInfoPath = tempDir.Combine("gameinfo.gi");

            Mock<ICs2DirectoryPaths> pathsMock = new();
            pathsMock.SetupGet(p => p.CsvgPluginBinDirectory).Returns(PluginBinDirectory);
            pathsMock.SetupGet(p => p.CsvgPluginDirectory).Returns(PluginDirectory);
            pathsMock.SetupGet(p => p.CsvgPluginServerDllPath).Returns(TargetDllPath);
            pathsMock.SetupGet(p => p.Cs2GameInfoFilePath).Returns(GameInfoPath);

            IOptions<Cs2VideoGeneratorOptions> options =
                Options.Create(new Cs2VideoGeneratorOptions { ExternalPluginPath = SourcePath });

            Installer = new PluginInstaller(pathsMock.Object, options, BackupMock.Object,
                new Mock<INativeAssetProvider>().Object, new NullLogger<PluginInstaller>());
        }

        public PluginInstaller Installer { get; }
        public Mock<IBackupManager> BackupMock { get; } = new();
        public string SourcePath { get; }
        public string PluginDirectory { get; }
        public string PluginBinDirectory { get; }
        public string TargetDllPath { get; }
        public string GameInfoPath { get; }
    }

    [Fact]
    public void InstallPluginFiles_CopiesPlugin_WhenNoExisting()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        File.WriteAllText(harness.SourcePath, "contents-v1");

        harness.Installer.InstallPluginFiles();

        Assert.True(File.Exists(harness.TargetDllPath));
        Assert.Equal("contents-v1", File.ReadAllText(harness.TargetDllPath));

        harness.BackupMock.Verify(b => b.BackupPath(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void InstallPluginFiles_BacksUpAndOverwrites_WhenExisting()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        File.WriteAllText(harness.SourcePath, "new-contents");
        Directory.CreateDirectory(harness.PluginBinDirectory);
        File.WriteAllText(harness.TargetDllPath, "old-contents");

        harness.Installer.InstallPluginFiles();

        harness.BackupMock.Verify(b => b.BackupPath(harness.TargetDllPath), Times.Once);

        Assert.Equal("new-contents", File.ReadAllText(harness.TargetDllPath));
    }

    /// <summary>The csvg directory has to end up in gameinfo.gi's search paths.</summary>
    [Fact]
    public void ModifyGameInfoFile_UpdatesGameInfoSearchPaths()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        // minimum the regex will match: indented Game csgo plus a newline
        File.WriteAllText(harness.GameInfoPath, "prefix\n\tGame csgo\npost\n");

        harness.Installer.ModifyGameInfoFile();

        string modified = File.ReadAllText(harness.GameInfoPath);
        Assert.Contains("Game\tcsgo/csvg", modified);
        Assert.Contains("Game csgo", modified); // original text should still be present (per implementation)
    }

    [Fact]
    public void UninstallPluginFiles_RemovesPluginDirectory()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        Directory.CreateDirectory(harness.PluginBinDirectory);
        File.WriteAllText(harness.TargetDllPath, "dummy");

        harness.Installer.UninstallPluginFiles();

        Assert.False(Directory.Exists(harness.PluginDirectory));
    }

    [Fact]
    public void InstallPluginFiles_Throws_WhenSourceMissing()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        // Do not create the source file

        Assert.Throws<CsvgServerPluginNotFoundException>(() => harness.Installer.InstallPluginFiles());
    }

    /// <summary>Same hash, so the copy is skipped - but the backup is still taken.</summary>
    [Fact]
    public void InstallPluginFiles_SkipsOverwrite_WhenHashesMatch()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        const string content = "same-contents";
        File.WriteAllText(harness.SourcePath, content);
        Directory.CreateDirectory(harness.PluginBinDirectory);
        File.WriteAllText(harness.TargetDllPath, content);

        harness.Installer.InstallPluginFiles();

        harness.BackupMock.Verify(b => b.BackupPath(harness.TargetDllPath), Times.Once);
        Assert.Equal(content, File.ReadAllText(harness.TargetDllPath));
    }

    /// <summary>
    ///     No matching SearchPaths section: throwing beats mangling a file the pattern never found.
    /// </summary>
    [Fact]
    public void ModifyGameInfoFile_Throws_WhenRegexNotFound()
    {
        using TempDir tempDir = new();
        InstallerHarness harness = new(tempDir);
        File.WriteAllText(harness.GameInfoPath, "no matching line here\n");

        Assert.Throws<GameInfoRegexException>(() => harness.Installer.ModifyGameInfoFile());
    }
}
