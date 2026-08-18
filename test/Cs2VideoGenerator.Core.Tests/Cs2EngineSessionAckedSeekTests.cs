#region

using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The acknowledged seek path (command-id + pending-ack table): id allocation, ack
///     correlation, timeout and cancellation, unknown-ack drop, pending waiters failing on session
///     end, and the legacy fire-and-forget overload staying byte-for-byte compatible.
/// </summary>
public class Cs2EngineSessionAckedSeekTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AckedSeek_SendsCommandIdAndPauseAfterSeek_CompletesFromAck()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        Task<SeekResult> seekTask = session.SetDemoTickAsync(500, pauseAfterSeek: true, waitForCompletion: true,
            TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.SetDemoTick, command.CommandCase);
        Assert.Equal(500, command.SetDemoTick.DemoTick);
        Assert.True(command.SetDemoTick.HasPauseAfterSeek);
        Assert.True(command.SetDemoTick.PauseAfterSeek);
        Assert.True(command.HasCommandId);
        Assert.Equal(1UL, command.CommandId); // Monotonic id source starts at 1.

        session.OnCommandAck("session-1",
            new CommandAckEvent { CommandId = command.CommandId, Success = true, ObservedDemoTick = 498 });

        SeekResult result = await seekTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(498, result.ObservedDemoTick);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AckedSeek_FailureAck_MapsErrorAndNullObservedTick()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        Task<SeekResult> seekTask = session.SetDemoTickAsync(500, null, waitForCompletion: true,
            TestContext.Current.CancellationToken);

        session.OnCommandAck("session-1",
            new CommandAckEvent { CommandId = sent[0].CommandId, Success = false, ErrorMessage = "seek timed out" });

        SeekResult result = await seekTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Null(result.ObservedDemoTick); // observed_demo_tick absent -> null, not 0.
        Assert.Equal("seek timed out", result.Error);
    }

    [Fact]
    public async Task AckedSeek_CommandIdsAreMonotonic()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        Task<SeekResult> first = session.SetDemoTickAsync(100, null, waitForCompletion: true,
            TestContext.Current.CancellationToken);
        Task<SeekResult> second = session.SetDemoTickAsync(200, null, waitForCompletion: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, sent.Count);
        Assert.Equal(1UL, sent[0].CommandId);
        Assert.Equal(2UL, sent[1].CommandId);

        // Acks correlate by id, not arrival order: ack the second command first.
        session.OnCommandAck("session-1", new CommandAckEvent { CommandId = 2, Success = true, ObservedDemoTick = 200 });
        session.OnCommandAck("session-1", new CommandAckEvent { CommandId = 1, Success = true, ObservedDemoTick = 100 });

        Assert.Equal(100, (await first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).ObservedDemoTick);
        Assert.Equal(200,
            (await second.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).ObservedDemoTick);
    }

    /// <summary>
    ///     An ack that never arrives becomes a <see cref="TimeoutException" />. The 15-second default
    ///     is shortened through the internal test seam.
    /// </summary>
    [Fact]
    public async Task AckedSeek_AckNeverArrives_ThrowsTimeoutException()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        session.SeekAckTimeout = TimeSpan.FromMilliseconds(150);
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => session.SetDemoTickAsync(500, null, waitForCompletion: true, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("acknowledge", ex.Message);
    }

    [Fact]
    public async Task AckedSeek_CallerCancellation_ThrowsOperationCanceled()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        using CancellationTokenSource cts = new();
        Task<SeekResult> seekTask = session.SetDemoTickAsync(500, null, waitForCompletion: true, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => seekTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     Session end must fail pending ack waiters immediately (clear/cancel-on-session-end): the
    ///     plugin can never answer once the stream is gone, so the seek must not burn its timeout.
    /// </summary>
    [Fact]
    public async Task AckedSeek_SessionEnd_FailsPendingWaiterImmediately()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        Task<SeekResult> seekTask = session.SetDemoTickAsync(500, null, waitForCompletion: true,
            TestContext.Current.CancellationToken);

        await session.OnSessionEndedAsync("session-1", 42, DateTimeOffset.UtcNow);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seekTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("ended", ex.Message);
    }

    /// <summary>
    ///     An ack for an unknown command id (waiter already gone, stale session) is dropped without
    ///     throwing - the read loop must never be destabilized by a late ack.
    /// </summary>
    [Fact]
    public async Task CommandAck_UnknownId_IsDroppedSilently()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        session.OnCommandAck("session-1", new CommandAckEvent { CommandId = 999, Success = true }); // must not throw
    }

    /// <summary>Legacy wire shape: plain SetDemoTickCommand, no command id, no pause_after_seek.</summary>
    [Fact]
    public async Task LegacyOverload_SendsPlainCommand_WithoutCommandIdOrPauseAfterSeek()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        await session.SetDemoTickAsync(42, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.SetDemoTick, command.CommandCase);
        Assert.Equal(42, command.SetDemoTick.DemoTick);
        Assert.False(command.HasCommandId);
        Assert.False(command.SetDemoTick.HasPauseAfterSeek);
    }

    /// <summary>
    ///     A non-waiting seek carries pause_after_seek when requested (harmless on old plugins,
    ///     honored by "seek-ack" plugins) but still no command id - no ack is being requested.
    /// </summary>
    [Fact]
    public async Task FireAndForget_WithPauseAfterSeek_IncludesFieldButNoCommandId()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.CommandAck);

        SeekResult result = await session
            .SetDemoTickAsync(300, pauseAfterSeek: false, waitForCompletion: false,
                TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        CsControlRequest command = Assert.Single(sent);
        Assert.True(command.SetDemoTick.HasPauseAfterSeek);
        Assert.False(command.SetDemoTick.PauseAfterSeek);
        Assert.False(command.HasCommandId);
    }
}
