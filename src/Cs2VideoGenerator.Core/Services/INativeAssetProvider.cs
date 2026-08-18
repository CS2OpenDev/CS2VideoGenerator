namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Provides access to native assets bundled with the Cs2VideoGenerator package.
/// </summary>
public interface INativeAssetProvider
{
    /// <summary>
    ///     Gets the path to the bundled server plugin (server.dll on Windows, server.so elsewhere).
    /// </summary>
    /// <returns>The absolute path to the server plugin, or null if not found.</returns>
    string? ServerPluginPath { get; }

    /// <summary>
    ///     Gets the path to the bundled mock server executable.
    /// </summary>
    /// <returns>The absolute path to the mock server executable, or null if not found.</returns>
    string? MockServerPath { get; }
}
