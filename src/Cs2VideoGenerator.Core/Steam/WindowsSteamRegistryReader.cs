namespace Cs2VideoGenerator.Core.Steam;

/// <summary>
///     Reads the Steam installation path from the Windows registry
///     (HKCU\Software\Valve\Steam\SteamPath). Returns null on non-Windows platforms.
/// </summary>
public sealed class WindowsSteamRegistryReader : ISteamRegistryReader
{
    private const string SteamRegistryKey = @"HKEY_CURRENT_USER\Software\Valve\Steam";
    private const string SteamPathValueName = "SteamPath";

    /// <inheritdoc />
    public string? GetSteamInstallPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string? path = Microsoft.Win32.Registry.GetValue(SteamRegistryKey, SteamPathValueName, null) as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            // The registry stores forward slashes (e.g. "c:/program files (x86)/steam");
            // normalize to a platform path.
            return Path.GetFullPath(path);
        }
        catch
        {
            // Registry access failure — treat as "not registered".
            return null;
        }
    }
}
