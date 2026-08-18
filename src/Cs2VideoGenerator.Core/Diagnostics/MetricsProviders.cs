#region

using System.Diagnostics;

#endregion

namespace Cs2VideoGenerator.Core.Diagnostics;

/// <summary>
///     Reads the capture-provider gauges for one capture session. Both delegates return null once the
///     session they refer to is gone, so a registration can hold its session weakly and the static
///     registry never keeps it alive.
/// </summary>
/// <param name="providerName">The provider name to tag samples with, or null.</param>
/// <param name="providerStatus">The current capture state as a numeric value, or null.</param>
internal sealed class CaptureMetricsAccessor(Func<string?> providerName, Func<long?> providerStatus)
{
    public Func<string?> ProviderName { get; } = providerName ?? throw new ArgumentNullException(nameof(providerName));

    public Func<long?> ProviderStatus { get; } =
        providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
}

/// <summary>
///     Wires platform/process/capture sampling into <see cref="CsvgMetricsSampler" />. Hosts call
///     <see cref="Initialize" /> once during startup to activate the gauges. The samplers are
///     deliberately cheap and pull in no extra dependencies.
/// </summary>
internal static class MetricsProviders
{
    // last registration wins: unregistering only clears the gauges when the caller still owns them.
    private static CaptureMetricsAccessor? _currentCaptureAccessor;

    /// <summary>
    ///     Pass a capture accessor to surface its state and name as well.
    /// </summary>
    public static void Initialize(CaptureMetricsAccessor? captureProviderAccessor = null)
    {
        // CPU: TotalProcessorTime delta over wall time since the last sample.
        ProcessCpuSampler cpuSampler = new();
        CsvgMetricsSampler.HostCpuProvider = cpuSampler.Sample;

        // Memory: GC info plus the current process working set.
        CsvgMetricsSampler.HostMemoryPercentProvider = MemorySampler.SamplePercent;
        CsvgMetricsSampler.HostMemoryBytesProvider = MemorySampler.SampleUsedBytes;

        // cs2.exe: a process existence check, nothing cleverer.
        CsvgMetricsSampler.Cs2ProcessStatusProvider = () => Process.GetProcessesByName("cs2").Length > 0 ? 1L : 0L;
        CsvgMetricsSampler.Cs2ProcessCountProvider = () => Process.GetProcessesByName("cs2").Length;

        // capture goes through an accessor so the provider resolves lazily
        if (captureProviderAccessor is not null)
        {
            RegisterCaptureProvider(captureProviderAccessor);
        }
    }

    /// <summary>
    ///     Registers a capture accessor so capture status/name gauges get populated. Callers should
    ///     pass an accessor that does not strongly root the capture session (e.g. via
    ///     <see cref="WeakReference{T}" />) and call <see cref="UnregisterCaptureProvider" /> on dispose.
    /// </summary>
    public static void RegisterCaptureProvider(CaptureMetricsAccessor captureProviderAccessor)
    {
        ArgumentNullException.ThrowIfNull(captureProviderAccessor);

        _currentCaptureAccessor = captureProviderAccessor;
        CsvgMetricsSampler.CaptureProviderStatusProvider = captureProviderAccessor.ProviderStatus.Invoke;
        CsvgMetricsSampler.CaptureProviderNameProvider = captureProviderAccessor.ProviderName.Invoke;
    }

    /// <summary>
    ///     Unregisters a previously registered capture accessor. No-ops when a different (newer)
    ///     accessor owns the gauges, so disposing an old capture session never clobbers the
    ///     registration of its replacement.
    /// </summary>
    public static void UnregisterCaptureProvider(CaptureMetricsAccessor captureProviderAccessor)
    {
        if (!ReferenceEquals(_currentCaptureAccessor, captureProviderAccessor))
        {
            return;
        }

        _currentCaptureAccessor = null;
        CsvgMetricsSampler.CaptureProviderStatusProvider = null;
        CsvgMetricsSampler.CaptureProviderNameProvider = null;
    }

    private sealed class ProcessCpuSampler
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly int _processorCount = Environment.ProcessorCount;
        private TimeSpan _lastCpu = TimeSpan.Zero;
        private DateTime _lastSample = DateTime.MinValue;

        public double? Sample()
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan cpu = _process.TotalProcessorTime;
            if (_lastSample == DateTime.MinValue)
            {
                _lastCpu = cpu;
                _lastSample = now;
                return null; // first sample establishes baseline
            }

            double cpuDelta = (cpu - _lastCpu).TotalMilliseconds;
            double wallDelta = (now - _lastSample).TotalMilliseconds;

            _lastCpu = cpu;
            _lastSample = now;

            if (wallDelta <= 0)
            {
                return null;
            }

            double utilization = cpuDelta / wallDelta * 100 / _processorCount;
            return Math.Clamp(utilization, 0, 100);
        }
    }

    private static class MemorySampler
    {
        public static double? SamplePercent()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            long total = info.TotalAvailableMemoryBytes;
            if (total <= 0)
            {
                return null;
            }

            long used = info.HeapSizeBytes;
            // Include process working set for non-managed usage to approximate overall utilization.
            long workingSet = Process.GetCurrentProcess().WorkingSet64;
            long estimatedUsed = used + workingSet;
            double percent = (double)estimatedUsed / total * 100;
            return Math.Clamp(percent, 0, 100);
        }

        public static long? SampleUsedBytes()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            long heap = info.HeapSizeBytes;
            long workingSet = Process.GetCurrentProcess().WorkingSet64;
            // Return combined managed + working set as a coarse approximation.
            return heap + workingSet;
        }
    }
}
