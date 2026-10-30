#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.ProcessManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Core m5: mock-mode and capture-provider selection must be resolved from bound options at
///     resolution time, not from raw IConfiguration at registration time. Otherwise code-based
///     configuration (services.Configure&lt;Cs2VideoGeneratorOptions&gt;) is silently ignored, and
///     running without an appsettings.json silently loses the OBS capture provider.
/// </summary>
public class ServiceCollectionOptionsSelectionTests
{
    private static ServiceProvider BuildProvider(
        Dictionary<string, string?>? configValues = null,
        Action<IServiceCollection>? postConfigure = null,
        Action<IServiceCollection>? preConfigure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? [])
            .Build();

        ServiceCollection services = new();
        services.AddLogging();

        // registrations made before the core ones exercise the host-override seam: every capture
        // registration is a TryAdd, so whatever the host put there first survives
        preConfigure?.Invoke(services);
        services.AddCs2VideoGeneratorCore(configuration);
        postConfigure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The m5 regression scenario: mock mode enabled purely from code, with no configuration
    ///     section at all, must yield the mock process manager.
    /// </summary>
    [Fact]
    public async Task CodeConfiguredMockMode_WithoutConfigSection_YieldsMockProcessManager()
    {
        await using ServiceProvider provider = BuildProvider(
            postConfigure: services =>
                services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true));

