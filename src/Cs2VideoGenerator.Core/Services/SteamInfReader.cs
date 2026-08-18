namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Reads the CS2 build number from a <c>steam.inf</c> file (located at
///     <c>game/csgo/steam.inf</c> under the CS2 install root). The <c>ClientVersion</c> value is the
///     build number Steam bumps on every game update - the same value the plugin reports at runtime
///     in the version handshake, which makes it the reference for offline checks (<c>csvg doctor</c>).
/// </summary>
public static class SteamInfReader
{
    /// <summary>
    ///     Returns the path to <c>steam.inf</c> under the given CS2 install root
    ///     ("...\Counter-Strike Global Offensive"), or <see langword="null" /> if it does not exist.
    /// </summary>
    public static string? FindSteamInfPath(string cs2RootDirectory)
    {
        if (string.IsNullOrWhiteSpace(cs2RootDirectory))
        {
            return null;
        }

        string path = Path.Combine(cs2RootDirectory, "game", "csgo", "steam.inf");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     Reads the <c>ClientVersion</c> (CS2 build number) from a steam.inf file.
    ///     Best-effort: returns <see langword="null" /> when the file is missing, unreadable,
    ///     or does not contain the key.
    /// </summary>
    public static string? ReadClientVersion(string steamInfPath)
    {
        try
        {
            if (!File.Exists(steamInfPath))
            {
                return null;
            }

            foreach (string rawLine in File.ReadLines(steamInfPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("ClientVersion=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = line["ClientVersion=".Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
