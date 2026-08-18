#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Platform;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The restore-on-exit safety promise. When CS2 exits on its own - crash or user-close -
///     <see cref="Cs2ProcessManager" /> still restores the backed-up game files (gameinfo.gi,
///     game/core/cfg) and removes the installed plugin, and a later StopAsync is a no-op rather
///     than a second restore. Real <see cref="PluginInstaller" /> against a fake CS2 tree, a real
///     OS process standing in for CS2, in-memory backup manager.
/// </summary>
public class Cs2ProcessManagerRestoreTests : IDisposable
{
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(30);

    private const string OriginalGameInfoContent =
        "GameInfo\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame\tcsgo\n\t\t}\n\t}\n}\n";

    private readonly TempDir _tempRoot = new("csvg-restore-tests-");

    public void Dispose()
    {
        _tempRoot.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExternalCs2Death_RestoresBackups_AndStopAsyncAfterwardIsSafeNoOp()
    {
        TestHarness harness = CreateHarness();
        await using TestableCs2ProcessManager manager = harness.Manager;

        await manager.LaunchAsync(TestContext.Current.CancellationToken);

        // Sanity: while "CS2" is running, the game files are modified.
        string modifiedGameInfo = await File.ReadAllTextAsync(
            harness.GameInfoPath, TestContext.Current.CancellationToken);
        Assert.Contains("csgo/csvg", modifiedGameInfo);
        Assert.True(File.Exists(harness.PluginDllPath));

        // a crash, or the user closing the game
        Process.GetProcessById(manager.ProcessId!.Value).Kill(true);

        using CancellationTokenSource cts = new(ExitWaitTimeout);
        await manager.WaitForExitAsync(cts.Token);

        // Core safety promise: everything restored even though StopAsync was never called.
        string restoredGameInfo = await File.ReadAllTextAsync(
            harness.GameInfoPath, TestContext.Current.CancellationToken);
        Assert.Equal(OriginalGameInfoContent, restoredGameInfo);
        Assert.DoesNotContain("csvg", restoredGameInfo);
        Assert.False(Directory.Exists(harness.PluginDirectory));
        Assert.Equal("original-cfg", await File.ReadAllTextAsync(
            harness.CfgFilePath, TestContext.Current.CancellationToken));

        List<string> restoredAfterExit = harness.BackupManager.SnapshotRestoredPaths();
        Assert.Contains(Path.GetFullPath(harness.GameInfoPath), restoredAfterExit);
        Assert.Contains(Path.GetFullPath(harness.CoreCfgDirectory), restoredAfterExit);

        // StopAsync after the process already died: no throw, no double-restore.
        await manager.StopAsync(TestContext.Current.CancellationToken);
        await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(restoredAfterExit, harness.BackupManager.SnapshotRestoredPaths());
        Assert.Equal(OriginalGameInfoContent, await File.ReadAllTextAsync(
            harness.GameInfoPath, TestContext.Current.CancellationToken));
    }

    /// <summary>The pre-existing path still works, and the exit monitor does not double up on it.</summary>
    [Fact]
    public async Task ExplicitStop_RestoresBackupsExactlyOnce()
    {
        TestHarness harness = CreateHarness();
        await using TestableCs2ProcessManager manager = harness.Manager;

        await manager.LaunchAsync(TestContext.Current.CancellationToken);
        Assert.Contains("csgo/csvg", await File.ReadAllTextAsync(
            harness.GameInfoPath, TestContext.Current.CancellationToken));

        await manager.StopAsync(TestContext.Current.CancellationToken);

        string restoredGameInfo = await File.ReadAllTextAsync(
            harness.GameInfoPath, TestContext.Current.CancellationToken);
        Assert.Equal(OriginalGameInfoContent, restoredGameInfo);
        Assert.False(Directory.Exists(harness.PluginDirectory));

        // Each backed-up path restored exactly once (monitor and StopAsync did not both restore).
        List<string> restored = harness.BackupManager.SnapshotRestoredPaths();
        Assert.Equal(restored.Count, restored.Distinct().Count());
        Assert.Contains(Path.GetFullPath(harness.GameInfoPath), restored);
    }

    #region Harness

    private TestHarness CreateHarness()
    {
        // Fake CS2 installation tree
        string csgoDir = _tempRoot.Combine("game", "csgo");
        string coreCfgDir = _tempRoot.Combine("game", "core", "cfg");
        Directory.CreateDirectory(csgoDir);
        Directory.CreateDirectory(coreCfgDir);

        string gameInfoPath = Path.Combine(csgoDir, "gameinfo.gi");
        File.WriteAllText(gameInfoPath, OriginalGameInfoContent);

        string cfgFilePath = Path.Combine(coreCfgDir, "video.txt");
        File.WriteAllText(cfgFilePath, "original-cfg");

        // Fake plugin binary to install
        string pluginSource = _tempRoot.Combine("server_source.dll");
        File.WriteAllText(pluginSource, "fake-plugin-binary");

        // Empty Steam dir so user-settings discovery finds nothing (keeps the test hermetic)
        string steamDir = _tempRoot.Combine("steam");
        Directory.CreateDirectory(steamDir);

        Cs2VideoGeneratorOptions options = new()
        {
            Cs2RootInstallationDirectory = _tempRoot.Path,
            ExternalPluginPath = pluginSource,
            SteamInstallationDirectory = steamDir
        };
        IOptions<Cs2VideoGeneratorOptions> optionsWrapper = Options.Create(options);

        FakePlatformPathProvider provider = new();
        Cs2DirectoryUtil directoryUtil = new(options, provider);
        InMemoryBackupManager backupManager = new();
        PluginInstaller pluginInstaller = new(directoryUtil, optionsWrapper, backupManager,
            new Mock<INativeAssetProvider>().Object, new NullLogger<PluginInstaller>());

        (string exe, string[] args) = TestProcesses.SleeperCommand();

        TestableCs2ProcessManager manager = new(exe, args, directoryUtil, backupManager,
            pluginInstaller);

        return new TestHarness
        {
            Manager = manager,
            BackupManager = backupManager,
            GameInfoPath = gameInfoPath,
            CfgFilePath = cfgFilePath,
            CoreCfgDirectory = coreCfgDir,
            PluginDirectory = directoryUtil.CsvgPluginDirectory,
            PluginDllPath = directoryUtil.CsvgPluginServerDllPath
        };
    }

    private sealed class TestHarness
    {
        public required TestableCs2ProcessManager Manager { get; init; }
        public required InMemoryBackupManager BackupManager { get; init; }
        public required string GameInfoPath { get; init; }
        public required string CfgFilePath { get; init; }
        public required string CoreCfgDirectory { get; init; }
        public required string PluginDirectory { get; init; }
        public required string PluginDllPath { get; init; }
    }

    /// <summary>
    ///     Cs2ProcessManager with the real backup/install/restore lifecycle, but launching a
    ///     harmless long-lived OS process instead of CS2 and skipping the 5s post-launch delay.
    /// </summary>
    private sealed class TestableCs2ProcessManager(
        string executablePath,
        string[] arguments,
        Cs2DirectoryUtil directoryUtil,
        IBackupManager backupManager,
        IPluginInstaller pluginInstaller)
        : Cs2ProcessManager(directoryUtil, backupManager, pluginInstaller, NullLoggerFactory.Instance)
    {
        protected override TimeSpan PostLaunchDelay => TimeSpan.Zero;

        protected override string? DiscoverExecutable(Cs2LaunchConfiguration config) => executablePath;

        protected override IEnumerable<string> BuildArguments(Cs2LaunchConfiguration config) => arguments;
    }

    private sealed class FakePlatformPathProvider : IPlatformPathProvider
    {
        public string[] DefaultInstallationRoots => [];
        public string CsgoRelativePath => Path.Combine("game", "csgo");
        public string PluginRelativeDirectory => "csvg";
        public string PluginBinDirectoryName => "bin";
        public string ServerPluginFileName => "server.dll";
        public string? ExecutableRelativePath => "cs2";
        public bool IsCaseSensitiveFileSystem => !OperatingSystem.IsWindows();
    }

    /// <summary>
    ///     In-memory IBackupManager: snapshots file/directory content on backup and writes it back
    ///     on restore, recording every restore call so tests can assert restore-exactly-once.
    /// </summary>
    private sealed class InMemoryBackupManager : IBackupManager
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _directoryBackups = new();
        private readonly Dictionary<string, byte[]> _fileBackups = new();
        private readonly Lock _lock = new();
        private readonly List<string> _restoredPaths = [];

        public string CentralBackupDirectory => Path.GetTempPath();

        public void BackupPath(string originalPath)
        {
            string fullPath = Path.GetFullPath(originalPath);
            lock (_lock)
            {
                if (File.Exists(fullPath))
                {
                    _fileBackups[fullPath] = File.ReadAllBytes(fullPath);
                }
                else if (Directory.Exists(fullPath))
                {
                    Dictionary<string, byte[]> files = new();
                    foreach (string file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                    {
                        files[Path.GetRelativePath(fullPath, file)] = File.ReadAllBytes(file);
                    }

                    _directoryBackups[fullPath] = files;
                }
                else
                {
                    throw new FileNotFoundException("Original path not found", originalPath);
                }
            }
        }

        public void RestorePath(string originalPath)
        {
            string fullPath = Path.GetFullPath(originalPath);
            lock (_lock)
            {
                _restoredPaths.Add(fullPath);

                if (_fileBackups.TryGetValue(fullPath, out byte[]? content))
                {
                    File.WriteAllBytes(fullPath, content);
                    return;
                }

                if (_directoryBackups.TryGetValue(fullPath, out Dictionary<string, byte[]>? files))
                {
                    Directory.CreateDirectory(fullPath);
                    foreach ((string relativePath, byte[] bytes) in files)
                    {
                        string target = Path.Combine(fullPath, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, bytes);
                    }

                    return;
                }

                throw new FileNotFoundException("No backup found", originalPath);
            }
        }

        public void CleanupOldBackups(TimeSpan maxAge)
        {
        }

        public IReadOnlyDictionary<string, BackupIndexEntry> GetBackupIndex()
            => new Dictionary<string, BackupIndexEntry>();

        public int DeleteBackups(IEnumerable<string> backupNames) => 0;

        public List<string> SnapshotRestoredPaths()
        {
            lock (_lock)
            {
                return [.. _restoredPaths];
            }
        }
    }

    #endregion
}
