namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Provides an interface for classes that handle the installation and uninstallation
///     of the CSVG plugin into a local CS2 installation.
/// </summary>
/// <remarks>
///     Synchronous on purpose: these are short local file operations run during session setup, where
///     async I/O buys nothing.
/// </remarks>
public interface IPluginInstaller
{
    /// <summary>
    ///     Finds, validates, and copies all files needed into the local CS2 installation:
    ///     the server plugin (server.dll / server.so) and any associated dependencies.
    /// </summary>
    void InstallPluginFiles();

    /// <summary>
    ///     Updates the gameinfo.gi file to ensure that the csvg plugin is loaded
    ///     when cs2.exe is launched.
    /// </summary>
    void ModifyGameInfoFile();

    /// <summary>
    ///     Removes all files added/modified during <see cref="InstallPluginFiles" />
    /// </summary>
    void UninstallPluginFiles();
}
