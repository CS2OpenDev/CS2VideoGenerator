#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Tests for <see cref="CsvgVideoSession" /> lifecycle races: stale-session teardown must
///     not fault a reconnected session, a Ready status delivered while LaunchAsync is still running
///     must not be lost, a CS2 process dying during startup must fail fast with its exit code, and a
///     cancelled <see cref="CsvgVideoSession.WaitForEndAsync" /> must not poison the shared
///     session-ended signal for other waiters.
/// </summary>
public class CsvgVideoSessionLifecycleTests
{
    /// <summary>
    ///     Upper bound for operations that should complete (or fail) quickly. Well below the
    ///     2-minute plugin-connect timeout so a regression surfaces as a fast test failure, not a hang.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Starts a connected session (the plugin handshake is driven from inside LaunchAsync, i.e.
    ///     during the post-launch delay window) and asserts it reaches Connected.
    /// </summary>
    private static async Task StartConnectedSessionAsync(
        CsvgVideoSession client,
        Mock<ICs2ProcessManager> processManager,
        string sessionId,
        Func<CsControlRequest, Task>? sendAsync = null,
        Action? requestStop = null)
    {
        await client
            .StartConnectedAsync(processManager, sessionId, sendAsync, requestStop,
                cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, client.State);
    }

    /// <summary>
    ///     The Ready status here is delivered while LaunchAsync is still running - before the old code
    ///     installed its ProcessStatusChanged subscription (it subscribed only after LaunchAsync
    ///     returned, including the post-launch delay). With the fix (subscribe before launch and consult
    ///     LastProcessStatus after it) the session must connect instead of timing out.
    /// </summary>
    [Fact]
    public async Task StartSession_ReadyDeliveredDuringLaunchWindow_Connects()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);

        await StartConnectedSessionAsync(client, processManager, "session-1");

        Assert.Equal("session-1", client.Engine.SessionId);
        Assert.Equal(Cs2ProcessStatus.Types.ProcessStatus.Ready, client.Engine.LastProcessStatus?.Status);
    }

    /// <summary>
    ///     When CS2 dies during startup (Steam not running, plugin crash on load, ...) the ready-wait
    ///     must fail immediately with the exit code in the message instead of burning the full
    ///     2-minute connect timeout with a generic TimeoutException.
    /// </summary>
    [Fact]
    public async Task StartSession_ProcessExitsDuringStartup_FailsFastWithExitCodeInMessage()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        processManager
            .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Raises(m => m.Exited += null,
                new ProcessExitedEventArgs(new ProcessExitResult(42, ProcessExitReason.Completed)));

        // WaitAsync(TestTimeout) guarantees "fail fast": if the old 2-minute timeout path were still
        // taken this would surface as a TimeoutException and fail the ThrowsAsync assertion.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        // Same message shape every process-exit-abandoned wait now carries (see
        // Cs2EngineSessionProcessExitTests), so a crash reads the same wherever it interrupts a run.
        Assert.Contains("CS2 process exited (exit code 42", ex.Message);
        Assert.Contains("connect and report Ready", ex.Message);
        Assert.Equal(CsvgSessionState.Faulted, client.State);
    }

    /// <summary>
    ///     A stale session ending after the plugin reconnected (new session took over) must not wipe
    ///     the new session's command sender, clear its id, or fault the video session.
    /// </summary>
    [Fact]
    public async Task StaleSessionEnd_DoesNotTearDownReconnectedSession()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        List<CsControlRequest> newSessionCommands = [];

        await StartConnectedSessionAsync(client, processManager, "session-old");

        // Plugin reconnects: a new session takes over as current.
        await client.EngineSession().OnSessionStartedAsync("session-new");
        client.EngineSession().OnCommandSenderAvailable("session-new", cmd =>
        {
            newSessionCommands.Add(cmd);
            return Task.CompletedTask;
        });

        // The old session's read loop now ends - after the reconnect.
        await client.EngineSession().OnSessionEndedAsync("session-old", 123, DateTimeOffset.UtcNow);

        Assert.Equal(CsvgSessionState.Connected, client.State);
        Assert.Equal("session-new", client.Engine.SessionId);

        // The new session's command sender must still work.
        await client.Engine.ResumeDemoAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Single(newSessionCommands);
    }

    /// <summary>
    ///     The current session ending unexpectedly must still tear down: client faults, session id and
    ///     command sender are cleared, and subsequent commands fail immediately (not after a timeout).
    ///     StopAsync is then the documented reset path back to Disconnected, after which a new
    ///     session can be started.
    /// </summary>
    [Fact]
    public async Task CurrentSessionEnd_FaultsClient_AndStopSessionResets()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        await StartConnectedSessionAsync(client, processManager, "session-1");

        await client.EngineSession().OnSessionEndedAsync("session-1", 5, DateTimeOffset.UtcNow);

        Assert.Equal(CsvgSessionState.Faulted, client.State);
        Assert.Null(client.Engine.SessionId);

        // Commands fail fast with a clear message instead of vanishing into a timeout.
        InvalidOperationException sendEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Engine.ResumeDemoAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("no active session", sendEx.Message);

        // Starting directly from Faulted is rejected with a hint at the reset path.
        InvalidOperationException startEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("StopAsync", startEx.Message);

        // StopAsync is the reset path: it returns the session to Disconnected...
        await client.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Disconnected, client.State);
        processManager.Verify(m => m.StopAsync(It.IsAny<CancellationToken>()), Times.Once);

        // ...after which a new session can be started.
        await StartConnectedSessionAsync(client, processManager, "session-2");
    }

    /// <summary>
    ///     A cancelled WaitForEndAsync must not poison the shared session-ended signal:
    ///     a second waiter must still complete when the session actually ends.
    /// </summary>
    [Fact]
    public async Task WaitForSessionEnd_CancelledWaiter_DoesNotPoisonLaterWaiters()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        await StartConnectedSessionAsync(client, processManager, "session-1");

        using CancellationTokenSource cts = new();
        Task firstWait = client.WaitForEndAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        // A second wait must still be pending, and must complete when the session ends.
        Task secondWait = client.WaitForEndAsync(TestContext.Current.CancellationToken);
        Assert.False(secondWait.IsCompleted);

        await client.EngineSession().OnSessionEndedAsync("session-1", 99, DateTimeOffset.UtcNow);
        await secondWait.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendCommand_WithoutSession_FailsImmediately()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out _);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Engine.PauseDemoAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("no active session", ex.Message);
    }

    /// <summary>
    ///     A subscriber throwing from an async event must neither prevent later subscribers from
    ///     running nor destabilize the session (fire-and-forget dispatch used to surface it as an
    ///     unobserved task exception and skip the remaining handlers).
    /// </summary>
    [Fact]
    public async Task EventDispatch_ThrowingSubscriber_DoesNotSkipLaterSubscribers()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);

        bool laterSubscriberRan = false;
        client.SessionStarted += _ => throw new InvalidOperationException("subscriber boom");
        client.SessionStarted += _ =>
        {
            laterSubscriberRan = true;
            return Task.CompletedTask;
        };

        // Session startup drives the SessionStarted event through the throwing subscriber.
        await StartConnectedSessionAsync(client, processManager, "session-1");

        Assert.True(laterSubscriberRan);
    }

    /// <summary>
    ///     Two concurrent starts must not both proceed. The Disconnected check runs under the video
    ///     session's gate, but the flip to <see cref="CsvgSessionState.Connecting" /> used to happen
    ///     after the lock was released, so a caller arriving in that window saw Disconnected, passed the
    ///     check and started a second session on top of the first (replacing its engine session
    ///     mid-startup). The claim is now part of the same lock acquisition as the check.
    /// </summary>
    /// <remarks>
    ///     The window is reproduced deterministically rather than raced: the only await between the
    ///     claim and the announcement is the disposal of the previous run's capture session (a stop
    ///     leaves that object behind for the next start to dispose), so a capture session that parks
    ///     inside <c>DisposeAsync</c> holds a start exactly there.
    /// </remarks>
    [Fact]
    public async Task StartSession_WhileAnotherStartIsClaimingTheSession_IsRejected()
    {
        BlockingCaptureFactory captureFactory = new();
        CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager,
            captureFactory: captureFactory, coordinator: new ClipCaptureCoordinator());

        await using (client)
        {
            // First run: connect with capture, then stop. The stop shuts the capture session down but
            // leaves the object for the next start to dispose.
            await StartConnectedSessionAsync(client, processManager, "session-1");
            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            // Second run parks inside that disposal, holding the claim on the session.
            captureFactory.BlockDisposal();
            Task parkedStart = client.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await captureFactory.DisposalEntered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

                // Third run: rejected, because the second one already left Disconnected under the gate.
                // WaitAsync bounds the call: a client that lets it through instead spends the whole
                // startup path here, and the TimeoutException fails this assertion promptly.
                InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.StartAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
                Assert.Contains("current state is Connecting", ex.Message);
                Assert.Equal(1, captureFactory.CreatedSessionCount);
            }
            finally
            {
                // Whatever happened above, nothing may stay parked: session disposal awaits the same
                // capture-session disposal, so a held block would wedge the test run instead of
                // failing it.
                captureFactory.ReleaseDisposal();
            }

            // The parked start still completes normally once disposal is released.
            await parkedStart.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(CsvgSessionState.Connected, client.State);
            Assert.Equal(2, captureFactory.CreatedSessionCount);

            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     Two concurrent stops must not both tear the session down. The Disconnected check ran outside
    ///     the video session's gate and the flip to <see cref="CsvgSessionState.Disconnecting" /> came
    ///     after it, so a second caller arriving in that window ran the whole teardown again: a second
    ///     capture shutdown, a second engine stop, and - the consequence that actually corrupts - a
    ///     second announcement of <see cref="CsvgSessionState.Disconnected" /> published while the first
    ///     stop was still killing CS2, which is enough for a concurrent start to be accepted against a
    ///     process that is still dying. The claim is now part of the same lock acquisition as the check.
    /// </summary>
    /// <remarks>
    ///     Reproduced deterministically rather than raced: the first await a claiming stop performs is
    ///     the capture backend's <c>ShutdownAsync</c>, so a capture session that parks there holds one
    ///     stop exactly inside the window while a second is attempted. Against the previous session the
    ///     second stop is accepted and parks on that same shutdown, so this fails as a TimeoutException
    ///     on the bounded second call rather than on a state assertion; the shutdown count guards the
    ///     other half - released, that second stop would take it to 2.
    /// </remarks>
    [Fact]
    public async Task StopSession_WhileAnotherStopIsTearingItDown_IsANoOp()
    {
        BlockingCaptureFactory captureFactory = new();
        CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager,
            captureFactory: captureFactory, coordinator: new ClipCaptureCoordinator());

        await using (client)
        {
            List<(CsvgSessionState Old, CsvgSessionState New)> transitions = [];
            client.StateChanged += (oldState, newState) =>
            {
                lock (transitions)
                {
                    transitions.Add((oldState, newState));
                }

                return Task.CompletedTask;
            };

            await StartConnectedSessionAsync(client, processManager, "session-1");

            // The first stop parks inside the capture backend's shutdown, holding the claim.
            captureFactory.BlockShutdown();
            Task parkedStop = client.StopAsync(TestContext.Current.CancellationToken);
            try
            {
                await captureFactory.ShutdownEntered.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
                Assert.Equal(CsvgSessionState.Disconnecting, client.State);

                // The second stop must return without running any of the teardown. WaitAsync bounds it:
                // a session that lets it through parks on the very same capture shutdown instead, and
                // the TimeoutException fails this promptly rather than hanging the run.
                await client.StopAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

                // Still Disconnecting: the loser must not publish a terminal state under the winner.
                Assert.Equal(CsvgSessionState.Disconnecting, client.State);
                Assert.Equal(1, captureFactory.ShutdownCallCount);
                processManager.Verify(m => m.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                // Whatever happened above, nothing may stay parked: session disposal stops the session
                // too, so a held block would wedge the test run instead of failing it.
                captureFactory.ReleaseShutdown();
            }

            await parkedStop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(CsvgSessionState.Disconnected, client.State);
            Assert.Equal(1, captureFactory.ShutdownCallCount);
            processManager.Verify(m => m.StopAsync(It.IsAny<CancellationToken>()), Times.Once);

            // Exactly one stop was announced, and the Disconnecting transition carries the state the
            // session was actually in rather than a hardcoded one.
            lock (transitions)
            {
                Assert.Equal(
                    [(CsvgSessionState.Connected, CsvgSessionState.Disconnecting),
                        (CsvgSessionState.Disconnecting, CsvgSessionState.Disconnected)],
                    transitions.SkipWhile(t => t.New != CsvgSessionState.Disconnecting).ToArray());
            }
        }
    }

    /// <summary>
    ///     A capture factory whose sessions can be made to park inside <c>DisposeAsync</c> or
    ///     <c>ShutdownAsync</c> - the first await each of
    ///     <see cref="CsvgVideoSession.StartAsync(CancellationToken)" /> and
    ///     <see cref="CsvgVideoSession.StopAsync" /> performs after claiming the session, which is what
    ///     makes both claim races reproducible rather than raced.
    /// </summary>
    private sealed class BlockingCaptureFactory : ICaptureSessionFactory
    {
        private readonly TaskCompletionSource _disposalEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _shutdownEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource? _release;
        private TaskCompletionSource? _shutdownRelease;
        private int _createdSessionCount;
        private int _shutdownCallCount;

        /// <summary>Completes when a session's disposal is reached.</summary>
        public Task DisposalEntered => _disposalEntered.Task;

        /// <summary>Completes when a session's shutdown is reached.</summary>
        public Task ShutdownEntered => _shutdownEntered.Task;

        /// <summary>How many capture sessions this factory has handed out.</summary>
        public int CreatedSessionCount => Volatile.Read(ref _createdSessionCount);

        /// <summary>How many times a session of this factory has been shut down.</summary>
        public int ShutdownCallCount => Volatile.Read(ref _shutdownCallCount);

        /// <summary>Makes the next disposal park until <see cref="ReleaseDisposal" /> is called.</summary>
        public void BlockDisposal() =>
            Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Lets a parked disposal (and every later one) finish.</summary>
        public void ReleaseDisposal()
        {
            TaskCompletionSource? release = Volatile.Read(ref _release);
            Volatile.Write(ref _release, null);
            release?.TrySetResult();
        }

        /// <summary>Makes the next shutdown park until <see cref="ReleaseShutdown" /> is called.</summary>
        public void BlockShutdown() =>
            Volatile.Write(ref _shutdownRelease,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Lets a parked shutdown (and every later one) finish.</summary>
        public void ReleaseShutdown()
        {
            TaskCompletionSource? release = Volatile.Read(ref _shutdownRelease);
            Volatile.Write(ref _shutdownRelease, null);
            release?.TrySetResult();
        }

        /// <inheritdoc />
        public string ProviderName => "Blocking";

        /// <inheritdoc />
        public bool IsSupportedOnCurrentPlatform => true;

        /// <inheritdoc />
        public Task<CaptureAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CaptureAvailabilityResult.Succeeded());

        /// <inheritdoc />
        public ICaptureSession Create(CaptureSessionContext context)
        {
            Interlocked.Increment(ref _createdSessionCount);
            return new Session(this);
        }

        private sealed class Session(BlockingCaptureFactory owner) : ICaptureSession
        {
            // Nothing in this test records or fails a clip, so neither diagnostic event is ever raised.
#pragma warning disable CS0067

            /// <inheritdoc />
            public event EventHandler<CaptureProgressEventArgs>? ProgressUpdated;

            /// <inheritdoc />
            public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

#pragma warning restore CS0067

            /// <inheritdoc />
            public string ProviderName => owner.ProviderName;

            /// <inheritdoc />
            public CaptureTraits Traits => new()
            {
                TimingModel = CaptureTimingModel.WallClock,
                UsesEngine = false,
                HonorsFrameRate = false
            };

            /// <inheritdoc />
            public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<IClipCapture> BeginClipAsync(ClipCaptureContext context,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("This test never records a clip.");

            /// <inheritdoc />
            public async Task ShutdownAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._shutdownCallCount);
                owner._shutdownEntered.TrySetResult();

                if (Volatile.Read(ref owner._shutdownRelease) is { } release)
                {
                    await release.Task;
                }
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                owner._disposalEntered.TrySetResult();

                if (Volatile.Read(ref owner._release) is { } release)
                {
                    await release.Task;
                }
            }
        }
    }
}
