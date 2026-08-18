#region

using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Regression tests for the fresh-engine-session-per-start lifetime rule: every
///     <see cref="CsvgVideoSession.StartAsync(CancellationToken)" /> creates a new engine session, so
///     per-session state (latched ticks, capabilities, process status, pending command waiters)
///     structurally cannot leak across a Start→Stop→Start cycle, pending waiters are failed on stop
///     instead of burning their timeouts, and session-level event subscribers survive the session
///     replacement. All tests run the capture-disabled path (no capture provider is configured),
///     matching the watch/--no-capture session shape.
/// </summary>
public class CsvgVideoSessionRestartTests
{
    /// <summary>
    ///     Upper bound for operations that should complete (or fail) quickly. Well below the client's
    ///     2-minute plugin-connect timeout and the command-ack timeouts, so a regression surfaces as
    ///     a fast test failure, not a hang.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static DemoPlaybackStatusChange StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus status) =>
        new() { NewStatus = status };

    /// <summary>
    ///     Starts a connected session through <see cref="SessionHarness.StartConnectedAsync" /> and
    ///     asserts it reaches Connected.
    /// </summary>
    private static async Task StartConnectedSessionAsync(
        CsvgVideoSession client,
        Mock<ICs2ProcessManager> processManager,
        string sessionId,
        Func<CsControlRequest, Task>? sendAsync = null,
        Action? requestStop = null,
        string[]? capabilities = null,
        Action? onLaunchBeforeEvents = null)
    {
        await client
            .StartConnectedAsync(processManager, sessionId, sendAsync, requestStop, capabilities,
                onLaunchBeforeEvents, initializeCapture: false, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, client.State);
    }

    /// <summary>
    ///     Session 1's latched engine truth (last tick, pause flag, playback status, process status)
    ///     and its capability set must not be visible after a restart until session 2's handshake
    ///     delivers something. Nothing "cleans up" the old values - the restart just builds a fresh
    ///     session object. That is the structural fix for the whole stale-lifetime bug class, where
    ///     a leftover Ready or capability set satisfies a new session's gates.
    /// </summary>
    [Fact]
    public async Task Restart_LatchedTruthAndCapabilities_NotVisibleBeforeNewHandshake()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        await StartConnectedSessionAsync(client, processManager, "session-1",
            capabilities: [CsvgCapabilities.CommandAck, CsvgCapabilities.TickScheduledCommands]);

