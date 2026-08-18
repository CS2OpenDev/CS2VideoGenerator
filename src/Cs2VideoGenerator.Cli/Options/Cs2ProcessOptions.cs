#region

using CommandLine;

#endregion

namespace Cs2VideoGenerator.Cli.Options;

/// <summary>
///     Base options shared across all tasks that interact with CS2 configuration.
///     Contains mock mode settings that determine which process manager is used.
/// </summary>
public abstract class Cs2ProcessOptions
{
    /// <summary>
    ///     Enable mock mode for testing without CS2.
    ///     Uses the bundled mock_server executable unless --mock-server-path is specified.
    /// </summary>
    [Option("mock", Required = false, Default = false, HelpText = "Enable mock mode for testing without CS2")]
    [ConfigurationOverride(ConfigurationPath = "Cs2VideoGenerator:MockMode")]
    public bool MockMode { get; set; }

    /// <summary>
    ///     Override the bundled mock_server executable with an external path.
    ///     Only used when --mock is enabled. Setting this implicitly enables mock mode.
    /// </summary>
    [Option("mock-server-path", Required = false,
        HelpText = "Override bundled mock_server executable (implies --mock)")]
    [ConfigurationOverride(ConfigurationPath = "Cs2VideoGenerator:ExternalMockServerPath")]
    public string? ExternalMockServerPath { get; set; }

    /// <summary>
    ///     Override the bundled server.dll plugin with an external path.
    ///     Only used in real CS2 mode, i.e. without --mock.
    /// </summary>
    [Option("plugin-path", Required = false,
        HelpText = "Override bundled server.dll plugin (real CS2 mode only)")]
    [ConfigurationOverride(ConfigurationPath = "Cs2VideoGenerator:ExternalPluginPath")]
    public string? ExternalPluginPath { get; set; }
}
