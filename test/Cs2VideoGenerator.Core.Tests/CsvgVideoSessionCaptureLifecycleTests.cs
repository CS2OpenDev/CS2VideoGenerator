#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Regression tests for the capture-side lifetime bugs the per-session object graph fixes: capture
///     initialization used to survive <see cref="CsvgVideoSession.StopAsync" />, so a Start→Stop→Start
///     cycle handed the second run a disposed provider. Capture is driven through a fake factory
///     registered in place of OBS, so the cycle is exercised on every platform without a real backend.
/// </summary>
public class CsvgVideoSessionCaptureLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static CsvgVideoSession CreateClient(
        out Mock<ICs2ProcessManager> processManager,
        out FakeCaptureSessionFactory captureFactory)
    {
        captureFactory = new FakeCaptureSessionFactory();
        return SessionHarness.CreateVideoSession(out processManager, captureFactory: captureFactory,
            coordinator: new ClipCaptureCoordinator());
    }

    private static async Task StartConnectedSessionAsync(
        CsvgVideoSession client,
        Mock<ICs2ProcessManager> processManager,
        string sessionId,
        Func<CsControlRequest, Task>? sendAsync = null)
    {
        await client
            .StartConnectedAsync(processManager, sessionId, sendAsync,
                cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, client.State);
    }

    /// <summary>
    ///     The headline bug. A Start→Stop→Start cycle with capture enabled gives the second run a
    ///     fresh capture session and disposes the first. Before M3 the <c>_captureInitialized</c> flag
    ///     survived the stop, so the second run skipped initialization and recorded through a
    ///     disposed provider.
    /// </summary>
    [Fact]
    public async Task StartStopStart_WithCaptureEnabled_CreatesAFreshCaptureSessionAndDisposesTheOldOne()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        await using (client)
        {
            await StartConnectedSessionAsync(client, processManager, "session-1");

            Assert.True(client.HasCaptureProvider);
            Assert.Equal(1, captureFactory.CheckAvailabilityCallCount);
            FakeCaptureSession first = Assert.Single(captureFactory.CreatedSessions);
            Assert.Equal(1, first.InitializeCallCount);
            Assert.Equal(0, first.ShutdownCallCount);

            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            // Shutdown is the reversible teardown; the object is not disposed until it is replaced.
            Assert.Equal(1, first.ShutdownCallCount);
            Assert.Equal(0, first.DisposeCallCount);

            await StartConnectedSessionAsync(client, processManager, "session-2");

            Assert.Equal(2, captureFactory.CheckAvailabilityCallCount);
            Assert.Equal(2, captureFactory.CreatedSessions.Count);

            FakeCaptureSession second = captureFactory.CreatedSessions[1];
            Assert.NotSame(first, second);
            Assert.Equal(1, second.InitializeCallCount);

            // The replaced session is disposed exactly once, so nothing can record through it again.
            Assert.Equal(1, first.DisposeCallCount);
            Assert.Equal(0, second.DisposeCallCount);
        }

        // Session disposal releases the live capture session too.
        Assert.Equal(1, captureFactory.CreatedSessions[1].DisposeCallCount);
    }

    /// <summary>
    ///     Clips go through the current run's capture session. The pre-M3 client reused the singleton
    ///     provider, so there was nothing to observe; now the second run's clip is created on the
    ///     second session.
    /// </summary>
    [Fact]
    public async Task StartStopStart_ClipsAreRecordedThroughTheCurrentSessionsCaptureBackend()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        await using (client)
        {
            await StartConnectedSessionAsync(client, processManager, "session-1");
            await RecordOneClipAsync(client);

            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            await StartConnectedSessionAsync(client, processManager, "session-2");
            await RecordOneClipAsync(client);

            Assert.Single(captureFactory.CreatedSessions[0].Clips);
            Assert.Single(captureFactory.CreatedSessions[1].Clips);
        }
    }

    /// <summary>
    ///     A single clip can carry a spectator target, which the compilation path always did and the old
    ///     public single-clip API could not express at all. The target is applied to the engine and
    ///     scheduled as a re-assert one tick before recording (the CSDM #1238 workaround: seeking into a
    ///     clip can drop the spectator lock, and only a re-assert fired by the engine itself survives
    ///     it), so a single clip now gets the same guard a compilation's clips get.
    /// </summary>
    [Fact]
    public async Task CaptureClip_WithASpectatorTarget_AppliesItAndSchedulesTheReassert()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager, out _);
        List<CsControlRequest> sent = [];

        await using (client)
        {
            await client
                .StartConnectedAsync(processManager, "session-1", cmd =>
                {
                    lock (sent)
                    {
                        sent.Add(cmd);
                    }

                    return Task.CompletedTask;
                }, capabilities: [CsvgCapabilities.TickScheduledCommands],
                    cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            // No demo is loaded, so playback fails and the clip comes back as a failed result - after
            // the spectator commands have already gone out.
            await client
                .CaptureClipAsync(new ClipCaptureContext
                {
                    StartTick = 500,
                    EndTick = 600,
                    Metadata = Metadata(),
                    SpectatorTarget = "s1mple",
                    ClipOptions = Cs2ClipOptions.XrayEnabled
                }, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        List<CsControlRequest> commands = [.. sent];

        // Options and spectator target are applied up front, exactly as the runner does, and exactly
        // once each. Assert.Single rather than Assert.Contains: the entry point and the coordinator
        // both used to apply them, and a Contains passes just as happily on two.
        Assert.Single(commands, c => c.CommandCase == CsControlRequest.CommandOneofCase.SetDemoOptions);
        Assert.Single(commands,
            c => c.CommandCase == CsControlRequest.CommandOneofCase.SetSpectatorTarget &&
                 c.SetSpectatorTarget.PlayerName == "s1mple");

        // ...and the engine-side re-assert is armed for the tick before recording starts.
        CsControlRequest schedule = Assert.Single(commands,
            c => c.CommandCase == CsControlRequest.CommandOneofCase.ScheduleCommandsAtTick);
        Assert.Equal(499, schedule.ScheduleCommandsAtTick.DemoTick);
        Assert.Equal(["spec_player \"s1mple\""], schedule.ScheduleCommandsAtTick.ConsoleCommands);
    }

    /// <summary>
    ///     Capture must be torn down before CS2 is killed. OBS's game-capture source is hooked into CS2,
    ///     and killing CS2 first makes OBS fault during its own teardown and pop an
    ///     interaction-blocking crash dialog - so this ordering is load-bearing, not cosmetic.
    /// </summary>
    [Fact]
    public async Task StopSession_TearsDownCaptureBeforeStoppingCs2()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        processManager
            .Setup(m => m.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                captureFactory.Journal.Add("cs2.stop");
                return Task.CompletedTask;
            });

        await using (client)
        {
            await StartConnectedSessionAsync(client, processManager, "session-1");
            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        List<string> entries = [.. captureFactory.Journal.Entries];
        int shutdownIndex = entries.IndexOf("session.shutdown");
        int cs2StopIndex = entries.IndexOf("cs2.stop");

        Assert.True(shutdownIndex >= 0, "the capture session was never shut down");
        Assert.True(cs2StopIndex >= 0, "CS2 was never stopped");
        Assert.True(shutdownIndex < cs2StopIndex,
            $"capture teardown must precede the CS2 stop; got {string.Join(" -> ", entries)}");
    }

    /// <summary>
    ///     The mirror image of <see cref="StopSession_TearsDownCaptureBeforeStoppingCs2" />, and the
    ///     reason <c>ConnectAsync</c> takes a pre-launch hook at all: a screen recorder's game-capture
    ///     source has to be hooked before the game it captures exists, so the capture session must be
    ///     created and initialized before CS2 is launched. Only the unavailable-provider shortcut was
    ///     pinned before this; the succeeding path - the one that actually runs - was not, so moving the
    ///     hook after the launch went unnoticed by every test.
    /// </summary>
    [Fact]
    public async Task StartSession_BringsCaptureUpBeforeLaunchingCs2()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        await using (client)
        {
            await client
                .StartConnectedAsync(processManager, "session-1",
                    onLaunchBeforeEvents: () => captureFactory.Journal.Add("cs2.launch"),
                    cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(CsvgSessionState.Connected, client.State);
        }

        List<string> entries = [.. captureFactory.Journal.Entries];
        int availabilityIndex = entries.IndexOf("factory.checkAvailability");
        int createIndex = entries.IndexOf("factory.create");
        int initializeIndex = entries.IndexOf("session.initialize");
        int launchIndex = entries.IndexOf("cs2.launch");

        Assert.True(availabilityIndex >= 0, "availability was never checked");
        Assert.True(createIndex >= 0, "no capture session was created");
        Assert.True(initializeIndex >= 0, "the capture session was never initialized");
        Assert.True(launchIndex >= 0, "CS2 was never launched");

        string order = string.Join(" -> ", entries);
        Assert.True(availabilityIndex < createIndex,
            $"availability must be checked before a capture session is created; got {order}");
        Assert.True(createIndex < initializeIndex,
            $"the capture session must be created before it is initialized; got {order}");
        Assert.True(initializeIndex < launchIndex,
            $"capture bring-up must precede the CS2 launch; got {order}");
    }

    /// <summary>
    ///     The public re-initialization entry point has to follow the same lifetime rule as the start
    ///     path: replacing the capture session disposes the one it replaces. Dropping it instead would
    ///     leak its process manager and leave its event handlers attached, which is the very bug class
    ///     the per-session object graph exists to remove.
    /// </summary>
    [Fact]
    public async Task InitializeCaptureAsync_AfterAStop_ReplacesAndDisposesThePreviousCaptureSession()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        await using (client)
        {
            await StartConnectedSessionAsync(client, processManager, "session-1");
            FakeCaptureSession first = Assert.Single(captureFactory.CreatedSessions);

            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, first.DisposeCallCount);

            CaptureAvailabilityResult result = await client
                .InitializeCaptureAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(result.IsAvailable);
            Assert.Equal(2, captureFactory.CreatedSessions.Count);
            Assert.Equal(1, first.DisposeCallCount);
            Assert.Equal(1, captureFactory.CreatedSessions[1].InitializeCallCount);
        }
    }

    /// <summary>
    ///     A capture session's token tracks the CS2 run, not the caller's startup token: it must still be
    ///     live once <see cref="CsvgVideoSession.StartAsync(CancellationToken)" /> has returned (a
    ///     bring-up token is typically scoped to the call and disposed right after), and it must fire when
    ///     the session is replaced so a backend's background work stops.
    /// </summary>
    [Fact]
    public async Task CaptureSessionToken_OutlivesStartup_AndFiresWhenTheSessionIsReplaced()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        await using (client)
        {
            using (CancellationTokenSource startupCts = new())
            {
                await StartConnectedSessionAsync(client, processManager, "session-1");
                await startupCts.CancelAsync();
            }

            CancellationToken sessionToken = Assert.Single(captureFactory.Contexts).SessionToken;
            Assert.True(sessionToken.CanBeCanceled);
            Assert.False(sessionToken.IsCancellationRequested);

            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            // A stop is reversible, so the token survives it; replacing the session is what ends it.
            Assert.False(sessionToken.IsCancellationRequested);

            await StartConnectedSessionAsync(client, processManager, "session-2");
            Assert.True(sessionToken.IsCancellationRequested);
            Assert.False(captureFactory.Contexts[1].SessionToken.IsCancellationRequested);
        }
    }

    /// <summary>
    ///     Backends that register against the loaded demo (HLAE's mirv handshake) need to know when the
    ///     engine actually has one open, so the demo-loaded hook fires after the engine load completes.
    ///     Nothing consumes it until the HLAE provider ships, but the call site has to exist for it to
    ///     land against.
    /// </summary>
    [Fact]
    public async Task LoadDemo_NotifiesTheCaptureSessionAfterTheEngineHasTheDemoOpen()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);

        await using (client)
        {
            // The "plugin" answers a load command with the DemoLoaded status the engine session waits for.
            await StartConnectedSessionAsync(client, processManager, "session-1", async request =>
            {
                if (request.CommandCase == CsControlRequest.CommandOneofCase.LoadDemo)
                {
                    await client.EngineSession().OnDemoPlaybackStatusChangedAsync("session-1",
                        new DemoPlaybackStatusChange
                        {
                            NewStatus = DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded
                        });
                }
            });

            string demoPath = Path.Combine(Path.GetTempPath(), "match.dem");
            await LoadDemoAsync(client, demoPath);

            Assert.Equal([demoPath], Assert.Single(captureFactory.CreatedSessions).DemosLoaded);
        }
    }

    /// <summary>
    ///     A session started with capture explicitly disabled (the watch / --no-capture shape) must not
    ///     bring a capture backend up at all.
    /// </summary>
    [Fact]
    public async Task StartSession_WithCaptureDisabled_NeverCreatesACaptureSession()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        await using (client)
        {
            await client
                .StartConnectedAsync(processManager, "session-1", initializeCapture: false,
                    cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(CsvgSessionState.Connected, client.State);
            Assert.Equal(0, captureFactory.CheckAvailabilityCallCount);
            Assert.Empty(captureFactory.CreatedSessions);
        }
    }

    /// <summary>
    ///     A provider that reports itself unavailable fails the start rather than launching CS2 into a
    ///     session that can never record - the check is engine-free precisely so it can run first.
    /// </summary>
    [Fact]
    public async Task StartSession_WithUnavailableCaptureProvider_FailsBeforeLaunchingCs2()
    {
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);
        captureFactory.Availability = CaptureAvailabilityResult.Failed("OBS Studio executable not found");

        await using (client)
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.StartAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

            Assert.Contains("OBS Studio executable not found", ex.Message);
            Assert.Equal(CsvgSessionState.Faulted, client.State);
            Assert.Empty(captureFactory.CreatedSessions);
            processManager.Verify(
                m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    #region Helpers

    private static CaptureMetadata Metadata() => new()
    {
        OutputFileName = "clip",
        OutputDirectory = Path.GetTempPath()
    };

    /// <summary>
    ///     Records one clip. The engine session is never actually playing a demo, so the clip fails
    ///     inside the coordinator - which is fine here: what these tests assert is which capture session
    ///     the clip was created on, and a failed clip is a returned result rather than an exception
    ///     either way.
    /// </summary>
    private static async Task RecordOneClipAsync(CsvgVideoSession client)
    {
        CaptureResult result = await client
            .CaptureClipAsync(
                new ClipCaptureContext { StartTick = 100, EndTick = 200, Metadata = Metadata() },
                TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    /// <summary>Loads a demo the same way the compilation pipeline does.</summary>
    private static async Task LoadDemoAsync(CsvgVideoSession client, string demoPath) =>
        await client.LoadDemoAsync(demoPath, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

    #endregion
}
