#region

using System.Diagnostics.Metrics;
using Cs2VideoGenerator.Core.ProcessManagement;

#endregion

namespace Cs2VideoGenerator.Core.Diagnostics;

/// <summary>
///     Central meter and instruments for Cs2VideoGenerator.Core. Uses NerdBank.GitVersioning informational
///     version so emitted metrics include the same semantic version as tracing.
/// </summary>
internal static class CsvgMetrics
{
    public const string MeterName = "Cs2VideoGenerator.Core";

    public const string Version = ThisAssembly.AssemblyInformationalVersion;

    // Shared meter for all Core metrics; collector-agnostic System.Diagnostics instruments.
    public static readonly Meter Meter = new(MeterName, Version);

    public static readonly Counter<long> ProcessLaunchCount =
        Meter.CreateCounter<long>("cs2.process.launch.count");

    /// <summary>
    ///     Records the outcome of a (real or mock) CS2 process launch: increments
    ///     <see cref="ProcessLaunchCount" /> with a result tag derived from the exit reason, plus
    ///     <see cref="ProcessFailureByStage" /> when the process failed with an exception.
    ///     Shared by the real and mock process managers.
    /// </summary>
    public static void RecordProcessLaunch(ProcessExitResult result, bool mockMode)
    {
        string resultTag = result.Reason switch
        {
            ProcessExitReason.Completed => "success",
            ProcessExitReason.Canceled => "cancelled",
            _ => "failure"
        };

        ProcessLaunchCount.Add(
            1,
            new KeyValuePair<string, object?>("result", resultTag),
            new KeyValuePair<string, object?>("mock_mode", mockMode));

        if (result.Reason == ProcessExitReason.Error && result.Exception != null)
        {
            ProcessFailureByStage.Add(
                1,
                new KeyValuePair<string, object?>("stage", "launch"),
                new KeyValuePair<string, object?>("exception", result.Exception.GetType().Name));
        }
    }

    public static readonly Histogram<double> ProcessLaunchDuration =
        Meter.CreateHistogram<double>("cs2.process.launch.duration", "s");

    public static readonly Counter<long> PluginInstallCount =
        Meter.CreateCounter<long>("cs2.plugin.install.count");

    public static readonly Histogram<double> PluginInstallDuration =
        Meter.CreateHistogram<double>("cs2.plugin.install.duration", "s");

    public static readonly Counter<long> GrpcSessionCount =
        Meter.CreateCounter<long>("cs2.grpc.session.count");

    public static readonly UpDownCounter<long> GrpcSessionsActive =
        Meter.CreateUpDownCounter<long>("cs2.grpc.sessions.active");

    public static readonly Histogram<double> GrpcTickInterval =
        Meter.CreateHistogram<double>("cs2.grpc.tick.interval", "ms");

    public static readonly Counter<long> DemoCommandCount =
        Meter.CreateCounter<long>("cs2.demo.command.count");

    public static readonly Histogram<double> DemoCommandLatency =
        Meter.CreateHistogram<double>("cs2.demo.command.latency", "ms");

    public static readonly UpDownCounter<long> ProcessInstancesActive =
        Meter.CreateUpDownCounter<long>("cs2.process.instances.active");

    public static readonly Counter<long> ProcessFailureCount =
        Meter.CreateCounter<long>("cs2.process.failure.count");

    public static readonly Counter<long> GrpcExceptionCount =
        Meter.CreateCounter<long>("cs2.grpc.exception.count");

    public static readonly Counter<long> GrpcServerStatusCount =
        Meter.CreateCounter<long>("cs2.grpc.server.status.count");

    public static readonly Histogram<double> GrpcServerHandlerDuration =
        Meter.CreateHistogram<double>("cs2.grpc.server.handler.duration", "s");

    public static readonly Counter<long> CaptureSessionCount =
        Meter.CreateCounter<long>("cs2.capture.session.count");

    public static readonly Histogram<double> CaptureOutputSize =
        Meter.CreateHistogram<double>("cs2.capture.output.size", "By");

    public static readonly Counter<long> ProcessFailureByStage =
        Meter.CreateCounter<long>("cs2.process.failure.count.by_stage");

    public static readonly ObservableUpDownCounter<long> Cs2ProcessInstances =
        Meter.CreateObservableUpDownCounter("cs2.process.instances", ObserveCs2ProcessInstances, "count");

    public static readonly ObservableGauge<double> HostCpuUtilization =
        Meter.CreateObservableGauge("cs2.host.cpu.utilization", ObserveHostCpu, "%");

    public static readonly ObservableGauge<double> HostMemoryUtilization =
        Meter.CreateObservableGauge("cs2.host.memory.utilization", ObserveHostMemory, "%");