        // Latch engine truth on session 1.
        client.EngineSession().OnTickUpdated("session-1", 500, isPaused: true);
        await client.EngineSession().OnDemoPlaybackStatusChangedAsync("session-1",
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded));

        Assert.Equal(500, client.Engine.LastTick);
        Assert.True(client.Engine.LastTickIsPaused);
        Assert.True(client.Engine.Supports(CsvgCapabilities.CommandAck));
        Assert.NotNull(client.Engine.LastProcessStatus);
        Assert.NotNull(client.Engine.LastDemoPlaybackStatusChange);

        await client.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // sample the engine-truth view during the second start, before session 2 delivers any
        // event: everything session 1 latched must already be gone
        long? tickBeforeHandshake = -1;
        bool? isPausedBeforeHandshake = true;
        bool supportsBeforeHandshake = true;
        Cs2ProcessStatus? statusBeforeHandshake = SessionHarness.ReadyStatus();
        DemoPlaybackStatusChange? playbackBeforeHandshake =
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded);

        await StartConnectedSessionAsync(client, processManager, "session-2",
            onLaunchBeforeEvents: () =>
            {
                tickBeforeHandshake = client.Engine.LastTick;
                isPausedBeforeHandshake = client.Engine.LastTickIsPaused;
                supportsBeforeHandshake = client.Engine.Supports(CsvgCapabilities.CommandAck);
                statusBeforeHandshake = client.Engine.LastProcessStatus;
                playbackBeforeHandshake = client.Engine.LastDemoPlaybackStatusChange;
            });

        Assert.Null(tickBeforeHandshake);
        Assert.Null(isPausedBeforeHandshake);
        Assert.False(supportsBeforeHandshake);
        Assert.Null(statusBeforeHandshake);
        Assert.Null(playbackBeforeHandshake);

        // Session 2 advertised no capabilities, so session 1's set must not resurface after the
        // new handshake either.
        Assert.Empty(client.Engine.PluginCapabilities);
        Assert.False(client.Engine.Supports(CsvgCapabilities.CommandAck));
        Assert.Null(client.Engine.LastTick);
        Assert.Equal("session-2", client.Engine.SessionId);
    }

    /// <summary>
    ///     StopAsync fails pending command waiters at once instead of letting them burn their full
    ///     ack/query timeouts - the plugin can never answer once the session is stopped. The
    ///     generous timeout seams are set before the session starts, which incidentally proves the
    ///     session-level test seams survive the fresh-session-per-start replacement.
    /// </summary>
    [Fact]
    public async Task Stop_FailsPendingCommandWaitersFast()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);

        // far above TestTimeout: if stop failed to release the waiters, the asserts below hit the
        // WaitAsync bound rather than passing on a slow ack-timeout expiry
        client.CommandAckTimeout = TimeSpan.FromSeconds(60);
        client.DemoStateQueryTimeout = TimeSpan.FromSeconds(60);

        await StartConnectedSessionAsync(client, processManager, "session-1",
            capabilities:
            [
                CsvgCapabilities.CommandAck, CsvgCapabilities.TickScheduledCommands,
                CsvgCapabilities.DemoStateEvents
            ]);

        // The sender accepts both commands but never acknowledges them.
        Task scheduleTask = client.Engine.ScheduleCommandsAtTickAsync(128, ["spec_mode 1"],
            TestContext.Current.CancellationToken);
        Task queryTask = client.Engine.QueryDemoStateAsync(TestContext.Current.CancellationToken);
        Assert.False(scheduleTask.IsCompleted);
        Assert.False(queryTask.IsCompleted);

        await client.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        InvalidOperationException scheduleEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduleTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("stopped before the command completed", scheduleEx.Message);

        InvalidOperationException queryEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("stopped before the command completed", queryEx.Message);
    }

    /// <summary>
    ///     Commands issued after a restart route to the new session's sender only. The replaced
    ///     session's command channel is dead, not lingering.
    /// </summary>
    [Fact]
    public async Task Restart_AfterStop_RoutesCommandsToNewSessionOnly()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        List<CsControlRequest> firstSessionCommands = [];
        List<CsControlRequest> secondSessionCommands = [];

        await StartConnectedSessionAsync(client, processManager, "session-1", cmd =>
        {
            lock (firstSessionCommands)
            {
                firstSessionCommands.Add(cmd);
            }

            return Task.CompletedTask;
        });

        await client.Engine.ResumeDemoAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Single(firstSessionCommands);

        await client.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Disconnected, client.State);

        await StartConnectedSessionAsync(client, processManager, "session-2", cmd =>
        {
            lock (secondSessionCommands)
            {
                secondSessionCommands.Add(cmd);
            }

            return Task.CompletedTask;
        });

        Assert.Equal("session-2", client.Engine.SessionId);

        await client.Engine.ResumeDemoAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Single(secondSessionCommands);
        Assert.Single(firstSessionCommands); // Unchanged: nothing leaked back to session 1.
    }

    /// <summary>
    ///     Subscribers added to the video session while engine session A is current still fire when
    ///     B is current. The video session re-exposes engine truth over per-session forwarding
    ///     subscriptions, so the replacement is invisible to consumers - sync and async events both.
    /// </summary>
    [Fact]
    public async Task Restart_SessionEventSubscribersSurviveSessionReplacement()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        await StartConnectedSessionAsync(client, processManager, "session-1");

        // Subscribed while session 1 is current.
        int tickCount = 0;
        long lastObservedTick = 0;
        client.TickUpdated += (_, tick) =>
        {
            Interlocked.Increment(ref tickCount);
            Interlocked.Exchange(ref lastObservedTick, tick);
        };

        int playbackCount = 0;
        client.DemoPlaybackStatusChanged += (_, _) =>
        {
            Interlocked.Increment(ref playbackCount);
            return Task.CompletedTask;
        };

        client.EngineSession().OnTickUpdated("session-1", 64);
        await client.EngineSession().OnDemoPlaybackStatusChangedAsync("session-1",
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Playing));
        Assert.Equal(1, Volatile.Read(ref tickCount));
        Assert.Equal(1, Volatile.Read(ref playbackCount));

        await client.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await StartConnectedSessionAsync(client, processManager, "session-2");

        // the same subscriptions, no re-subscribe
        client.EngineSession().OnTickUpdated("session-2", 128);
        await client.EngineSession().OnDemoPlaybackStatusChangedAsync("session-2",
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused));

        Assert.Equal(2, Volatile.Read(ref tickCount));
        Assert.Equal(128, Volatile.Read(ref lastObservedTick));
        Assert.Equal(2, Volatile.Read(ref playbackCount));
    }

    /// <summary>
    ///     StopAsync makes the session terminal: a WaitForEndAsync waiter from before
    ///     the stop is released by the stop itself (the fresh-object rule - a stopped session can
    ///     never end "later"), rather than hanging until client disposal.
    /// </summary>
    [Fact]
    public async Task Stop_ReleasesWaitForSessionEndWaiters()
    {
        await using CsvgVideoSession client = SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        await StartConnectedSessionAsync(client, processManager, "session-1");

        Task waiter = client.WaitForEndAsync(TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        await client.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await waiter.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }
}
