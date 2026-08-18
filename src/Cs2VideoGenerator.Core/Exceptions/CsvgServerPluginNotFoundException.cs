namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     An Exception that is thrown when the Cs2VideoGenerator server plugin cannot be found at the provided path
/// </summary>
/// <param name="serverPluginSourcePath">The path where the Cs2VideoGenerator plugin's server.dll should be copied from</param>
public class CsvgServerPluginNotFoundException(string serverPluginSourcePath) : Exception(
    $"The Cs2VideoGenerator server plugin was not found at the provided path: {serverPluginSourcePath}");
