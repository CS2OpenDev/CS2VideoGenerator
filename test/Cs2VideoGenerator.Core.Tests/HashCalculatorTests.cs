#region

using System.Security.Cryptography;
using Cs2VideoGenerator.Core.Services;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

public class HashCalculatorTests
{
    [Fact]
    public void HashFileContent_ComputesExpectedHash()
    {
        using TempDir tempDir = new();
        string temp = tempDir.Combine("file.bin");
        File.WriteAllText(temp, "hello world");

        // hash it independently, without leaving a handle open
        byte[] bytes = File.ReadAllBytes(temp);
        using SHA256 sha = SHA256.Create();
        byte[] expectedBytes = sha.ComputeHash(bytes);
        string expected = Convert.ToBase64String(expectedBytes);

        string actual = HashCalculator.HashFileContent(temp);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     Steam or an antivirus scanner can be holding a file open while backup validation hashes it.
    /// </summary>
    [Fact]
    public void HashFileContent_SucceedsWhileFileHeldOpenForRead()
    {
        using TempDir tempDir = new();
        string temp = tempDir.Combine("file.bin");
        File.WriteAllText(temp, "concurrently read");
        string expected = HashCalculator.HashFileContent(temp);

        using FileStream reader = File.Open(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        string actual = HashCalculator.HashFileContent(temp);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HashDirectory_SucceedsWhileFileHeldOpenForRead()
    {
        using TempDir tempDir = new();
        string dir = tempDir.Combine("dir");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "one");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "two");
        string expected = HashCalculator.HashDirectory(dir);

        // concurrent reader on one of the files
        using FileStream reader = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        string actual = HashCalculator.HashDirectory(dir);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HashDirectory_DetectsEqualityAndDifference()
    {
        using TempDir tempDir = new();
        string dirA = tempDir.Combine("dirA");
        string dirB = tempDir.Combine("dirB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        Directory.CreateDirectory(Path.Combine(dirA, "sub"));
        Directory.CreateDirectory(Path.Combine(dirB, "sub"));
        File.WriteAllText(Path.Combine(dirA, "a.txt"), "one");
        File.WriteAllText(Path.Combine(dirB, "a.txt"), "one");
        File.WriteAllText(Path.Combine(dirA, "sub", "b.txt"), "two");
        File.WriteAllText(Path.Combine(dirB, "sub", "b.txt"), "two");

        string hashA = HashCalculator.HashDirectory(dirA);
        string hashB = HashCalculator.HashDirectory(dirB);
        Assert.Equal(hashA, hashB);

        File.WriteAllText(Path.Combine(dirB, "a.txt"), "changed");
        string hashB2 = HashCalculator.HashDirectory(dirB);
        Assert.NotEqual(hashA, hashB2);
    }
}
