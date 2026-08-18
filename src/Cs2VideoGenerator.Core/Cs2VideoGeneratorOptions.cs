#region

using System.ComponentModel.DataAnnotations;
using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     Configuration options for the CS2 Video Generator.
/// </summary>
public class Cs2VideoGeneratorOptions : IValidatableObject
{
    /// <summary>
    ///     The configuration section name used for binding.
    /// </summary>
    public const string ConfigurationSectionName = "Cs2VideoGenerator";

    /// <summary>
    ///     When set to <see langword="true" />, enables mock mode for testing without CS2.
    ///     Uses the bundled mock_server executable unless <see cref="ExternalMockServerPath" /> is set.
    ///     In mock mode, no plugin management, backup/restore, or CS2 validation is performed.
    /// </summary>
    public bool MockMode { get; set; }

    /// <summary>
    ///     Optional: Override the bundled mock_server executable with an external path.
    ///     Only used when <see cref="MockMode" /> is true.
    ///     When set, implicitly enables mock mode.
    /// </summary>
    public string? ExternalMockServerPath { get; set; }

    /// <summary>
    ///     The effective mock-mode flag: mock mode is active when <see cref="MockMode" /> is set
    ///     explicitly or implicitly via <see cref="ExternalMockServerPath" />. Use this instead of
    ///     re-deriving the predicate at call sites.
    /// </summary>
    public bool IsMockModeEffective => MockMode || !string.IsNullOrWhiteSpace(ExternalMockServerPath);

    /// <summary>
    ///     Optional: Override the bundled server.dll/server.so plugin with an external path.
    ///     Only used when <see cref="MockMode" /> is false (real CS2 mode).
    /// </summary>
    public string? ExternalPluginPath { get; set; }

    /// <summary>
    ///     The path to the Steam installation directory.
    ///     If not specified, will be auto-discovered from common installation locations.
    ///     Only used when <see cref="MockMode" /> is false.
    /// </summary>
    /// <example>
    ///     Windows: "C:\Program Files (x86)\Steam"
    ///     Linux: "~/.local/share/Steam"
    /// </example>
    public string? SteamInstallationDirectory { get; set; }

    /// <summary>
    ///     The path to the CS2 installation root directory ending with "Counter-Strike Global Offensive".
    ///     Only used when <see cref="MockMode" /> is false.
    /// </summary>
    /// <example>
    ///     Windows: "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike 2\Counter-Strike Global Offensive\"
    ///     Linux: "~/.steam/steam/steamapps/common/Counter-Strike Global Offensive/"
    /// </example>
    public string? Cs2RootInstallationDirectory { get; set; }

    /// <summary>
    ///     TCP port on localhost where the orchestrator's gRPC server (<c>Cs2GameService</c>) is hosted.
    ///     The CS2 plugin and the mock server connect back to this port, and
    ///     <c>CsvgVideoSession.StartAsync</c> probes it before launching CS2 so a host that never
    ///     mapped the gRPC service fails fast instead of timing out.
    ///     Bound from the <c>Cs2VideoGenerator:GrpcPort</c> configuration key; defaults to 50051.
    /// </summary>
    [Range(1, 65535, ErrorMessage = "GrpcPort must be between 1 and 65535.")]
    public int GrpcPort { get; set; } = 50051;

    /// <summary>
    ///     When set to <see langword="true" />, proceed with the session even if the plugin/game
    ///     version pair reported during the handshake is on the known-incompatible list
    ///     (CLI: <c>--force</c>). Newer or unknown CS2 versions never block, whatever this is set to;
    ///     they only warn.
    /// </summary>
    public bool ForceIncompatiblePlugin { get; set; }

    /// <summary>
    ///     Optional: path to a JSON file that replaces the embedded plugin compatibility data
    ///     (reference CS2 build + known-broken version pairs). Useful to pick up a compatibility
    ///     fix without waiting for a package update.
    /// </summary>
    public string? CompatibilityOverridePath { get; set; }

    /// <summary>
    ///     Opt in to client-side game-event telemetry (<c>CsvgVideoSession.GameEventReceived</c>).
    ///     <b>Off by default</b>: nothing is hooked in CS2 and no events are forwarded unless this is
    ///     set (or <c>SetGameEventSubscriptionAsync</c> is called directly). When set, the
    ///     subscription is applied once the plugin handshake lands; a plugin build without the
    ///     <c>game-event-telemetry</c> capability logs a warning and the session proceeds without it.
    /// </summary>
    public bool EnableGameEventTelemetry { get; set; }

    /// <summary>
    ///     Game event names to forward when <see cref="EnableGameEventTelemetry" /> is set; empty or
    ///     null forwards every event. Read at session start only; call
    ///     <c>SetGameEventSubscriptionAsync</c> to change the filter mid-session.
    /// </summary>
    public IReadOnlyList<string>? GameEventNames { get; set; }

    /// <summary>
    ///     Which capture provider records clips: one of <see cref="CaptureProviderNames.All" />,
    ///     matched ignoring case and normalized to the canonical spelling when the options are bound.
    ///     The provider's factory comes from the keyed capture registry, and its own settings are bound
    ///     from <c>Capture:&lt;name&gt;</c>.
    /// </summary>
    [Required(ErrorMessage = "VideoCaptureProvider is required.")]
    public string VideoCaptureProvider { get; set; } = CaptureProviderNames.InEngine;

    /// <summary>
    ///     Where the CLI's <c>watch</c> verb writes finished clips. A relative path resolves against
    ///     the current working directory; null falls back to the working directory itself. The
    ///     bundled configuration sets <c>captures</c>, so ad-hoc recordings collect in one place
    ///     instead of landing wherever the command happened to run. Library callers are unaffected;
    ///     they name their outputs on <see cref="Capture.CaptureMetadata" /> directly, as does a
    ///     compilation's JSON.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    ///     Frame rate the CLI's <c>watch</c> verb records at when <c>--fps</c> is not given.
    ///     Defaults to 30: for the offline in-engine providers every output frame is a real
    ///     rendered frame, so 30 fps renders half as many frames as 60 and captures markedly faster
    ///     while staying smooth enough for review. That is the throughput/quality balance ad-hoc
    ///     highlight capture wants. Pass <c>--fps 60</c> (or set this) for the smoothest result.
    /// </summary>
    /// <remarks>
    ///     Scope is deliberately the interactive <c>watch</c> path only. The library
    ///     <see cref="Capture.CaptureMetadata.FrameRate" /> and the compilation schema's
    ///     <c>frameRate</c> keep their 60 default, so a downstream consumer that never set a frame
    ///     rate sees no change: a committed compilation renders exactly as before.
    /// </remarks>
    [Range(1, 1000, ErrorMessage = "DefaultCaptureFrameRate must be between 1 and 1000.")]
    public int DefaultCaptureFrameRate { get; set; } = 30;

    /// <summary>
    ///     The cross-property checks that data annotations cannot express.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // ExternalMockServerPath implicitly enabling mock mode is handled in PostConfigure, not here
        if (!CaptureProviderNames.IsKnown(VideoCaptureProvider))
        {
            yield return new ValidationResult(
                $"VideoCaptureProvider '{VideoCaptureProvider}' is not a known capture provider. " +
                $"Valid names (case-insensitive): {CaptureProviderNames.Format()}.",
                [nameof(VideoCaptureProvider)]);
        }
    }
}