        Assert.IsType<MockCs2ProcessManager>(provider.GetRequiredService<ICs2ProcessManager>());
    }

    /// <summary>
    ///     Code-configured ExternalMockServerPath implicitly enables mock mode (PostConfigure
    ///     normalization) and must also yield the mock process manager.
    /// </summary>
    [Fact]
    public async Task CodeConfiguredExternalMockServerPath_YieldsMockProcessManager()
    {
        await using ServiceProvider provider = BuildProvider(
            postConfigure: services =>
                services.Configure<Cs2VideoGeneratorOptions>(o =>
                    o.ExternalMockServerPath = "/tmp/mock_server"));

        Assert.IsType<MockCs2ProcessManager>(provider.GetRequiredService<ICs2ProcessManager>());
    }

    /// <summary>
    ///     Configuration-based mock mode (the pre-m5 path) must keep working unchanged.
    /// </summary>
    [Fact]
    public async Task ConfigurationMockMode_YieldsMockProcessManager()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" });

        Assert.IsType<MockCs2ProcessManager>(provider.GetRequiredService<ICs2ProcessManager>());
    }

    /// <summary>No mock configuration anywhere, and an explicit CS2 root so nothing discovers one.</summary>
    [Fact]
    public async Task RealMode_YieldsRealProcessManager()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:Cs2RootInstallationDirectory"] = Path.GetTempPath()
            });

        Assert.IsType<Cs2ProcessManager>(provider.GetRequiredService<ICs2ProcessManager>());
    }

    /// <summary>
    ///     With no configuration file at all, the bound options default
    ///     (VideoCaptureProvider = "OBS") must still produce a capture provider — it must not be
    ///     silently dropped because the raw config key is absent.
    /// </summary>
    [Fact]
    public async Task NoConfigSection_CaptureProviderStillRegisteredFromBoundDefault()
    {
        await using ServiceProvider provider = BuildProvider(
            postConfigure: services =>
                services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true));

        CsvgVideoSession session = provider.GetRequiredService<CsvgVideoSession>();
        Assert.True(session.HasCaptureProvider);
        Assert.IsType<InEngineCaptureSessionFactory>(provider.GetRequiredService<ICaptureSessionFactory>());
    }

    /// <summary>
    ///     Provider names are matched ignoring case and normalized to their canonical spelling when the
    ///     options are bound, so every downstream reader (keyed lookup, doctor, CLI) sees one value.
    /// </summary>
    [Fact]
    public async Task LowercaseProviderName_ResolvesTheKeyedFactory_AndIsNormalized()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:MockMode"] = "true",
                ["Cs2VideoGenerator:VideoCaptureProvider"] = "obs"
            });

        Assert.Equal(CaptureProviderNames.Obs,
            provider.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>()
                .Value.VideoCaptureProvider);
        Assert.IsType<ObsCaptureSessionFactory>(provider.GetRequiredService<ICaptureSessionFactory>());
    }

    /// <summary>
    ///     A name no provider answers to fails options validation - the replacement for the old
    ///     <c>^(OBS)$</c> regex - and the message names the valid providers.
    /// </summary>
    [Fact]
    public async Task UnknownProviderName_FailsOptionsValidation_ListingTheValidNames()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:MockMode"] = "true",
                ["Cs2VideoGenerator:VideoCaptureProvider"] = "Vegas"
            });

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value);

        Assert.Contains("Vegas", ex.Message);
        Assert.Contains(CaptureProviderNames.Obs, ex.Message);
    }

    /// <summary>
    ///     The documented host-override seam: an unkeyed factory the host registers itself wins over
    ///     the selection forwarder, so a host can supply a backend the registry never heard of.
    /// </summary>
    [Fact]
    public async Task HostRegisteredUnkeyedFactory_WinsOverTheRegistry()
    {
        TestSupport.FakeCaptureSessionFactory hostFactory = new() { ProviderName = "HostSupplied" };

        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" },
            preConfigure: services => services.AddSingleton<ICaptureSessionFactory>(hostFactory));

        Assert.Same(hostFactory, provider.GetRequiredService<ICaptureSessionFactory>());
        Assert.True(provider.GetRequiredService<CsvgVideoSession>().HasCaptureProvider);
    }

    /// <summary>
    ///     The platform gate is enforced where the provider is selected: a factory that reports it
    ///     cannot run here fails resolution with a message naming the provider, instead of failing
    ///     halfway through capture bring-up. Registered under the OBS key (the keyed registration is a
    ///     TryAdd, so a host-supplied one wins) to prove the gate runs on the selected factory rather
    ///     than on a hardcoded provider.
    /// </summary>
    [Fact]
    public async Task ProviderUnsupportedOnCurrentPlatform_FailsResolutionWithAClearMessage()
    {
        TestSupport.FakeCaptureSessionFactory unsupported = new()
        {
            ProviderName = CaptureProviderNames.InEngine,
            IsSupportedOnCurrentPlatform = false
        };

        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" },
            preConfigure: services => services
                .AddKeyedSingleton<ICaptureSessionFactory>(CaptureProviderNames.InEngine, unsupported));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            provider.GetRequiredService<ICaptureSessionFactory>);

        Assert.Contains(CaptureProviderNames.Obs, ex.Message);
        Assert.Contains("not supported on this platform", ex.Message);
    }

    /// <summary>
    ///     The OBS factory is genuinely cross-platform (OBS Studio runs everywhere CSVG does), so the
    ///     gate above must never fire for it - on any host this suite runs on.
    /// </summary>
    [Fact]
    public async Task ObsProvider_IsSupportedOnEveryPlatform()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" });

        Assert.True(provider.GetRequiredKeyedService<ICaptureSessionFactory>(CaptureProviderNames.Obs)
            .IsSupportedOnCurrentPlatform);
    }

    /// <summary>
    ///     Bound from "Capture:OBS" wherever the provider decision lands, so a host such as doctor
    ///     can always inspect it.
    /// </summary>
    [Fact]
    public async Task ObsCaptureConfiguration_IsBoundFromCaptureSection()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:MockMode"] = "true",
                ["Capture:OBS:Port"] = "4460"
            });

        ObsCaptureConfiguration obsConfig = provider
            .GetRequiredService<IOptions<ObsCaptureConfiguration>>().Value;
        Assert.Equal(4460, obsConfig.Port);
    }

    [Fact]
    public async Task InEngineProviderName_ResolvesTheKeyedFactory_AndIsNormalized()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:MockMode"] = "true",
                ["Cs2VideoGenerator:VideoCaptureProvider"] = "inengine"
            });

        Assert.IsType<InEngineCaptureSessionFactory>(provider.GetRequiredService<ICaptureSessionFactory>());
        Assert.Equal(CaptureProviderNames.InEngine,
            provider.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value.VideoCaptureProvider);
    }

    /// <summary>
    ///     InEngine is the default: its frame pipeline and audio were validated against a
    ///     real CS2 (build 2000879), it needs no OBS install, and its Targa path is both lossless and
    ///     faster than jpg on the machines measured. This test is the tripwire on that decision.
    /// </summary>
    [Fact]
    public async Task DefaultCaptureProvider_IsInEngine()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" });

        Assert.Equal(CaptureProviderNames.InEngine,
            provider.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value.VideoCaptureProvider);
        Assert.IsType<InEngineCaptureSessionFactory>(provider.GetRequiredService<ICaptureSessionFactory>());
    }

    /// <summary>
    ///     Nothing about the in-engine backend is platform-specific: it is CS2's own console plus
    ///     ffmpeg. Whether CS2 is installed is an availability question, and gating the platform would
    ///     make the provider unresolvable rather than unavailable.
    /// </summary>
    [Fact]
    public async Task InEngineProvider_IsSupportedOnEveryPlatform()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" });

        Assert.True(provider.GetRequiredKeyedService<ICaptureSessionFactory>(CaptureProviderNames.InEngine)
            .IsSupportedOnCurrentPlatform);
    }

    /// <summary>"Capture:InEngine", nested subsection included, whichever provider is selected.</summary>
    [Fact]
    public async Task InEngineCaptureConfiguration_IsBoundFromCaptureSection()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:MockMode"] = "true",
                ["Capture:InEngine:FrameFormat"] = "Tga",
                ["Capture:InEngine:MissingAudioBehavior"] = "WarnAndContinue",
                ["Capture:InEngine:EngineOutput:FrameNumberDigits"] = "5",
                ["Capture:InEngine:EngineOutput:TgaFrameSizeBytes"] = "6220818"
            });

        InEngineCaptureConfiguration config = provider
            .GetRequiredService<IOptions<InEngineCaptureConfiguration>>().Value;

        Assert.Equal(InEngineFrameFormat.Tga, config.FrameFormat);
        Assert.Equal(MissingAudioBehavior.WarnAndContinue, config.MissingAudioBehavior);
        Assert.Equal(5, config.EngineOutput.FrameNumberDigits);
        Assert.Equal(6220818, config.EngineOutput.TgaFrameSizeBytes);
    }

    /// <summary>
    ///     Registering the in-engine provider must not need a CS2 installation. Its default staging and
    ///     audio directories come from one, which is why they arrive as functions - constructing the
    ///     factory may not call them.
    /// </summary>
    [Fact]
    public async Task InEngineFactory_ConstructsWithoutTouchingTheCs2Installation()
    {
        await using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:Cs2RootInstallationDirectory"] =
                    Path.Combine(Path.GetTempPath(), "csvg-no-such-cs2-" + Guid.NewGuid().ToString("N")),
                ["Cs2VideoGenerator:VideoCaptureProvider"] = CaptureProviderNames.InEngine
            });

        ICaptureSessionFactory factory = provider.GetRequiredService<ICaptureSessionFactory>();
        Assert.Equal(CaptureProviderNames.InEngine, factory.ProviderName);
    }

    /// <summary>
    ///     The canonical consumer pattern <c>using var provider = services.BuildServiceProvider()</c>
    ///     disposes the container synchronously. Microsoft.Extensions.DependencyInjection throws
    ///     InvalidOperationException for any held service that only implements IAsyncDisposable,
    ///     so every disposable service in the default registration must also implement IDisposable.
    ///     Regression for the RC-verification finding on the OBS capture provider.
    /// </summary>
    [Fact]
    public void SyncDisposeOfProvider_WithResolvedSessionAndCaptureProvider_DoesNotThrow()
    {
        ServiceProvider provider = BuildProvider(
            postConfigure: services =>
                services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true));

        // Resolve the full graph so the container tracks every disposable instance.
        provider.GetRequiredService<CsvgVideoSession>();

        provider.Dispose(); // must not throw
    }

    /// <summary>OBS capture-session disposal must be idempotent across both dispose bridges.</summary>
    [Fact]
    public async Task ObsCaptureSession_DoubleDispose_SyncThenAsync_DoesNotThrow()
    {
        ObsCaptureSession session = new(new ObsCaptureConfiguration(),
            new CaptureSessionContext { Engine = new TestSupport.FakeEngineConsole() },
            logger: null, pathDiscoveryService: null);
        session.Dispose();
        await session.DisposeAsync(); // second call must no-op
        session.Dispose(); // and again
    }
}
