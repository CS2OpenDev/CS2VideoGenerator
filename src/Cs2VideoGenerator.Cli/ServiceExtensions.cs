#region

using System.Diagnostics;
using Cs2VideoGenerator.Cli.Options;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

#endregion

namespace Cs2VideoGenerator.Cli;

public static class ServiceExtensions
{
    /// <summary>
    ///     Wires up OpenTelemetry from the "Telemetry" section of appsettings.json. Strictly
    ///     opt-in: with no such section (or "Enabled": false) nothing OTLP is registered and there
    ///     is nothing to flush, leaving only the <see cref="ActivitySource" /> and console logging.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns><c>true</c> when telemetry export is enabled; otherwise <c>false</c>.</returns>
    public static bool AddSharedTelemetry(this IHostApplicationBuilder builder)
    {
        IConfigurationSection telemetrySection = builder.Configuration.GetSection("Telemetry");
        bool telemetryEnabled = telemetrySection.Exists() && telemetrySection.GetValue("Enabled", true);

        TelemetryOptions telemetryOptions = telemetrySection.Get<TelemetryOptions>() ?? new TelemetryOptions();

        // Registered either way, because Program and Core resolve it. With telemetry off there are
        // no listeners and StartActivity just returns null.
        builder.Services.AddSingleton(
            new ActivitySource(telemetryOptions.ServiceName, ThisAssembly.AssemblyInformationalVersion));

        builder.Services.AddLogging(loggingBuilder =>
        {
            // Plain single-line console output instead of systemd `<N>`-prefixed lines, and
            // warnings+ only so log records don't interleave with the Spectre console UI.
            // File/OTLP logging behavior is governed separately below.
            loggingBuilder
                .AddSimpleConsole(consoleOptions => consoleOptions.SingleLine = true)
                .AddFilter<ConsoleLoggerProvider>(null, LogLevel.Warning)
                // Host startup failures are reported as a single friendly line by Program;
                // suppress the host's own stack-trace error log. The provider-specific default rule
                // above would otherwise override the category rule in appsettings.json.
                .AddFilter<ConsoleLoggerProvider>("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);

            if (!telemetryEnabled)
            {
                return;
            }

            loggingBuilder
                .AddOpenTelemetry(otelOptions =>
                {
                    otelOptions.IncludeFormattedMessage = true;
                    otelOptions.IncludeScopes = true;
                })
                .Configure(loggerFactoryOptions =>
                {
                    loggerFactoryOptions.ActivityTrackingOptions = ActivityTrackingOptions.SpanId
                                                                   | ActivityTrackingOptions.TraceId
                                                                   | ActivityTrackingOptions.ParentId
                                                                   | ActivityTrackingOptions.Baggage
                                                                   | ActivityTrackingOptions.Tags;
                });
        });

        if (!telemetryEnabled)
        {
            return false;
        }

        Uri baseOpenObserveEndpoint = new(telemetryOptions.OpenObserveEndpoint);

        // gives the telemetry flush room on the way out
        builder.Services.Configure<HostOptions>(options => { options.ShutdownTimeout = TimeSpan.FromSeconds(30); });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .AddService(telemetryOptions.ServiceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    { "environment", builder.Environment.EnvironmentName },
                    { "service.version", ThisAssembly.AssemblyInformationalVersion },
                    { "service.instance.id", Environment.MachineName }
                }))
            .WithLogging(loggingProviderBuilder => loggingProviderBuilder
                .AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(baseOpenObserveEndpoint, telemetryOptions.OpenObserveLogPath);
                    otlpOptions.Headers = string.IsNullOrEmpty(telemetryOptions.OpenObserveAccessKey)
                        ? null
                        : $"Authorization=Basic {telemetryOptions.OpenObserveAccessKey}";
                    otlpOptions.Protocol = telemetryOptions.OtlpExportProtocol;
                }))
            .WithTracing(traceProviderBuilder => traceProviderBuilder
                .AddSource(telemetryOptions.ServiceName, "Cs2videoGenerator.Core")
                .AddGrpcClientInstrumentation(grpcTraceOptions =>
                {
                    grpcTraceOptions.EnrichWithHttpRequestMessage = (activity, request) =>
                    {
                        activity.SetTag("http.client.method", request.Method);
                        activity.SetTag("http.client.url", request.RequestUri?.ToString());
                    };
                    grpcTraceOptions.EnrichWithHttpResponseMessage = (activity, response) =>
                    {
                        activity.SetTag("http.response.status_code", response.StatusCode);
                    };
                })
                .AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(baseOpenObserveEndpoint, telemetryOptions.OpenObserveTracePath);
                    otlpOptions.Headers = string.IsNullOrEmpty(telemetryOptions.OpenObserveAccessKey)
                        ? null
                        : $"Authorization=Basic {telemetryOptions.OpenObserveAccessKey}";
                    otlpOptions.Protocol = telemetryOptions.OtlpExportProtocol;
                }))
            .WithMetrics(metrics => metrics
                .AddMeter(
                    "Cs2VideoGenerator.Cli",
                    "Cs2VideoGenerator.Core")
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(baseOpenObserveEndpoint, telemetryOptions.OpenObserveMetricsPath);
                    otlpOptions.Headers = string.IsNullOrEmpty(telemetryOptions.OpenObserveAccessKey)
                        ? null
                        : $"Authorization=Basic {telemetryOptions.OpenObserveAccessKey}";
                    otlpOptions.Protocol = telemetryOptions.OtlpExportProtocol;
                }));

        return true;
    }
}
