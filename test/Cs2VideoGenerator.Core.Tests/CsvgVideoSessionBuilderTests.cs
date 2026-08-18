#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     <see cref="CsvgVideoSessionBuilder" /> is the documented construction path for hosts without a
///     container, so what it composes is a contract: which collaborators are required, which have
///     working defaults, which misconfigurations are refused, and - above all - that the pieces it
///     wires together are the same instances the rest of the graph uses. A session whose gRPC layer
///     talks to a different engine host, or whose compilation runner reports through a different
///     capture state machine, builds and starts perfectly and then does nothing useful. The first
///     region pins the same identity contract on the container path, which needs no builder at all.
/// </summary>
public class CsvgVideoSessionBuilderTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    #region The DI path: resolve the session

    /// <summary>
    ///     The invariant the whole gRPC path rests on: the session must create its engine sessions from
    ///     the very host <c>Cs2GameService</c> resolves, because the wire routes the plugin's event
    ///     stream through <see cref="ICs2EngineHost.Current" />. Identity, not just non-null - two hosts
    ///     of the same type would satisfy a null check and silently drop every plugin event.
    /// </summary>
    /// <remarks>
    ///     Asserted on the container's own registration rather than on a builder: the session is the
    ///     thing <c>AddCs2VideoGeneratorCore</c> registers, and resolving it is the only supported way
    ///     to get one out of a container. A builder that re-composed the graph from the same container
    ///     would mint a second session and take <see cref="ICs2EngineHost.Current" /> from the
    ///     singleton, which is why that entry point no longer exists.
    /// </remarks>
    [Fact]
    public async Task TheContainersSession_SharesItsEngineHost_WithTheGrpcLayer()
    {
        await using ServiceProvider provider = BuildContainer();

        CsvgVideoSession session = provider.GetRequiredService<CsvgVideoSession>();
        ICs2EngineHost containerHost = provider.GetRequiredService<ICs2EngineHost>();

        Assert.Same(containerHost, session.EngineHost);

        // ...and the session it created is the one the wire hands events to.
        Assert.Same(session.Engine, containerHost.Current);

        // The registration is a singleton, so there is exactly one claimant of Current.
        Assert.Same(session, provider.GetRequiredService<CsvgVideoSession>());
    }

    /// <summary>
    ///     The unkeyed forwarder applies the provider selection rules, so a session resolved from a
    ///     container comes up with the configured backend rather than as playback-only.
    /// </summary>
    [Fact]
    public async Task TheContainersSession_TakesTheSelectedCaptureProvider()
    {
        await using ServiceProvider provider = BuildContainer();

        CsvgVideoSession session = provider.GetRequiredService<CsvgVideoSession>();

        Assert.True(session.HasCaptureProvider);
        Assert.Equal(CaptureProviderNames.InEngine, session.CaptureProviderName);
        Assert.Equal(CaptureState.NotInitialized, session.CaptureState);
    }

    /// <summary>
    ///     Selection failures surface while the graph is being resolved, not halfway through capture
    ///     bring-up: a provider that cannot run on this platform makes <see cref="ICaptureSessionFactory" />
    ///     resolution throw rather than handing back a session that turns out to be unable to record.
    /// </summary>
    [Fact]
    public async Task AProviderUnsupportedHere_ThrowsInsteadOfResolvingASessionThatCannotRecord()
    {
        FakeCaptureSessionFactory unsupported = new()
        {
            ProviderName = CaptureProviderNames.InEngine,
            IsSupportedOnCurrentPlatform = false
        };

        await using ServiceProvider provider = BuildContainer(preConfigure: services => services
            .AddKeyedSingleton<ICaptureSessionFactory>(CaptureProviderNames.InEngine, unsupported));

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ICaptureSessionFactory>);

        Assert.Contains("not supported on this platform", ex.Message);
    }

    #endregion

    #region The DI-less path: With*

    /// <summary>
    ///     The smallest hand-built session the class documentation promises: a process manager and
    ///     nothing else. It builds, it owns a real engine host with a live current session, and it is
    ///     playback-only - every capture surface reports "not configured" rather than pretending.
    /// </summary>
    [Fact]
    public async Task WithProcessManagerOnly_BuildsAPlaybackOnlySessionOverAFreshHost()
    {
        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .Build();

        await using (session)
        {
            Assert.IsType<Cs2EngineHost>(session.EngineHost);
            Assert.Same(session.Engine, session.EngineHost.Current);

            Assert.False(session.HasCaptureProvider);
            Assert.Null(session.CaptureProviderName);
            Assert.Null(session.CaptureState);
            Assert.Equal(CsvgSessionState.Disconnected, session.State);
        }
    }

    /// <summary>
    ///     Neither a host nor a process manager means no CS2 process could ever be launched, which is a
    ///     build-time error rather than a session that fails on first start. The message names both ways
    ///     out.
    /// </summary>
    [Fact]
    public void WithNeitherHostNorProcessManager_RefusesToBuild()
    {
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => new CsvgVideoSessionBuilder().Build());

        Assert.Contains(nameof(CsvgVideoSessionBuilder.WithProcessManager), ex.Message);
        Assert.Contains(nameof(CsvgVideoSessionBuilder.WithEngineHost), ex.Message);
    }

    /// <summary>
    ///     A supplied engine host already owns a process manager, so it satisfies the requirement on its
    ///     own - and the builder uses that host rather than creating a second one, which is what lets a
    ///     host share it with an already-wired <c>Cs2GameService</c>.
    /// </summary>
    [Fact]
    public async Task WithEngineHost_SatisfiesTheProcessManagerRequirement_AndIsUsedAsIs()
    {
        Cs2EngineHost host = CreateHost();

        CsvgVideoSession session = new CsvgVideoSessionBuilder().WithEngineHost(host).Build();
        await using (session)
        {
            Assert.Same(host, session.EngineHost);
        }
    }

    /// <summary>
    ///     When both are supplied the host wins: the process manager exists only to build a host when
    ///     none was given, so supplying both must not quietly produce a second host that the gRPC layer
    ///     knows nothing about.
    /// </summary>
    [Fact]
    public async Task WithEngineHostAndProcessManager_TheHostWins()
    {
        Cs2EngineHost host = CreateHost();
        Mock<ICs2ProcessManager> unused = new(MockBehavior.Strict);

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(unused.Object)
            .WithEngineHost(host)
            .Build();

        await using (session)
        {
            Assert.Same(host, session.EngineHost);
        }

        unused.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WithCaptureProvider_ReachesTheBuiltSession()
    {
        FakeCaptureSessionFactory factory = new() { ProviderName = "Fake" };

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithCaptureProvider(factory)
            .Build();

        await using (session)
        {
            Assert.True(session.HasCaptureProvider);
            Assert.Equal("Fake", session.CaptureProviderName);
            Assert.Equal(CaptureState.NotInitialized, session.CaptureState);
        }
    }

    /// <summary>
    ///     The three optional collaborators take null on purpose - null capture means playback-only,
    ///     null ffmpeg means no concatenation, null probe means no endpoint verification - so passing
    ///     null must build, not throw. The asymmetry with the guarded setters below is deliberate and
    ///     worth pinning.
    /// </summary>
    [Fact]
    public async Task TheOptionalCollaborators_AcceptNull()
    {
        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithCaptureProvider(null)
            .WithFfmpegService(null)
            .WithGrpcEndpointProbe(null)
            .Build();

        await using (session)
        {
            Assert.False(session.HasCaptureProvider);
        }
    }

    /// <summary>
    ///     Every setter whose argument the session cannot do without rejects null at the call site,
    ///     where the caller can still see which line was wrong, rather than at Build or first use.
    /// </summary>
    [Fact]
    public void TheRequiredCollaborators_RejectNull()
    {
        CsvgVideoSessionBuilder builder = new();

        Assert.Throws<ArgumentNullException>(() => builder.WithLoggerFactory(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithOptions((Cs2VideoGeneratorOptions)null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithOptions((IOptions<Cs2VideoGeneratorOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithProcessManager(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithEngineHost(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithClipCaptureCoordinator(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithCompilationRunner(null!));
    }

    /// <summary>
    ///     Every setter returns the same builder, which is what makes the documented one-expression
    ///     construction work rather than silently configuring a discarded copy.
    /// </summary>
    [Fact]
    public void EverySetter_ReturnsTheSameBuilder()
    {
        CsvgVideoSessionBuilder builder = new();
        ClipCaptureCoordinator coordinator = new();

        Assert.Same(builder, builder.WithLoggerFactory(NullLoggerFactory.Instance));
        Assert.Same(builder, builder.WithOptions(new Cs2VideoGeneratorOptions()));
        Assert.Same(builder, builder.WithOptions(Options.Create(new Cs2VideoGeneratorOptions())));
        Assert.Same(builder, builder.WithProcessManager(new Mock<ICs2ProcessManager>().Object));
        Assert.Same(builder, builder.WithEngineHost(CreateHost()));
        Assert.Same(builder, builder.WithCaptureProvider(null));
        Assert.Same(builder, builder.WithClipCaptureCoordinator(coordinator));
        Assert.Same(builder, builder.WithCompilationRunner(new Cs2CompilationRunner(coordinator)));
        Assert.Same(builder, builder.WithFfmpegService(null));
        Assert.Same(builder, builder.WithGrpcEndpointProbe(null));
    }

    #endregion

    #region Options and the endpoint probe reach the session

    /// <summary>
    ///     Options and the endpoint probe both have to travel builder → engine host → engine session to
    ///     be worth anything, and the probe is the cheapest place to observe both at once: starting a
    ///     session probes the port the options carry, before anything is launched. A configured probe
    ///     that reports nothing listening must fail the start with that port named.
    /// </summary>
    [Fact]
    public async Task WithOptionsAndProbe_TheProbeChecksTheConfiguredPort_BeforeAnythingLaunches()
    {
        RecordingEndpointProbe probe = new() { IsListening = false };
        Mock<ICs2ProcessManager> processManager = new();

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(processManager.Object)
            .WithOptions(new Cs2VideoGeneratorOptions { MockMode = true, GrpcPort = 61234 })
            .WithGrpcEndpointProbe(probe)
            .Build();

        await using (session)
        {
            InvalidOperationException ex = await Assert
                .ThrowsAsync<InvalidOperationException>(() => session.StartAsync(TestContext.Current.CancellationToken))
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Contains("61234", ex.Message);
        }

        Assert.Equal([61234], probe.ProbedPorts);

        // The probe gates the launch rather than reporting on one that already happened.
        processManager.Verify(
            m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The overload an options monitor takes. It has to land the same way.</summary>
    [Fact]
    public async Task WithWrappedOptions_LandsTheSameWayAsThePlainOverload()
    {
        RecordingEndpointProbe probe = new() { IsListening = false };

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithOptions(Options.Create(new Cs2VideoGeneratorOptions { MockMode = true, GrpcPort = 61235 }))
            .WithGrpcEndpointProbe(probe)
            .Build();

        await using (session)
        {
            await Assert
                .ThrowsAsync<InvalidOperationException>(() => session.StartAsync(TestContext.Current.CancellationToken))
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal([61235], probe.ProbedPorts);
    }

    /// <summary>
    ///     The probe is only ever used by a host this builder creates, which the setter documents. A
    ///     supplied host already decided whether it probes, so the builder's probe is ignored rather than
    ///     bolted on as a second check - pinned here so nobody "fixes" it into a double probe.
    /// </summary>
    [Fact]
    public async Task WithGrpcEndpointProbe_IsIgnoredWhenAnEngineHostIsSupplied()
    {
        RecordingEndpointProbe probe = new() { IsListening = false };
        InvalidOperationException launchFailure = new("launch reached");

        Mock<ICs2ProcessManager> processManager = new();
        processManager
            .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(launchFailure);

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithEngineHost(CreateHost(processManager.Object))
            .WithGrpcEndpointProbe(probe)
            .Build();

        await using (session)
        {
            // The start gets all the way to launch, which is only possible if no probe ran.
            InvalidOperationException ex = await Assert
                .ThrowsAsync<InvalidOperationException>(() => session.StartAsync(TestContext.Current.CancellationToken))
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Same(launchFailure, ex);
        }

        Assert.Empty(probe.ProbedPorts);
    }

    #endregion

    #region The composed graph

    /// <summary>
    ///     The composition rule the builder's own documentation states: a runner it creates must share
    ///     this session's coordinator. Proven end to end rather than by inspection - a compilation runs
    ///     through the runner, and the capture state it drives comes out of the session's
    ///     <see cref="CsvgVideoSession.CaptureStateChanged" />. Two coordinators and this event never
    ///     fires.
    /// </summary>
    [Fact]
    public async Task ABuiltSession_ForwardsCaptureStateFromTheRunnerItComposed()
    {
        using TempDir dir = new();
        string demoPath = CreateDemo(dir);

        FakeCaptureSessionFactory captureFactory = new();
        Mock<ICs2ProcessManager> processManager = new();

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(processManager.Object)
            .WithOptions(new Cs2VideoGeneratorOptions { MockMode = true })
            .WithCaptureProvider(captureFactory)
            .Build();

        await using (session)
        {
            List<CaptureState> states = [];
            session.CaptureStateChanged += (_, newState) =>
            {
                lock (states)
                {
                    states.Add(newState);
                }

                return Task.CompletedTask;
            };

            await StartConnectedAsync(session, processManager, demoPath);

            // The backend refuses the clip, so the compilation fails fast without a plugin driving
            // playback - the state machine still has to have been driven and forwarded.
            captureFactory.CreatedSessions[0].BeginClipFailure = new InvalidOperationException("backend down");

            Cs2CompilationResult result = await session
                .RunCompilationAsync(Compilation(dir.Path, demoPath), TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(result.Success);

            lock (states)
            {
                Assert.NotEmpty(states);
                Assert.Contains(CaptureState.Error, states);
            }
        }
    }

    /// <summary>
    ///     A supplied coordinator is the one the session forwards and the one the runner it builds
    ///     reports through, so a caller sharing a coordinator with something outside this session still
    ///     gets one state machine rather than two.
    /// </summary>
    [Fact]
    public async Task WithClipCaptureCoordinator_IsTheCoordinatorTheComposedRunnerUses()
    {
        ClipCaptureCoordinator shared = new();

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithCaptureProvider(new FakeCaptureSessionFactory())
            .WithClipCaptureCoordinator(shared)
            .Build();

        await using (session)
        {
            // CaptureState reads the coordinator the session forwards; a compilation's clips are
            // captured through the runner's. One coordinator means one answer.
            Assert.Equal(shared.State, session.CaptureState);

            List<CaptureState> states = [];
            session.CaptureStateChanged += (_, newState) =>
            {
                lock (states)
                {
                    states.Add(newState);
                }

                return Task.CompletedTask;
            };

            // driving the supplied coordinator reaches the session's subscribers, which is only true
            // if the session subscribed to this instance
            await DriveOneFailedClipAsync(shared);

            lock (states)
            {
                Assert.Equal([CaptureState.PreparingEnvironment, CaptureState.Error], states);
            }

            Assert.Equal(CaptureState.Error, session.CaptureState);
        }
    }

    /// <summary>
    ///     A runner and a coordinator that are not the same pair is a silent split brain - the runner's
    ///     clips drive one state machine while the session forwards another - so it is refused at
    ///     construction instead of producing a session whose capture state never moves. This is the
    ///     misconfiguration <see cref="CsvgVideoSessionBuilder.WithCompilationRunner" /> warns about in
    ///     prose; here it is enforced.
    /// </summary>
    [Fact]
    public void ARunnerBuiltOverADifferentCoordinator_IsRefused()
    {
        Cs2CompilationRunner foreignRunner = new(new ClipCaptureCoordinator());

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithClipCaptureCoordinator(new ClipCaptureCoordinator())
            .WithCompilationRunner(foreignRunner)
            .Build());

        Assert.Contains(nameof(ClipCaptureCoordinator), ex.Message);
    }

    /// <summary>
    ///     The same refusal covers the easier mistake: supplying only a runner. The builder would
    ///     otherwise create a fresh coordinator the runner has never heard of, which looks like a
    ///     perfectly configured session right up until nothing reports any capture state.
    /// </summary>
    [Fact]
    public void ARunnerSuppliedWithoutItsCoordinator_IsRefused()
    {
        Cs2CompilationRunner runner = new(new ClipCaptureCoordinator());

        Assert.Throws<ArgumentException>(() => new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithCompilationRunner(runner)
            .Build());
    }

    /// <summary>
    ///     The supported way to share a compilation pipeline: hand over both halves of the pair. It
    ///     builds, and the session forwards the coordinator both sides hold.
    /// </summary>
    [Fact]
    public async Task ARunnerSuppliedWithItsOwnCoordinator_IsAccepted()
    {
        ClipCaptureCoordinator shared = new();

        CsvgVideoSession session = new CsvgVideoSessionBuilder()
            .WithProcessManager(new Mock<ICs2ProcessManager>().Object)
            .WithCaptureProvider(new FakeCaptureSessionFactory())
            .WithClipCaptureCoordinator(shared)
            .WithCompilationRunner(new Cs2CompilationRunner(shared))
            .Build();

        await using (session)
        {
            List<CaptureState> states = [];
            session.CaptureStateChanged += (_, newState) =>
            {
                lock (states)
                {
                    states.Add(newState);
                }

                return Task.CompletedTask;
            };

            await DriveOneFailedClipAsync(shared);

            lock (states)
            {
                Assert.Equal([CaptureState.PreparingEnvironment, CaptureState.Error], states);
            }
        }
    }

    #endregion

    #region Helpers

    private static ServiceProvider BuildContainer(Action<IServiceCollection>? preConfigure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cs2VideoGenerator:MockMode"] = "true" })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        preConfigure?.Invoke(services);
        services.AddCs2VideoGeneratorCore(configuration);
        return services.BuildServiceProvider();
    }

    private static Cs2EngineHost CreateHost(ICs2ProcessManager? processManager = null) => new(
        NullLogger.Instance,
        Options.Create(new Cs2VideoGeneratorOptions { MockMode = true }),
        processManager ?? new Mock<ICs2ProcessManager>().Object);

    /// <summary>
    ///     Brings a built session up with capture initialized, answering a load command with the
    ///     DemoLoaded status the engine session waits for.
    /// </summary>
    private static Task StartConnectedAsync(CsvgVideoSession session, Mock<ICs2ProcessManager> processManager,
        string demoPath)
    {
        const string sessionId = "builder-session";
        return session
            .StartConnectedAsync(processManager, sessionId, async request =>
            {
                if (request.CommandCase == Proto.CsControlRequest.CommandOneofCase.LoadDemo)
                {
                    await session.EngineSession().OnDemoPlaybackStatusChangedAsync(sessionId,
                        new Proto.DemoPlaybackStatusChange
                        {
                            NewStatus = Proto.DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded
                        });
                }
            }, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Runs one clip straight through the given coordinator against a backend that refuses to begin
    ///     it, which drives the state machine to PreparingEnvironment and then Error without needing a
    ///     plugin, a demo or a real engine. Whoever subscribed to this coordinator sees both.
    /// </summary>
    private static async Task DriveOneFailedClipAsync(ClipCaptureCoordinator coordinator)
    {
        CaptureJournal journal = new();
        FakeCaptureSession captureSession = new(journal)
        {
            BeginClipFailure = new InvalidOperationException("backend down")
        };

        await coordinator.CaptureClipAsync(
            new FakeEngineSession(journal),
            captureSession,
            new ClipCaptureContext
            {
                StartTick = 100,
                EndTick = 200,
                Metadata = new CaptureMetadata { OutputFileName = "clip", OutputDirectory = Path.GetTempPath() }
            },
            TestContext.Current.CancellationToken);
    }

    private static string CreateDemo(TempDir dir)
    {
        string path = dir.Combine("match.dem");
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    private static Cs2Compilation Compilation(string outputDirectory, string demoPath) => new()
    {
        Settings = new Cs2CompilationSettings
        {
            OutputDirectory = outputDirectory,
            BaseFileName = "highlight",
            ConcatenateClips = false
        },
        Clips =
        [
            new Cs2CompilationClip
            {
                PlayerSteamId = 76561198000000000,
                MatchChecksum = "checksum",
                DemoFilePath = demoPath,
                StartTick = 100,
                EndTick = 200,
                PlayerNameToSpectate = "s1mple"
            }
        ]
    };

    /// <summary>Records the ports it was asked about and answers with a scripted result.</summary>
    private sealed class RecordingEndpointProbe : IGrpcEndpointProbe
    {
        public List<int> ProbedPorts { get; } = [];

        public bool IsListening { get; init; } = true;

        public Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken = default)
        {
            ProbedPorts.Add(port);
            return Task.FromResult(IsListening);
        }
    }

    #endregion
}