    public static readonly ObservableGauge<long> HostMemoryUsedBytes =
        Meter.CreateObservableGauge("cs2.host.memory.used", ObserveHostMemoryBytes, "By");

    public static readonly ObservableGauge<long> Cs2ProcessStatus =
        Meter.CreateObservableGauge("cs2.process.status", ObserveCs2ProcessStatus, "state");

    public static readonly ObservableGauge<long> CaptureProviderStatus =
        Meter.CreateObservableGauge("cs2.capture.provider.status", ObserveCaptureProviderStatus, "state");

    private static IEnumerable<Measurement<double>> ObserveHostCpu()
    {
        MetricsSnapshot snapshot = CsvgMetricsSampler.GetSnapshot();
        if (snapshot.HostCpuPercent.HasValue)
        {
            yield return new Measurement<double>(snapshot.HostCpuPercent.Value,
                new KeyValuePair<string, object?>("source", "host"));
        }
    }

    private static IEnumerable<Measurement<double>> ObserveHostMemory()
    {
        MetricsSnapshot snapshot = CsvgMetricsSampler.GetSnapshot();
        if (snapshot.HostMemoryPercent.HasValue)
        {
            yield return new Measurement<double>(snapshot.HostMemoryPercent.Value,
                new KeyValuePair<string, object?>("source", "host"));
        }
    }

    private static IEnumerable<Measurement<long>> ObserveHostMemoryBytes()
    {
        MetricsSnapshot snapshot = CsvgMetricsSampler.GetSnapshot();
        if (snapshot.HostMemoryBytes.HasValue)
        {
            yield return new Measurement<long>(snapshot.HostMemoryBytes.Value,
                new KeyValuePair<string, object?>("source", "host"));
        }
    }

    private static IEnumerable<Measurement<long>> ObserveCs2ProcessInstances()
    {
        MetricsSnapshot snapshot = CsvgMetricsSampler.GetSnapshot();
        if (snapshot.Cs2ProcessCount.HasValue)
        {
            yield return new Measurement<long>(snapshot.Cs2ProcessCount.Value,
                new KeyValuePair<string, object?>("process", "cs2.exe"));
        }
    }

    private static IEnumerable<Measurement<long>> ObserveCs2ProcessStatus()
    {
        MetricsSnapshot snapshot = CsvgMetricsSampler.GetSnapshot();
        if (snapshot.Cs2ProcessStatus.HasValue)
        {
            yield return new Measurement<long>(snapshot.Cs2ProcessStatus.Value,
                new KeyValuePair<string, object?>("process", "cs2.exe"));
        }
    }

    private static IEnumerable<Measurement<long>> ObserveCaptureProviderStatus()
    {
        MetricsSnapshot snapshot = CsvgMetricsSampler.GetSnapshot();
        if (snapshot.CaptureProviderStatus.HasValue)
        {
            yield return new Measurement<long>(
                snapshot.CaptureProviderStatus.Value,
                new KeyValuePair<string, object?>("provider", snapshot.CaptureProviderName ?? "unknown"));
        }
    }
}

internal static class CsvgMetricsSampler
{
    private static readonly object Gate = new();
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(1);
    private static DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private static MetricsSnapshot _snapshot = new(DateTimeOffset.MinValue, null, null, null, null, null, null, null);

    // Delegates allow host code to plug in platform-specific samplers without extra dependencies.
    internal static Func<double?>? HostCpuProvider;
    internal static Func<double?>? HostMemoryPercentProvider;
    internal static Func<long?>? HostMemoryBytesProvider;
    internal static Func<long?>? Cs2ProcessCountProvider;
    internal static Func<long?>? CaptureProviderStatusProvider;
    internal static Func<string?>? CaptureProviderNameProvider;
    internal static Func<long?>? Cs2ProcessStatusProvider;

    internal static MetricsSnapshot GetSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (Gate)
        {
            if (now - _lastSampleAt > SnapshotTtl)
            {
                _snapshot = new MetricsSnapshot(
                    now,
                    HostCpuProvider?.Invoke(),
                    HostMemoryPercentProvider?.Invoke(),
                    HostMemoryBytesProvider?.Invoke(),
                    Cs2ProcessCountProvider?.Invoke(),
                    Cs2ProcessStatusProvider?.Invoke(),
                    CaptureProviderStatusProvider?.Invoke(),
                    CaptureProviderNameProvider?.Invoke());
                _lastSampleAt = now;
            }

            return _snapshot;
        }
    }
}

internal readonly record struct MetricsSnapshot(
    DateTimeOffset Timestamp,
    double? HostCpuPercent,
    double? HostMemoryPercent,
    long? HostMemoryBytes,
    long? Cs2ProcessCount,
    long? Cs2ProcessStatus,
    long? CaptureProviderStatus,
    string? CaptureProviderName);
