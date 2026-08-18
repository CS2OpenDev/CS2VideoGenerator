#region

using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

public class BackupManagerTests
{
    [Fact]
    public void BackupAndRestore_File()
    {
        using TempDir tempDir = new();
        string file = tempDir.Combine("original.txt");
        File.WriteAllText(file, "original");
        BackupManager mgr = new(new NullLogger<BackupManager>());

        mgr.BackupPath(file);
        string backup = file + ".backup";
        Assert.True(File.Exists(backup));

        File.WriteAllText(file, "modified");
        mgr.RestorePath(file);
        Assert.Equal("original", File.ReadAllText(file));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void BackupAndRestore_Directory()
    {
        using TempDir tempDir = new();
        string dir = tempDir.Combine("data");
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "f.txt"), "1");
        BackupManager mgr = new(new NullLogger<BackupManager>());

        mgr.BackupPath(dir);
        string backup = Path.TrimEndingDirectorySeparator(dir) + ".backup";
        Assert.True(Directory.Exists(backup));

        File.WriteAllText(Path.Combine(sub, "f.txt"), "2");
        mgr.RestorePath(dir);
        Assert.Equal("1", File.ReadAllText(Path.Combine(sub, "f.txt")));
        Assert.False(Directory.Exists(backup));
    }

    /// <summary>
    ///     CS2 adds files to its config directories during a session. Restore has to cope with that:
    ///     remove the extras and still hash-match the backup.
    /// </summary>
    [Fact]
    public void RestorePath_Directory_RemovesExtraFilesAndDirectories()
    {
        using TempDir tempDir = new();
        string dir = tempDir.Combine("data");
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        string backup = Path.TrimEndingDirectorySeparator(dir) + ".backup";

        File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(sub, "f.txt"), "1");
        BackupManager mgr = new(new NullLogger<BackupManager>());

        mgr.BackupPath(dir);
        string backupHash = HashCalculator.HashDirectory(backup);

        // CS2 writing new files mid-session
        File.WriteAllText(Path.Combine(dir, "extra.cfg"), "new");
        File.WriteAllText(Path.Combine(sub, "extra_nested.cfg"), "new");
        string extraDir = Path.Combine(dir, "extra_dir", "deeper");
        Directory.CreateDirectory(extraDir);
        File.WriteAllText(Path.Combine(extraDir, "extra.txt"), "new");
        File.WriteAllText(Path.Combine(sub, "f.txt"), "2");

        mgr.RestorePath(dir);

        Assert.False(File.Exists(Path.Combine(dir, "extra.cfg")));
        Assert.False(File.Exists(Path.Combine(sub, "extra_nested.cfg")));
        Assert.False(Directory.Exists(Path.Combine(dir, "extra_dir")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "keep.txt")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(sub, "f.txt")));

        // hash equality is what restore validation checks; the backup is only deleted once it passes
        Assert.Equal(backupHash, HashCalculator.HashDirectory(dir));
        Assert.False(Directory.Exists(backup));
    }

    /// <summary>
    ///     A stale adjacent backup - left behind when a previous session died before restoring - used to
    ///     wedge every subsequent launch. BackupPath now treats the existing backup as authoritative and
    ///     restores from it instead of throwing.
    /// </summary>
    [Fact]
    public void BackupRestoreCycles_WithInjectedExtraFiles_NeverWedge()
    {
        using TempDir tempDir = new();
        string dir = tempDir.Combine("data");
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        string backup = Path.TrimEndingDirectorySeparator(dir) + ".backup";

        File.WriteAllText(Path.Combine(dir, "settings.cfg"), "user");
        File.WriteAllText(Path.Combine(sub, "video.cfg"), "user");
        BackupManager mgr = new(new NullLogger<BackupManager>());

        for (int cycle = 0; cycle < 3; cycle++)
        {
            // launch: first cycle backs up fresh, later ones find the stale adjacent backup
            mgr.BackupPath(dir);
            Assert.True(Directory.Exists(backup));

            // session: CS2 writes into the directory
            File.WriteAllText(Path.Combine(dir, $"session_{cycle}.cfg"), "written by cs2");
            File.WriteAllText(Path.Combine(sub, $"nested_{cycle}.cfg"), "written by cs2");

            if (cycle < 2)
            {
                // crash/stop where restore never ran, so the stale backup stays behind. The next
                // BackupPath has to self-recover back to the pre-CSVG state.
                mgr.BackupPath(dir);
                Assert.False(File.Exists(Path.Combine(dir, $"session_{cycle}.cfg")));
                Assert.False(File.Exists(Path.Combine(sub, $"nested_{cycle}.cfg")));
            }
            else
            {
                // normal stop
                mgr.RestorePath(dir);
                Assert.False(Directory.Exists(backup));
                Assert.False(File.Exists(Path.Combine(dir, $"session_{cycle}.cfg")));
                Assert.False(File.Exists(Path.Combine(sub, $"nested_{cycle}.cfg")));
            }

            // the user's own config survives every cycle
            Assert.Equal("user", File.ReadAllText(Path.Combine(dir, "settings.cfg")));
            Assert.Equal("user", File.ReadAllText(Path.Combine(sub, "video.cfg")));
        }
    }

    [Fact]
    public void BackupFileAttributeMismatch_Throws()
    {
        using TempDir tempDir = new();
        string file = tempDir.Combine("file.txt");
        File.WriteAllText(file, "x");
        string backupPath = file + ".backup";

        // a directory where the backup file should be
        Directory.CreateDirectory(backupPath);
        BackupManager mgr = new(new NullLogger<BackupManager>());
        Assert.Throws<BackupFileAttributeMismatchException>(() => mgr.BackupPath(file));
    }

    [Fact]
    public void RestorePath_NoBackup_Throws()
    {
        using TempDir tempDir = new();
        string file = tempDir.Combine("file_without_backup.txt");
        File.WriteAllText(file, "content");

        BackupManager mgr = new(new NullLogger<BackupManager>());
        Assert.Throws<FileNotFoundException>(() => mgr.RestorePath(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BackupPath_InvalidPath_Throws(string? path)
    {
        BackupManager mgr = new(new NullLogger<BackupManager>());
        Assert.Throws<ArgumentException>(() => mgr.BackupPath(path!));
    }

    [Fact]
    public void BackupPath_NonExistent_Throws()
    {
        BackupManager mgr = new(new NullLogger<BackupManager>());
        string nonExistentPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "does_not_exist.txt");
        Assert.Throws<FileNotFoundException>(() => mgr.BackupPath(nonExistentPath));
    }

    [Fact]
    public void CentralBackupDirectory_ReturnsValidPath()
    {
        BackupManager mgr = new(new NullLogger<BackupManager>());
        string centralDir = mgr.CentralBackupDirectory;

        Assert.False(string.IsNullOrWhiteSpace(centralDir));
        Assert.Contains("Cs2VideoGenerator", centralDir);
        Assert.Contains("backups", centralDir);
    }

    [Fact]
    public void CleanupOldBackups_EmptyDirectory_DoesNotThrow()
    {
        BackupManager mgr = new(new NullLogger<BackupManager>());
        Exception? exception = Record.Exception(() => mgr.CleanupOldBackups(TimeSpan.FromDays(1)));
        Assert.Null(exception);
    }
}
