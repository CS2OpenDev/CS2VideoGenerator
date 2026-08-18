namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Configuration specific to OBS capture
/// </summary>
public record ObsCaptureConfiguration : CaptureConfiguration
{
    /// <summary>
    ///     OBS WebSocket host (default: localhost)
    /// </summary>
    public string Host { get; init; } = "localhost";

    /// <summary>
    ///     OBS WebSocket port (default: 4455)
    /// </summary>
    public int Port { get; init; } = 4455;

    /// <summary>
    ///     OBS WebSocket password (if authentication is enabled)
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    ///     OBS profile name to use
    /// </summary>
    public string? ProfileName { get; init; }

    /// <summary>
    ///     OBS scene collection name to use
    /// </summary>
    public string? SceneCollectionName { get; init; }

    /// <summary>
    ///     Whether to hide OBS window when starting
    /// </summary>
    public bool StartMinimized { get; init; } = true;

    /// <summary>
    ///     Command-line arguments to pass when starting OBS
    /// </summary>
    public string[]? StartupArguments { get; init; }

    /// <summary>
    ///     Whether to shut down OBS when the capture provider is disposed.
    ///     Only applies if OBS was started by this provider (AutoStart = true).
    ///     Default is true - OBS will be closed when the application exits.
    /// </summary>
    public bool ShutdownOnDispose { get; init; } = true;

    /// <summary>
    ///     How long, in milliseconds, to wait between readiness re-checks when OBS has accepted
    ///     the WebSocket connection but is not yet ready to service requests (obs-websocket
    ///     RequestStatus 207). The obs-websocket server begins accepting connections before OBS
    ///     finishes initializing, so the first real request can fail transiently. Default: 5000 ms.
    /// </summary>
    public int ReadinessRetryDelayMs { get; init; } = 5000;

    /// <summary>
    ///     Maximum total time, in milliseconds, to wait for OBS to become ready to service
    ///     requests after the WebSocket connection is established, before giving up. Default:
    ///     30000 ms.
    /// </summary>
    public int ReadinessTimeoutMs { get; init; } = 30000;
}
