#region

using CommandLine;
using Cs2VideoGenerator.Cli.Tasks;

#endregion

namespace Cs2VideoGenerator.Cli.Options;

/// <summary>
///     Options for tasks that execute CS2 or mock_server (watch, generate).
///     These are the only verbs that host the gRPC endpoint the CS2 plugin connects to;
///     <see cref="Program" /> uses this type as the verb → needs-gRPC marker.
///     Inherits mock mode options from <see cref="Cs2ProcessOptions" />.
/// </summary>
public abstract class CommonOptions : Cs2ProcessOptions, IValidatableOptions
{
    /// <summary>
    ///     Usage errors, exit code 2. Overriders should combine <c>base.Validate()</c> with their own.
    /// </summary>
    public virtual IReadOnlyList<string> Validate()
    {
        List<string> issues = [];

        if (GrpcPort == 0)
        {
            issues.Add("--grpc-port 0 (dynamic port) is not supported: the CS2 plugin and mock server " +
                       "connect to a port fixed at plugin build time (default 50051). " +
                       "Specify a port between 1 and 65535.");
        }
        else if (GrpcPort is < 1 or > 65535)
        {
            issues.Add($"--grpc-port must be between 1 and 65535 (got {GrpcPort}).");
        }

        return issues;
    }
    /// <summary>
    ///     The localhost port the gRPC server listens on for the CS2 plugin / mock server. The
    ///     bundled plugin and mock_server connect to 50051, so anything else needs a plugin build
    ///     targeting that port.
    /// </summary>
    [Option("grpc-port", Required = false, Default = DefaultGrpcPort,
        HelpText = "Port for the gRPC server the CS2 plugin connects to (default: 50051)")]
    [ConfigurationOverride(ConfigurationPath = GrpcPortConfigurationPath)]
    public int GrpcPort { get; set; } = DefaultGrpcPort;

    /// <summary>The default gRPC port, matching the port compiled into the CS2 plugin and mock server.</summary>
    public const int DefaultGrpcPort = 50051;

    public const string GrpcPortConfigurationPath = "Cs2VideoGenerator:GrpcPort";

    /// <summary>
    ///     Proceed even when the plugin/game version pair reported by the handshake is on the
    ///     known-incompatible list. Newer or unknown CS2 versions never block regardless of this
    ///     flag - they only produce a warning (warn-not-block policy).
    /// </summary>
    [Option("force", Required = false, Default = false,
        HelpText = "Proceed even if the plugin/game version pair is on the known-incompatible list")]
    [ConfigurationOverride(ConfigurationPath = ForceIncompatiblePluginConfigurationPath)]
    public bool ForceIncompatiblePlugin { get; set; }

    public const string ForceIncompatiblePluginConfigurationPath = "Cs2VideoGenerator:ForceIncompatiblePlugin";

    /// <summary>
    ///     Opt in to client-side game-event telemetry. Off by default: without this flag the plugin
    ///     installs no game-event hook and forwards nothing. Requires a plugin advertising
    ///     <c>game-event-telemetry</c> (not available on every platform); when it is missing, the
    ///     session logs a warning and proceeds without telemetry.
    /// </summary>
    [Option("game-events", Required = false, Default = false,
        HelpText = "Forward CS2 client-side game events to the host (off by default; observability only)")]
    [ConfigurationOverride(ConfigurationPath = GameEventTelemetryConfigurationPath)]
    public bool GameEvents { get; set; }

    public const string GameEventTelemetryConfigurationPath = "Cs2VideoGenerator:EnableGameEventTelemetry";

    /// <summary>
    ///     Which capture provider records clips. Names are matched ignoring case; when the flag is
    ///     omitted the configuration key (and ultimately the "InEngine" default) decides, so no value is
    ///     injected into configuration for an absent flag.
    /// </summary>
    [Option("capture-provider", Required = false,
        HelpText = "Capture provider to record with: InEngine (default), InEngineHooked " +
                   "(experimental, several times faster, Windows only), or OBS")]
    [ConfigurationOverride(ConfigurationPath = CaptureProviderConfigurationPath)]
    public string? CaptureProvider { get; set; }

    public const string CaptureProviderConfigurationPath = "Cs2VideoGenerator:VideoCaptureProvider";
}
