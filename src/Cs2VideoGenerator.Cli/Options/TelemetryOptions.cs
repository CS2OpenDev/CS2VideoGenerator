#region

using OpenTelemetry.Exporter;

#endregion

namespace Cs2VideoGenerator.Cli.Options;

public class TelemetryOptions
{
    /// <summary>
    ///     Authenticates against the OpenObserve endpoint. A secret; store it accordingly.
    /// </summary>
    public string? OpenObserveAccessKey { get; init; }

    /// <summary>
    ///     The OpenObserve endpoint to send telemetry data to (e.g. "https://cloud.openobserve.com:4317")
    /// </summary>
    public string OpenObserveEndpoint { get; init; } = "http://127.0.0.1:4317";

    /// <summary>
    ///     The path to use for logs on the OpenObserve endpoint (e.g. "/v1/logs")
    /// </summary>
    public string? OpenObserveLogPath { get; init; }

    public string? OpenObserveMetricsPath { get; init; }

    public string? OpenObserveTracePath { get; init; }

    public OtlpExportProtocol OtlpExportProtocol { get; init; } = OtlpExportProtocol.Grpc;

    public string ServiceName { get; init; } = "Cs2VideoGenerator.Cli";
}
