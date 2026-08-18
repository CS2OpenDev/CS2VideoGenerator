namespace Cs2VideoGenerator.Core.Steam;

/// <summary>
///     Reads the Steam installation path registered with the operating system
///     (the Windows registry). Abstracted behind an interface so it is unit-testable
///     and safely no-ops on platforms without a registry.
/// </summary>
public interface ISteamRegistryReader
{
    /// <summary>
    ///     Gets the Steam installation path registered with the OS,
    ///     or null if unavailable (not registered, non-Windows platform, or access error).
    /// </summary>
    string? GetSteamInstallPath();
}
