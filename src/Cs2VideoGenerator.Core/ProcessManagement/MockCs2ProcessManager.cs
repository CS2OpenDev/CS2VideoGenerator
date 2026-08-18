#region

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using CliWrap;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Process manager for mock CS2 mode. Launches the mock_server executable
///     without any plugin management, backup/restore, or CS2-specific validation.
///     Used for testing without a real CS2 installation. Also exposes the mock's
///     stdin user-action injection channel as <see cref="IMockUserActionInjector" />.
/// </summary>
public partial class MockCs2ProcessManager : IntegratedProcessLifecycleManager<MockLaunchConfiguration>,
    ICs2ProcessManager, IMockUserActionInjector
{
    private readonly Lock _injectionGate = new();
    private readonly ILogger _logger;
    private readonly INativeAssetProvider _nativeAssetProvider;
    private readonly IOptions<Cs2VideoGeneratorOptions> _options;

    // line channel feeding the current mock_server process's stdin. recreated per launch (see
    // GetStandardInputPipe), completed on process exit so the pipe writer finishes and late
    // injections fail fast.
    private Channel<string>? _injectionChannel;

    /// <summary>
    ///     Creates a mock process manager that resolves the mock_server executable from
    ///     <see cref="Cs2VideoGeneratorOptions.ExternalMockServerPath" /> when configured,
    ///     otherwise from the native assets bundled with the NuGet package.
    /// </summary>
    /// <param name="nativeAssetProvider">Locates the bundled mock_server executable.</param>
    /// <param name="options">Options carrying the optional external mock server path.</param>
    /// <param name="loggerFactory">Factory for the manager and process-output loggers.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public MockCs2ProcessManager(
        INativeAssetProvider nativeAssetProvider,
        IOptions<Cs2VideoGeneratorOptions> options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory?.CreateLogger<MockCs2ProcessManager>(),
            loggerFactory?.CreateLogger("Cs2VideoGenerator.Core.MockCs2Process"))
    {
        _nativeAssetProvider = nativeAssetProvider ?? throw new ArgumentNullException(nameof(nativeAssetProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = loggerFactory?.CreateLogger<MockCs2ProcessManager>() ??
                  throw new ArgumentNullException(nameof(loggerFactory));
    }

    #region IntegratedProcessLifecycleManager Implementation

    /// <inheritdoc />
    protected override string LogPrefix => "MockCS2";

    /// <inheritdoc />
    /// <remarks>One second. The headless mock starts far faster than the real game.</remarks>
    protected override TimeSpan PostLaunchDelay => TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    /// <remarks>
    ///     Prefers <see cref="Cs2VideoGeneratorOptions.ExternalMockServerPath" /> when configured,
    ///     then falls back to the mock_server bundled with the package
    ///     (<see cref="INativeAssetProvider.MockServerPath" />). Returns null when neither source
    ///     yields a path, which makes the launch fail.
    /// </remarks>
    protected override string? DiscoverExecutable(MockLaunchConfiguration config)
    {
        string? externalPath = _options.Value.ExternalMockServerPath;
        if (!string.IsNullOrWhiteSpace(externalPath))
        {
            LogUsingExternalMockServer(_logger, externalPath);
            return externalPath;
        }

        string? bundledPath = _nativeAssetProvider.MockServerPath;
        if (!string.IsNullOrWhiteSpace(bundledPath))
        {
            LogUsingBundledMockServer(_logger, bundledPath);
            return bundledPath;
        }

        LogMockServerNotFound(_logger);
        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The mock server ignores display options; the only argument passed is a
    ///     <c>--traceparent</c> derived from the current <see cref="Activity" /> so mock-server
    ///     spans join the caller's distributed trace.
    /// </remarks>
    protected override IEnumerable<string> BuildArguments(MockLaunchConfiguration config)
    {
        Activity? activity = Activity.Current;
        if (activity != null)
        {
            string traceId = activity.TraceId.ToHexString();
            string spanId = activity.SpanId.ToHexString();
            string flags = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00";
            string traceparent = $"00-{traceId}-{spanId}-{flags}";
            yield return $"--traceparent={traceparent}";
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Intentionally a no-op (beyond a debug log): mock mode performs no plugin install,
    ///     no gameinfo.gi patching, and no backups.
    /// </remarks>
    protected override Task OnBeforeLaunchAsync(MockLaunchConfiguration config, CancellationToken cancellationToken)
    {
        LogMockModeNoPreLaunch(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Creates a fresh line channel per launch and feeds it to the child's stdin for the
    ///     process lifetime — the transport behind <see cref="IMockUserActionInjector" />.
    ///     The pipe callback drains the channel and writes one LF-terminated UTF-8 line per
    ///     injected command; channel completion (process exit) or CliWrap's cancellation
    ///     (process teardown) ends the feed, which the mock observes as stdin EOF and
    ///     handles gracefully.
    /// </remarks>
    protected override PipeSource GetStandardInputPipe(MockLaunchConfiguration config)
    {
        Channel<string> channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        lock (_injectionGate)
        {
            _injectionChannel = channel;
        }

        return PipeSource.Create(async (destination, cancellationToken) =>
        {
            try
            {
                await foreach (string line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // process teardown cancelled the pipe. expected; nothing to clean up.
            }
        });
    }

    /// <inheritdoc />
    /// <remarks>Records launch metrics (tagged as mock mode); there is nothing to uninstall.</remarks>
    protected override Task OnProcessExitedAsync(ProcessExitResult result)
    {
        CsvgMetrics.RecordProcessLaunch(result, mockMode: true);

        // Close the injection channel so the stdin feed ends and late injections fail fast.
        lock (_injectionGate)
        {
            _injectionChannel?.Writer.TryComplete();
            _injectionChannel = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Intentionally a no-op (beyond a debug log): mock mode takes no backups, so there is
    ///     nothing to restore.
    /// </remarks>
    protected override Task OnAfterStopAsync(CancellationToken cancellationToken)
    {
        LogMockModeNoCleanup(_logger);
        return Task.CompletedTask;
    }

    #endregion

    #region ICs2ProcessManager Implementation

    /// <summary>
    ///     Launches the mock server with the configured options.
    /// </summary>
    public Task LaunchAsync(CancellationToken cancellationToken)
    {
        MockLaunchConfiguration config = new();
        return LaunchAsync(config, cancellationToken);
    }

    /// <summary>
    ///     Launches the mock server. The display options are recorded and then ignored; the mock is
    ///     headless.
    /// </summary>
    public Task LaunchAsync(int? width, int? height, bool fullscreen, CancellationToken cancellationToken)
    {
        MockLaunchConfiguration config = new() { Width = width, Height = height, Fullscreen = fullscreen };
        return LaunchAsync(config, cancellationToken);
    }

    #endregion

    #region IMockUserActionInjector Implementation

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_injectionGate)
            {
                return IsRunning && _injectionChannel != null;
            }
        }
    }

    /// <inheritdoc />
    public async Task InjectAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Injection command must be non-empty.", nameof(command));
        }

        if (command.Contains('\n') || command.Contains('\r'))
        {
            throw new ArgumentException("Injection command must be a single line (see MockUserActions).",
                nameof(command));
        }

        Channel<string>? channel;
        lock (_injectionGate)
        {
            channel = IsRunning ? _injectionChannel : null;
        }

        if (channel == null)
        {
            throw new InvalidOperationException(
                "No mock user-action injection channel is available: the mock_server process is not running.");
        }

        await channel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        LogInjectedUserAction(_logger, command);
    }

    #endregion

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Using external mock server path: {Path}")]
    private static partial void LogUsingExternalMockServer(ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Using bundled mock server path: {Path}")]
    private static partial void LogUsingBundledMockServer(ILogger logger, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Mock server executable not found - no external path configured and bundled asset not available")]
    private static partial void LogMockServerNotFound(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "Mock mode - no pre-launch setup required")]
    private static partial void LogMockModeNoPreLaunch(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "Mock mode - no cleanup required")]
    private static partial void LogMockModeNoCleanup(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Injected mock user action: {Command}")]
    private static partial void LogInjectedUserAction(ILogger logger, string command);

    #endregion
}

/// <summary>
///     Configuration for launching a mock CS2 process.
/// </summary>
public record MockLaunchConfiguration
{
    /// <summary>
    ///     The width of the game window in pixels. Stored but not used by mock server.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    ///     The height of the game window in pixels. Stored but not used by mock server.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    ///     Whether to launch in fullscreen mode. Stored but not used by mock server.
    /// </summary>
    public bool Fullscreen { get; init; }
}
