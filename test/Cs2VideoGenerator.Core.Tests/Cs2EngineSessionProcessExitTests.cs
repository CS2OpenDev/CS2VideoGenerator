#region

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
///     Process-exit fast-fail on demo-control waits. A CS2 that dies under a wait can never deliver
///     the tick, status or ack it is parked on, so the wait fails at once naming the exit instead of
///     burning its full timeout. The symptom that prompted this: a crashed CS2 followed 39 seconds
///     later by a generic "Operation 'Cs2EngineSession.PlayTickRange.WaitForEndTick' timed out".
/// </summary>
public class Cs2EngineSessionProcessExitTests
{
    /// <summary>
    ///     Upper bound for operations that must fail (or complete) promptly. The waits under test are
    ///     all given multi-minute timeouts, so a regression to the timeout path surfaces here as a
    ///     fast failure rather than a hung test run.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Deliberately far longer than <see cref="TestTimeout" />: every assertion below is that the
    ///     wait ended on the process exit, not on the clock.
    /// </summary>
    private static readonly TimeSpan NeverReachedTimeout = TimeSpan.FromMinutes(5);

    private static DemoPlaybackStatusChange StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus status) =>
        new() { NewStatus = status };

    /// <summary>
    ///     Simulates the CS2 process dying, exactly as the process manager reports it (the same event
    ///     the CLI's "[CS2-ERR]" crash banner is forwarded alongside).
    /// </summary>
    private static void RaiseProcessExit(Mock<ICs2ProcessManager> processManager, int exitCode,
        ProcessExitReason reason = ProcessExitReason.Error) =>
        processManager.Raise(m => m.Exited += null, new ProcessExitedEventArgs(new ProcessExitResult(exitCode, reason)));

    /// <summary>
    ///     Brings a bare engine session to the state <see cref="ICs2EngineSession.PlayTickRangeAsync" />
    ///     requires: a wired command sender and a loaded demo.
    /// </summary>
    private static async Task<(Cs2EngineSession Session, Mock<ICs2ProcessManager> ProcessManager)>
        LoadedSessionAsync()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession(out Mock<ICs2ProcessManager> processManager);
        await session.ConnectSenderAsync("session-1");
        await session.OnDemoPlaybackStatusChangedAsync("session-1",
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded));
        return (session, processManager);
    }

    /// <summary>
    ///     The reported bug: CS2 crashed mid playback and the end-tick wait ran out its whole
    ///     (tick-range derived) timeout before failing with a message that said nothing about the dead
    ///     process. It must now fail immediately with the exit code and the operation it abandoned.
    /// </summary>
    [Fact]
    public async Task PlayTickRange_ProcessExitsDuringEndTickWait_FailsFastWithProcessExitMessage()
    {
        (Cs2EngineSession session, Mock<ICs2ProcessManager> processManager) = await LoadedSessionAsync();

        Task playback = session.PlayTickRangeAsync(0, 100_000, NeverReachedTimeout,
            TestContext.Current.CancellationToken);
        RaiseProcessExit(processManager, 3);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => playback.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("CS2 process exited (exit code 3", ex.Message);
        Assert.Contains("Cs2EngineSession.PlayTickRange.WaitForEndTick", ex.Message);
    }

    /// <summary>
    ///     The pause wait that follows the end tick is a second parked wait on the same crash-prone
    ///     stream, and has the same two-minute-class budget - it must fast-fail too.
    /// </summary>
    [Fact]
    public async Task PlayTickRange_ProcessExitsDuringPauseWait_FailsFastWithProcessExitMessage()
    {
        (Cs2EngineSession session, Mock<ICs2ProcessManager> processManager) = await LoadedSessionAsync();

        Task playback = session.PlayTickRangeAsync(0, 100, NeverReachedTimeout,
            TestContext.Current.CancellationToken);

        // Reaching the end tick completes the first wait and parks the second one on the auto-pause
        // status change that the crashed plugin will now never send.
        session.OnTickUpdated("session-1", 100);
        RaiseProcessExit(processManager, 3);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => playback.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("CS2 process exited (exit code 3", ex.Message);
    }

    /// <summary>
    ///     Demo loading parks on the same event scaffolding with a two-minute budget, so a CS2 that
    ///     dies while the demo is being read must not cost two minutes either.
    /// </summary>
    [Fact]
    public async Task LoadDemo_ProcessExitsDuringReadinessWait_FailsFastWithProcessExitMessage()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession(out Mock<ICs2ProcessManager> processManager);
        await session.ConnectSenderAsync("session-1");

        Task load = session.LoadDemoAsync("match.dem", TestContext.Current.CancellationToken);
        RaiseProcessExit(processManager, 42, ProcessExitReason.Completed);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => load.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("CS2 process exited (exit code 42", ex.Message);
        Assert.Contains("Cs2EngineSession.LoadDemo.WaitForStatus", ex.Message);
    }

    /// <summary>
    ///     The acknowledged seek waits on the plugin's <c>CommandAckEvent</c> with the longest budget
    ///     in the class (35 s, sized for the plugin's own arrival deadline). Dead plugin, no ack ever.
    /// </summary>
    [Fact]
    public async Task AckedSeek_ProcessExitsBeforeAck_FailsFastWithProcessExitMessage()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession(out Mock<ICs2ProcessManager> processManager);
        session.SeekAckTimeout = NeverReachedTimeout;
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        Task<SeekResult> seek = session.SetDemoTickAsync(500, pauseAfterSeek: true, waitForCompletion: true,
            TestContext.Current.CancellationToken);
        RaiseProcessExit(processManager, 3);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seek.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("CS2 process exited (exit code 3", ex.Message);
        Assert.Contains("acknowledge SetDemoTick (command id 1, target tick 500)", ex.Message);
    }

    /// <summary>
    ///     A demo-state query is answered by the engine itself, so a dead engine is terminal for it.
    /// </summary>
    [Fact]
    public async Task QueryDemoState_ProcessExitsBeforeReply_FailsFastWithProcessExitMessage()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession(out Mock<ICs2ProcessManager> processManager);
        session.DemoStateQueryTimeout = NeverReachedTimeout;
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        Task<DemoState> query = session.QueryDemoStateAsync(TestContext.Current.CancellationToken);
        RaiseProcessExit(processManager, 3);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => query.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("CS2 process exited (exit code 3", ex.Message);
        Assert.Contains("QueryDemoState", ex.Message);
    }

    /// <summary>
    ///     A wait started after the process already died must report the same root cause. The send it
    ///     performs first fails with "no active session" once the stream teardown lands, which is a
    ///     symptom of the crash - the exit is what the caller needs to be told about.
    /// </summary>
    [Fact]
    public async Task PlayTickRange_StartedAfterProcessDied_ReportsProcessExitRatherThanSendFailure()
    {
        (Cs2EngineSession session, Mock<ICs2ProcessManager> processManager) = await LoadedSessionAsync();

        // The crash, followed by the gRPC stream teardown it causes (which clears the sender).
        RaiseProcessExit(processManager, 3);
        await session.OnSessionEndedAsync("session-1", 250, DateTimeOffset.UtcNow);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PlayTickRangeAsync(0, 100_000, NeverReachedTimeout, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("CS2 process exited (exit code 3", ex.Message);
        Assert.DoesNotContain("no active session", ex.Message);
    }

    /// <summary>
    ///     The guard must be invisible to a healthy run: a wait that is satisfied normally still
    ///     completes normally, and a process exit reported afterwards (the ordinary end of a session)
    ///     cannot retroactively affect it.
    /// </summary>
    [Fact]
    public async Task PlayTickRange_ReachesEndTickAndPauses_CompletesNormally()
    {
        (Cs2EngineSession session, Mock<ICs2ProcessManager> processManager) = await LoadedSessionAsync();

        Task playback = session.PlayTickRangeAsync(0, 100, NeverReachedTimeout,
            TestContext.Current.CancellationToken);

        session.OnTickUpdated("session-1", 100);
        await session.OnDemoPlaybackStatusChangedAsync("session-1",
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused));

        await playback.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        // CS2 exiting after the wait was satisfied (a normal session teardown) changes nothing.
        RaiseProcessExit(processManager, 0, ProcessExitReason.Completed);
        Assert.True(playback.IsCompletedSuccessfully);
    }

    /// <summary>Likewise for a seek that is acknowledged normally.</summary>
    [Fact]
    public async Task AckedSeek_AckArrives_CompletesNormally()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession(out _);
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        Task<SeekResult> seek = session.SetDemoTickAsync(500, pauseAfterSeek: true, waitForCompletion: true,
            TestContext.Current.CancellationToken);
        session.OnCommandAck("session-1",
            new CommandAckEvent { CommandId = sent[0].CommandId, Success = true, ObservedDemoTick = 498 });

        SeekResult result = await seek.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(498, result.ObservedDemoTick);
    }

    /// <summary>
    ///     End to end through <see cref="CsvgVideoSession" />: the result-returning playback API keeps
    ///     its contract (a failed <see cref="DemoPlaybackResult" />, not an exception) and carries the
    ///     process-exit message, which is what the CLI prints as "Playback failed: ...". The session
    ///     state still follows the stream teardown the crash causes, unchanged by the fast-fail.
    /// </summary>
    [Fact]
    public async Task VideoSession_ProcessExitsDuringPlayback_FailedResultCarriesMessageAndSessionFaults()
    {
        await using CsvgVideoSession client =
            SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        await client.StartConnectedAsync(processManager, "session-1",
                cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await client.EngineSession().OnDemoPlaybackStatusChangedAsync("session-1",
            StatusChange(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded));

        Task<DemoPlaybackResult> playback = client.PlayTickRangeAsync(0, 100_000, NeverReachedTimeout,
            TestContext.Current.CancellationToken);
        RaiseProcessExit(processManager, 3);

        DemoPlaybackResult result = await playback.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("CS2 process exited (exit code 3", result.ErrorMessage);

        // The crash tears the plugin stream down right after; that is what faults the session state.
        await client.EngineSession().OnSessionEndedAsync("session-1", 250, DateTimeOffset.UtcNow);
        Assert.Equal(CsvgSessionState.Faulted, client.State);
    }
}
