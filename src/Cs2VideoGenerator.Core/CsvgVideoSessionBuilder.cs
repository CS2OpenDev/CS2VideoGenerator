#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     Assembles a <see cref="CsvgVideoSession" /> by hand, for a host with no container. Under
///     dependency injection there is nothing to assemble: <c>AddCs2VideoGeneratorCore</c> registers
///     the session itself, and resolving it is the supported path.
/// </summary>
/// <remarks>
///     <para>
///         A hand-built session still has to expose the gRPC endpoint the plugin connects back to
///         (<c>AddGrpc()</c> + <c>MapGrpcService&lt;Cs2GameService&gt;()</c>). The service that
///         receives those connections has to be built over the same
///         <see cref="CsvgVideoSession.EngineHost" /> as the session, or the plugin's events never
///         reach it. In practice: pass <c>session.EngineHost</c> to <see cref="Cs2GameService" />
///         yourself.
///     </para>
///     <para>
///         One session per engine host: constructing a <see cref="CsvgVideoSession" /> claims the
///         host's current engine session, so building a second one over a host that already has one
///         takes the plugin's events away from the first.
///     </para>
///     <para>
///         Every dependency except the CS2 process manager has a working default, so the smallest
///         hand-built session is
///         <c>new CsvgVideoSessionBuilder().WithProcessManager(pm).Build()</c>, a playback-only
///         session with no capture backend.
///     </para>
/// </remarks>
public sealed class CsvgVideoSessionBuilder
{
    private ICaptureSessionFactory? _captureSessionFactory;
    private ClipCaptureCoordinator? _captureCoordinator;
    private Cs2CompilationRunner? _compilationRunner;
    private ICs2EngineHost? _engineHost;
    private IGrpcEndpointProbe? _endpointProbe;
    private IFfmpegService? _ffmpegService;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IOptions<Cs2VideoGeneratorOptions> _options = Options.Create(new Cs2VideoGeneratorOptions());
    private ICs2ProcessManager? _processManager;

    /// <summary>
    ///     The logger factory the session, its engine sessions and the fallback collaborators log
    ///     through. Defaults to a no-op factory.
    /// </summary>
    public CsvgVideoSessionBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>
    ///     The CSVG options for this session (mock mode, gRPC port, telemetry defaults, ...).
    /// </summary>
    public CsvgVideoSessionBuilder WithOptions(Cs2VideoGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = Options.Create(options);
        return this;
    }

    /// <summary>
    ///     The CSVG options for this session, already wrapped (e.g. by an options monitor).
    /// </summary>
    public CsvgVideoSessionBuilder WithOptions(IOptions<Cs2VideoGeneratorOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>
    ///     The process manager engine sessions launch and stop CS2 through. Required unless an engine
    ///     host is supplied with <see cref="WithEngineHost" />, which already owns one.
    /// </summary>
    public CsvgVideoSessionBuilder WithProcessManager(ICs2ProcessManager processManager)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        return this;
    }

    /// <summary>
    ///     An existing engine host to create engine sessions from. Use this when the gRPC layer is
    ///     already wired to a host, so both sides share it. When omitted, <see cref="Build" /> creates
    ///     one over the process manager and publishes it as
    ///     <see cref="CsvgVideoSession.EngineHost" />.
    /// </summary>
    public CsvgVideoSessionBuilder WithEngineHost(ICs2EngineHost engineHost)
    {
        _engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
        return this;
    }

    /// <summary>
    ///     The capture backend to record with, or null for a playback-only session. See
    ///     <see cref="CaptureProviderNames" /> for the backends that ship.
    /// </summary>
    public CsvgVideoSessionBuilder WithCaptureProvider(ICaptureSessionFactory? captureSessionFactory)
    {
        _captureSessionFactory = captureSessionFactory;
        return this;
    }

    /// <summary>
    ///     The shared per-clip capture recipe. Supply one only to share a single capture state machine
    ///     with a compilation runner built elsewhere; otherwise <see cref="Build" /> creates the pair
    ///     consistently.
    /// </summary>
    public CsvgVideoSessionBuilder WithClipCaptureCoordinator(ClipCaptureCoordinator captureCoordinator)
    {
        _captureCoordinator = captureCoordinator ?? throw new ArgumentNullException(nameof(captureCoordinator));
        return this;
    }

    /// <summary>
    ///     The shared compilation pipeline. It must be built over the same coordinator this session
    ///     uses, or a compilation's clips report capture state through a different state machine than
    ///     the session forwards.
    /// </summary>
    public CsvgVideoSessionBuilder WithCompilationRunner(Cs2CompilationRunner compilationRunner)
    {
        _compilationRunner = compilationRunner ?? throw new ArgumentNullException(nameof(compilationRunner));
        return this;
    }

    /// <summary>
    ///     The FFmpeg service used to concatenate a compilation's clips. Without it clips are still
    ///     captured and a compilation that asked for concatenation reports partial success.
    /// </summary>
    public CsvgVideoSessionBuilder WithFfmpegService(IFfmpegService? ffmpegService)
    {
        _ffmpegService = ffmpegService;
        return this;
    }

    /// <summary>
    ///     The loopback probe that verifies the host's gRPC endpoint is listening before CS2 is
    ///     launched. Only used when this builder creates the engine host; null skips the check and a
    ///     missing endpoint then surfaces as a connect timeout instead of an immediate error.
    /// </summary>
    public CsvgVideoSessionBuilder WithGrpcEndpointProbe(IGrpcEndpointProbe? endpointProbe)
    {
        _endpointProbe = endpointProbe;
        return this;
    }

    /// <summary>
    ///     Builds the session. Composes the engine host (creating one over the process manager when
    ///     none was supplied) and the capture collaborators, then hands them to
    ///     <see cref="CsvgVideoSession" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Neither an engine host nor a process manager was supplied, so no CS2 process could ever be
    ///     launched.
    /// </exception>
    public CsvgVideoSession Build()
    {
        ICs2EngineHost engineHost = _engineHost ?? CreateEngineHost();
        ClipCaptureCoordinator coordinator = _captureCoordinator ??
                                             new ClipCaptureCoordinator(
                                                 _loggerFactory.CreateLogger<ClipCaptureCoordinator>());

        // A runner built here shares this session's coordinator, so a compilation's clips report
        // capture state through the same state machine the session forwards.
        Cs2CompilationRunner runner = _compilationRunner ??
                                      new Cs2CompilationRunner(coordinator, _ffmpegService,
                                          _loggerFactory.CreateLogger<Cs2CompilationRunner>());

        return new CsvgVideoSession(
            _loggerFactory.CreateLogger<CsvgVideoSession>(),
            engineHost,
            _options,
            _captureSessionFactory,
            coordinator,
            runner,
            _ffmpegService);
    }

    private Cs2EngineHost CreateEngineHost()
    {
        if (_processManager == null)
        {
            throw new InvalidOperationException(
                $"No engine host or CS2 process manager was supplied. Call {nameof(WithProcessManager)} " +
                $"(or {nameof(WithEngineHost)} with a host that already has one) before building - " +
                "without it no CS2 process can be launched.");
        }

        return new Cs2EngineHost(
            _loggerFactory.CreateLogger<Cs2EngineHost>(),
            _options,
            _processManager,
            _endpointProbe);
    }
}
