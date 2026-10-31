#region

using System.Runtime.InteropServices;
using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.Platform;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Steam;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.DependencyInjection;

/// <summary>
///     Extension methods to register Cs2VideoGenerator core services into a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds Cs2VideoGenerator core services to the provided <see cref="IHostApplicationBuilder" />.
    /// </summary>
    /// <param name="builder">The application builder whose services and configuration will be extended.</param>
    /// <returns>The same <see cref="IHostApplicationBuilder" /> instance to allow method chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Required gRPC hosting.</b> This registers <see cref="Cs2GameService" /> — the gRPC
    ///         service the CS2 plugin and mock server connect back to — but it does not host a gRPC
    ///         server. The host application has to do that itself, on the port configured via the
    ///         <c>Cs2VideoGenerator:GrpcPort</c> configuration key (default 50051):
    ///     </para>
    ///     <code>
    ///     WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    ///     builder.AddCs2VideoGeneratorCore();
    ///     builder.WebHost.ConfigureKestrel(o =&gt;
    ///         o.ListenLocalhost(50051, l =&gt; l.Protocols = HttpProtocols.Http2));
    ///     builder.Services.AddGrpc();
    ///
    ///     WebApplication app = builder.Build();
    ///     app.MapGrpcService&lt;Cs2GameService&gt;();
    ///     await app.StartAsync();
    ///
    ///     CsvgVideoSession session = app.Services.GetRequiredService&lt;CsvgVideoSession&gt;();
    ///     await session.StartAsync();
    ///     </code>
    ///     <para>
    ///         Without this wiring, <c>CsvgVideoSession.StartAsync</c> fails fast with an
    ///         <see cref="InvalidOperationException" /> explaining the missing hosting (instead of a
    ///         multi-minute connect timeout). See the "Using Core as a library" section of the README.
    ///     </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCs2VideoGeneratorCore(this IHostApplicationBuilder builder)
    {
        builder.Services.AddCs2VideoGeneratorCore(builder.Configuration);
        return builder;
    }

    /// <summary>
    ///     Registers the Cs2VideoGenerator Core services with sensible defaults.
    ///     - IPlatformPathProvider (singleton) - OS-aware default provider
    ///     - IBackupManager (singleton)
    ///     - Options binding for <see cref="Cs2VideoGeneratorOptions" />
    ///     The host can override any registration before calling this method.
    /// </summary>
    /// <remarks>
    ///     The host must additionally expose the gRPC endpoint the CS2 plugin/mock server connect to
    ///     (<c>AddGrpc()</c> + <c>MapGrpcService&lt;Cs2GameService&gt;()</c> on an HTTP/2 Kestrel endpoint at
    ///     <c>Cs2VideoGenerator:GrpcPort</c>, default 50051) — see
    ///     <see cref="AddCs2VideoGeneratorCore(IHostApplicationBuilder)" /> for a complete example.
    ///     Mock-mode and capture-provider selection are deferred to service resolution time and read the
    ///     bound <see cref="Cs2VideoGeneratorOptions" />, so code-based configuration such as
    ///     <c>services.Configure&lt;Cs2VideoGeneratorOptions&gt;(o =&gt; o.MockMode = true)</c> is honored.
    /// </remarks>
    public static IServiceCollection AddCs2VideoGeneratorCore(this IServiceCollection services,
        IConfiguration configuration)
    {
        // first, because the options PostConfigure below needs it
        services.TryAddSingleton<INativeAssetProvider, NativeAssetProvider>();

        services.AddOptions<Cs2VideoGeneratorOptions>()
            .Bind(configuration.GetSection(Cs2VideoGeneratorOptions.ConfigurationSectionName))
            .PostConfigure<INativeAssetProvider>((options, nativeAssetProvider) =>
            {
                // ExternalMockServerPath implicitly enables mock mode
                if (!string.IsNullOrWhiteSpace(options.ExternalMockServerPath))
                {
                    options.MockMode = true;
                }

                // Provider names are matched ignoring case; normalizing to the canonical spelling once
                // here is what lets the keyed lookup below, the doctor report and the CLI all read a
                // single value instead of each carrying its own comparison policy. A name no provider
                // answers to is left as written, for validation to reject by name.
                if (CaptureProviderNames.TryResolve(options.VideoCaptureProvider, out string providerName))
                {
                    options.VideoCaptureProvider = providerName;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IPlatformPathProvider>(_ =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new WindowsPlatformPathProvider()
                : new LinuxPlatformPathProvider());

        services.AddSingleton<IPathDiscoveryCache>(sp =>
            new PathDiscoveryCache(sp.GetService<ILogger<PathDiscoveryCache>>()));

        services.AddSingleton<CachedPathDiscoveryService>(sp =>
            new CachedPathDiscoveryService(
                sp.GetRequiredService<IPathDiscoveryCache>(),
                sp.GetRequiredService<IPlatformPathProvider>(),
                sp.GetService<ILogger<CachedPathDiscoveryService>>(),
                sp.GetService<ISteamRegistryReader>()));
        services.AddSingleton<IPathDiscoveryService>(sp => sp.GetRequiredService<CachedPathDiscoveryService>());

        // concrete type first, interface aliased to it below. picks up the cached CS2 root when one
        // exists and no explicit override was configured.
        services.AddSingleton<Cs2DirectoryUtil>(sp =>
        {
            Cs2VideoGeneratorOptions options = sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value;
            IPlatformPathProvider provider = sp.GetRequiredService<IPlatformPathProvider>();
            CachedPathDiscoveryService cacheService = sp.GetRequiredService<CachedPathDiscoveryService>();

            if (options.MockMode ||
                !string.IsNullOrWhiteSpace(options.Cs2RootInstallationDirectory) ||
                !string.IsNullOrWhiteSpace(options.ExternalMockServerPath))
            {
                return new Cs2DirectoryUtil(options, provider);
            }

            string? cachedRoot = cacheService.GetCs2RootDirectory();
            if (!string.IsNullOrEmpty(cachedRoot))
            {
                Cs2VideoGeneratorOptions optionsWithCachedRoot = new()
                {
                    Cs2RootInstallationDirectory = cachedRoot,
                    SteamInstallationDirectory = options.SteamInstallationDirectory,
                    ExternalPluginPath = options.ExternalPluginPath,
                    VideoCaptureProvider = options.VideoCaptureProvider,
                    ExternalMockServerPath = options.ExternalMockServerPath,
                    MockMode = options.MockMode,
                    GrpcPort = options.GrpcPort
                };
                return new Cs2DirectoryUtil(optionsWithCachedRoot, provider);
            }

            return new Cs2DirectoryUtil(options, provider);
        });
        services.AddSingleton<ICs2DirectoryPaths>(sp => sp.GetRequiredService<Cs2DirectoryUtil>());

        services.AddSingleton<IBackupManager, BackupManager>();
        services.AddSingleton<IPluginInstaller, PluginInstaller>();

        // ffmpeg, for concatenation
        services.AddOptions<FfmpegOptions>()
            .Bind(configuration.GetSection(FfmpegOptions.SectionName));
        services.AddSingleton<IFfmpegService, FfmpegService>();

        // Both process managers are registered; which one backs ICs2ProcessManager is decided at
        // resolution time from the bound options, not raw IConfiguration, so code-based configuration
        // like services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true) is honored (m5).
        // The unused manager is never constructed — singleton factories are lazy.
        services.AddSingleton<MockCs2ProcessManager>(sp => new MockCs2ProcessManager(
            sp.GetRequiredService<INativeAssetProvider>(),
            sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<Cs2ProcessManager>(sp => new Cs2ProcessManager(
            sp.GetRequiredService<Cs2DirectoryUtil>(),
            sp.GetRequiredService<IBackupManager>(),
            sp.GetRequiredService<IPluginInstaller>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IPathDiscoveryService>()));

        services.AddSingleton<ICs2ProcessManager>(sp =>
        {
            Cs2VideoGeneratorOptions options = sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value;

            // ExternalMockServerPath implicitly enables mock mode (also normalized in PostConfigure)
            bool isMockMode = options.IsMockModeEffective;
            return isMockMode
                ? sp.GetRequiredService<MockCs2ProcessManager>()
                : sp.GetRequiredService<Cs2ProcessManager>();
        });

        // Mock user-action injection channel: backed by the mock process manager when
        // mock mode is effective, otherwise a no-op stub whose IsAvailable is false, because a real
        // CS2 instance has no injection channel. Branch on the bound options, mirroring the
        // ICs2ProcessManager factory above, rather than resolving ICs2ProcessManager and as-casting
        // it: in real mode Cs2ProcessManager is never an injector anyway, and constructing it
        // eagerly pulls in Cs2DirectoryUtil, which throws when no CS2 install is present (e.g.
        // CI/library hosts). TryAdd so test hosts can stub it.
        services.TryAddSingleton<IMockUserActionInjector>(sp =>
        {
            Cs2VideoGeneratorOptions options = sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value;
            return options.IsMockModeEffective
                ? (IMockUserActionInjector)sp.GetRequiredService<MockCs2ProcessManager>()
                : new NoOpMockUserActionInjector();
        });

        // The capture registry: one factory per backend, keyed by its provider name, and one
        // configuration section per backend (Capture:<name>). Adding a provider is one options binding
        // plus one keyed registration; nothing shared below has to change.
        services.AddOptions<ObsCaptureConfiguration>()
            .Bind(configuration.GetSection(CaptureProviderNames.ConfigurationSectionName)
                .GetSection(CaptureProviderNames.Obs));

        services.AddSingleton<ObsProcessManager>(sp => new ObsProcessManager(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<CachedPathDiscoveryService>()));

        services.AddOptions<InEngineCaptureConfiguration>()
            .Bind(configuration.GetSection(CaptureProviderNames.ConfigurationSectionName)
                .GetSection(CaptureProviderNames.InEngine));

        // The OBS capture-session factory: one capture session per CS2 run, created from it. TryAdd so
        // a host that registers its own factory under this key beforehand wins.
        services.TryAddKeyedSingleton<ICaptureSessionFactory>(CaptureProviderNames.Obs, (sp, _) =>
            new ObsCaptureSessionFactory(
                sp.GetRequiredService<IOptions<ObsCaptureConfiguration>>(),
                sp.GetRequiredService<ObsProcessManager>(),
                sp.GetService<ILoggerFactory>()));

        // The in-engine capture-session factory. Its default staging and audio directories come from
        // the CS2 installation, which is why they arrive as functions: Cs2DirectoryUtil throws when no
        // CS2 is installed, and registering a provider nobody selected must not make a library host
        // care where the game is.
        services.TryAddKeyedSingleton<ICaptureSessionFactory>(CaptureProviderNames.InEngine, (sp, _) =>
            new InEngineCaptureSessionFactory(
                sp.GetRequiredService<IOptions<InEngineCaptureConfiguration>>(),
                sp.GetRequiredService<IFfmpegService>(),
                () => Path.Combine(sp.GetRequiredService<ICs2DirectoryPaths>().CsvgPluginDirectory,
                    InEngineCaptureConfiguration.DefaultStagingSubdirectory),
                // Audio and frames land under different roots (observed on build 2000879): frames go
                // under the csvg search path (csvg/movie/<timestamp>/), the wav under the csgo game
                // files directory (csgo/movie/<timestamp>/<name>.wav).
                () => Path.Combine(sp.GetRequiredService<ICs2DirectoryPaths>().CsgoGameFilesDirectory,
                    InEngineCaptureConfiguration.DefaultAudioSubdirectory),
                sp.GetService<ILoggerFactory>()));

        // The hooked in-engine factory: same Capture:InEngine configuration and directory logic,
        // different frame source (the plugin's DXGI present hook streaming raw RGBA over the frame
        // pipe instead of startmovie's frame files). Windows only, enforced by the platform gate on
        // the unkeyed forwarder below.
        services.TryAddKeyedSingleton<ICaptureSessionFactory>(CaptureProviderNames.InEngineHooked, (sp, _) =>
            new InEngineHookedCaptureSessionFactory(
                sp.GetRequiredService<IOptions<InEngineCaptureConfiguration>>(),
                sp.GetRequiredService<IFfmpegService>(),
                () => Path.Combine(sp.GetRequiredService<ICs2DirectoryPaths>().CsvgPluginDirectory,
                    InEngineCaptureConfiguration.DefaultStagingSubdirectory),
                () => Path.Combine(sp.GetRequiredService<ICs2DirectoryPaths>().CsgoGameFilesDirectory,
                    InEngineCaptureConfiguration.DefaultAudioSubdirectory),
                sp.GetService<ILoggerFactory>()));

        // The unkeyed forwarder to whichever provider is selected. Keeps plain
        // GetRequiredService<ICaptureSessionFactory>() working and preserves the host-override seam
        // (TryAdd, so a host-supplied unkeyed factory registered beforehand wins). Selection is
        // deferred to resolution time and reads the bound Cs2VideoGeneratorOptions (default "OBS", any
        // spelling normalized when the options were bound), so the provider is not silently lost when
        // no configuration file supplies the raw config key.
        // Keyed and unkeyed descriptors resolve to one and the same singleton, which the container
        // owns once per registration. A factory that ever becomes disposable therefore has to
        // tolerate being disposed twice.
        services.TryAddSingleton<ICaptureSessionFactory>(sp =>
        {
            string providerName = sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>()
                .Value.VideoCaptureProvider;

            ICaptureSessionFactory? factory = sp.GetKeyedService<ICaptureSessionFactory>(providerName);
            if (factory == null)
            {
                throw new InvalidOperationException(
                    $"No capture provider factory is registered for '{providerName}'. Known providers: " +
                    $"{CaptureProviderNames.Format()}. Register an ICaptureSessionFactory keyed by that " +
                    "name, or select a different provider with \"Cs2VideoGenerator:VideoCaptureProvider\".");
            }

            // The platform gate: a backend that cannot run on this operating system fails the session
            // here, before anything is launched, rather than halfway through capture bring-up.
            if (!factory.IsSupportedOnCurrentPlatform)
            {
                throw new InvalidOperationException(
                    $"Capture provider '{factory.ProviderName}' is not supported on this platform " +
                    $"({RuntimeInformation.OSDescription}). Select a provider that is with " +
                    $"\"Cs2VideoGenerator:VideoCaptureProvider\" (known providers: " +
                    $"{CaptureProviderNames.Format()}).");
            }

            return factory;
        });

        // The single per-clip capture recipe, shared by every provider and every session.
        services.TryAddSingleton<ClipCaptureCoordinator>(sp => new ClipCaptureCoordinator(
            sp.GetService<ILogger<ClipCaptureCoordinator>>()));

        // The compilation pipeline (demo grouping, per-clip capture through the coordinator,
        // concatenation). Stateless, so one instance serves every session.
        services.TryAddSingleton<Cs2CompilationRunner>(sp => new Cs2CompilationRunner(
            sp.GetRequiredService<ClipCaptureCoordinator>(),
            sp.GetService<IFfmpegService>(),
            sp.GetService<ILogger<Cs2CompilationRunner>>()));

        // Loopback probe used at session start to fail fast when the host never exposed the gRPC
        // endpoint (AddGrpc()/MapGrpcService<Cs2GameService>()).
        services.TryAddSingleton<IGrpcEndpointProbe, TcpGrpcEndpointProbe>();

        // The engine host: creates one engine session per CS2 run and tracks the current one. It has
        // to be a singleton shared by the video session (which creates sessions) and Cs2GameService
        // (which routes the plugin's event stream into whichever session is current). Two hosts would
        // mean the wire pushing events at a session nobody is using.
        services.TryAddSingleton<Cs2EngineHost>(sp => new Cs2EngineHost(
            sp.GetRequiredService<ILogger<Cs2EngineHost>>(),
            sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>(),
            sp.GetRequiredService<ICs2ProcessManager>(),
            sp.GetService<IGrpcEndpointProbe>()));
        services.TryAddSingleton<ICs2EngineHost>(sp => sp.GetRequiredService<Cs2EngineHost>());

        // The composition entry point. The capture factory comes from the selection forwarder above,
        // so "is capture configured" means "the selected provider name resolves in the registry and
        // runs on this platform".
        services.AddSingleton<CsvgVideoSession>(sp => new CsvgVideoSession(
            sp.GetRequiredService<ILogger<CsvgVideoSession>>(),
            sp.GetRequiredService<ICs2EngineHost>(),
            sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>(),
            sp.GetService<ICaptureSessionFactory>(),
            sp.GetRequiredService<ClipCaptureCoordinator>(),
            sp.GetRequiredService<Cs2CompilationRunner>(),
            sp.GetService<IFfmpegService>()));

        services.AddSingleton<Cs2GameService>();

        MetricsProviders.Initialize();

        return services;
    }
}
