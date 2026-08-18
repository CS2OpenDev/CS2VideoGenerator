namespace Cs2VideoGenerator.Core.Platform;

/// <summary>
///     Platform-aware CS2 directory layout abstraction used by plugin install/backup routines.
/// </summary>
public interface ICs2DirectoryPaths
{
    /// <summary>
    ///     Full path to the gameinfo.gi file that controls server search paths.
    /// </summary>
    string Cs2GameInfoFilePath { get; }

    /// <summary>
    ///     Full path to the CSVG server plugin binary (server.dll / server.so).
    /// </summary>
    string CsvgPluginServerDllPath { get; }

    /// <summary>
    ///     Directory containing the plugin binary and dependencies.
    /// </summary>
    string CsvgPluginBinDirectory { get; }

    /// <summary>
    ///     Root directory under csgo where CSVG plugin files live.
    /// </summary>
    string CsvgPluginDirectory { get; }

    /// <summary>
    ///     The csgo game files directory for the installation.
    /// </summary>
    string CsgoGameFilesDirectory { get; }
}
