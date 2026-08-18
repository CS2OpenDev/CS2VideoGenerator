#region

using Cs2VideoGenerator.Core.Services;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Parsing the CS2 build number out of steam.inf. <c>csvg doctor</c> uses it, and the plugin
///     reads the same file at runtime for the version handshake.
/// </summary>
public class SteamInfReaderTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("csvg-steaminf-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadClientVersion_ParsesClientVersion()
    {
        string path = Path.Combine(_tempDir, "steam.inf");
        // the real file is key=value with crlf
        File.WriteAllText(path,
            "ClientVersion=2000493\r\nServerVersion=2000493\r\nPatchVersion=1.40.4.9\r\nProductName=cs2\r\n");

        Assert.Equal("2000493", SteamInfReader.ReadClientVersion(path));
    }

    [Fact]
    public void ReadClientVersion_MissingKey_ReturnsNull()
    {
        string path = Path.Combine(_tempDir, "steam.inf");
        File.WriteAllText(path, "ProductName=cs2\n");

        Assert.Null(SteamInfReader.ReadClientVersion(path));
    }

    [Fact]
    public void ReadClientVersion_MissingFile_ReturnsNull() =>
        Assert.Null(SteamInfReader.ReadClientVersion(Path.Combine(_tempDir, "does-not-exist.inf")));

    [Fact]
    public void FindSteamInfPath_ReturnsPathUnderGameCsgo()
    {
        string csgoDir = Path.Combine(_tempDir, "game", "csgo");
        Directory.CreateDirectory(csgoDir);
        string steamInf = Path.Combine(csgoDir, "steam.inf");
        File.WriteAllText(steamInf, "ClientVersion=2000493\n");

        Assert.Equal(steamInf, SteamInfReader.FindSteamInfPath(_tempDir));
    }

    [Fact]
    public void FindSteamInfPath_MissingFile_ReturnsNull() =>
        Assert.Null(SteamInfReader.FindSteamInfPath(_tempDir));
}
