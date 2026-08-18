namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     An Exception that is thrown when the server plugin file's hash does not match the source plugin file's hash
/// </summary>
/// <param name="csvgPluginServerDllPath">
///     The path within the CS2 instalation directory where the plugin dll should be
///     placed
/// </param>
/// <param name="serverPluginSourcePath">The path where the Cs2VideoGenerator plugin's server.dll should be copied from</param>
public class ServerPluginHashMismatchException(string csvgPluginServerDllPath, string serverPluginSourcePath)
    : Exception(
        $"The server plugin file at `{csvgPluginServerDllPath}` does not match the source plugin file at `{serverPluginSourcePath}` after copying.");
