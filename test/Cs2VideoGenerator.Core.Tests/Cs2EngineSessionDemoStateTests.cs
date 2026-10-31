#region

using Cs2VideoGenerator.Core.Grpc;
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
///     The demo state surface: proto-to-record mapping (optional-field presence drives
///     nullability, plus the origin enum), <see cref="Cs2EngineSession.DemoStateChanged" /> dispatch,
///     <see cref="Cs2EngineSession.LastDemoState" /> and
///     <see cref="Cs2EngineSession.LastKnownTimescale" /> tracking,
///     <see cref="Cs2EngineSession.QueryDemoStateAsync" /> reply correlation, and
///     <see cref="Cs2GameSession" /> read-loop routing of the new event cases - including
///     <c>TickUpdateEvent.is_paused</c> into <see cref="Cs2EngineSession.LastTickIsPaused" />.
/// </summary>
public class Cs2EngineSessionDemoStateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Polls until <paramref name="condition" /> holds. Needed where events travel through a real
    ///     read loop on another task.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource cts =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TestTimeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public void FromProto_AllFieldsPresent_MapsEveryField()
    {
        DemoStateEvent stateEvent = new()
        {
            IsPlayingDemo = true,
            IsPaused = false,
            DemoTick = 12345,
            DemoFilePath = "/demos/match.dem",
            DemoTimescale = 2.5f,
            SpectatedPlayerName = "player-one",
            Origin = DemoStateEvent.Types.ChangeOrigin.OriginUser
        };

        DemoState state = DemoState.FromProto(stateEvent);

        Assert.True(state.IsPlayingDemo);
        Assert.False(state.IsPaused);
        Assert.Equal(12345, state.DemoTick);
        Assert.Equal("/demos/match.dem", state.DemoFilePath);
        Assert.Equal(2.5f, state.Timescale);
        Assert.Equal("player-one", state.SpectatedPlayerName);
        Assert.Equal(DemoStateOrigin.User, state.Origin);
    }

    /// <summary>
    ///     Absent proto3 optionals map to null, not to false/0/empty. A bare event therefore produces
    ///     an all-null record with origin Unknown.
    /// </summary>
    [Fact]
    public void FromProto_AbsentOptionals_MapToNull()
    {
        DemoState state = DemoState.FromProto(new DemoStateEvent());

        Assert.Null(state.IsPlayingDemo);
        Assert.Null(state.IsPaused);
        Assert.Null(state.DemoTick);
        Assert.Null(state.DemoFilePath);
        Assert.Null(state.Timescale);
        Assert.Null(state.SpectatedPlayerName);
        Assert.Equal(DemoStateOrigin.Unknown, state.Origin);
    }

    /// <summary>Presence drives nullability, not truthiness - explicit false/0 must survive.</summary>
    [Fact]
    public void FromProto_ExplicitFalseAndZero_ArePreserved()
    {
        DemoStateEvent stateEvent = new() { IsPlayingDemo = false, DemoTick = 0, DemoFilePath = "" };

        DemoState state = DemoState.FromProto(stateEvent);

        Assert.False(state.IsPlayingDemo);
        Assert.Equal(0, state.DemoTick);
        Assert.Equal("", state.DemoFilePath); // empty string = "no demo", distinct from null = "not reported"
        Assert.Null(state.IsPaused);
    }

    [Theory]
    [InlineData(DemoStateEvent.Types.ChangeOrigin.OriginUnknown, DemoStateOrigin.Unknown)]
    [InlineData(DemoStateEvent.Types.ChangeOrigin.OriginHostCommand, DemoStateOrigin.HostCommand)]
    [InlineData(DemoStateEvent.Types.ChangeOrigin.OriginUser, DemoStateOrigin.User)]
    [InlineData((DemoStateEvent.Types.ChangeOrigin)99, DemoStateOrigin.Unknown)] // unrecognized wire value
    public void FromProto_OriginMapping(DemoStateEvent.Types.ChangeOrigin wireOrigin, DemoStateOrigin expected)
    {
        DemoState state = DemoState.FromProto(new DemoStateEvent { Origin = wireOrigin });

        Assert.Equal(expected, state.Origin);
    }

    [Fact]
    public async Task OnDemoState_UpdatesLastDemoState_AndRaisesEvent()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        DemoState? received = null;
        string? receivedSessionId = null;
        session.DemoStateChanged += (sessionId, state) =>
        {
            receivedSessionId = sessionId;
            received = state;
            return Task.CompletedTask;
        };

        await session.OnDemoStateAsync("session-1",
            new DemoStateEvent { IsPaused = true, DemoTick = 777 });

        Assert.Equal("session-1", receivedSessionId);
        Assert.NotNull(received);
        Assert.True(received.IsPaused);
        Assert.Equal(777, received.DemoTick);
        Assert.Same(received, session.LastDemoState);
    }

    /// <summary>Same per-subscriber exception isolation as the other async events.</summary>
    [Fact]
    public async Task DemoStateChanged_ThrowingSubscriber_DoesNotSkipLaterSubscribers()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        bool laterSubscriberRan = false;
        session.DemoStateChanged += (_, _) => throw new InvalidOperationException("subscriber boom");
        session.DemoStateChanged += (_, _) =>
        {
            laterSubscriberRan = true;
            return Task.CompletedTask;
        };

        await session.OnDemoStateAsync("session-1", new DemoStateEvent { DemoTick = 1 });

        Assert.True(laterSubscriberRan);
    }

    [Fact]
    public async Task OnDemoState_WithTimescale_UpdatesLastKnownTimescale()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);
        Assert.Null(session.LastKnownTimescale);

        await session.OnDemoStateAsync("session-1", new DemoStateEvent { DemoTimescale = 0.5f });
        Assert.Equal(0.5f, session.LastKnownTimescale);

        // an event without a timescale must not clear the tracked value
        await session.OnDemoStateAsync("session-1", new DemoStateEvent { DemoTick = 10 });
        Assert.Equal(0.5f, session.LastKnownTimescale);
    }

    [Fact]
    public async Task QueryDemoState_CompletesFromMatchingReply()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        Task<DemoState> queryTask = session.QueryDemoStateAsync(TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.QueryDemoState, command.CommandCase);
        Assert.True(command.HasCommandId);

        await session.OnDemoStateAsync("session-1",
            new DemoStateEvent { IsPlayingDemo = true, DemoTick = 555, InReplyToCommandId = command.CommandId });

        DemoState state = await queryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(state.IsPlayingDemo);
        Assert.Equal(555, state.DemoTick);
    }

    /// <summary>
    ///     The query completes only from the DemoStateEvent whose in_reply_to_command_id matches -
    ///     neither an unrelated state event nor a <see cref="CommandAckEvent" /> for the same id may
    ///     complete it.
    /// </summary>
    [Fact]
    public async Task QueryDemoState_IgnoresUnrelatedEventsAndCommandAcks()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        Task<DemoState> queryTask = session.QueryDemoStateAsync(TestContext.Current.CancellationToken);
        ulong commandId = sent[0].CommandId;

        // A change-driven state event with no reply id must not complete the query...
        await session.OnDemoStateAsync("session-1", new DemoStateEvent { DemoTick = 1 });
        // ...nor a state event replying to some other command...
        await session.OnDemoStateAsync("session-1", new DemoStateEvent { DemoTick = 2, InReplyToCommandId = 999 });
        // ...nor a CommandAckEvent echoing the query's own id - the reply is the state event.
        session.OnCommandAck("session-1", new CommandAckEvent { CommandId = commandId, Success = true });
        Assert.False(queryTask.IsCompleted);

        await session.OnDemoStateAsync("session-1",
            new DemoStateEvent { DemoTick = 3, InReplyToCommandId = commandId });

        DemoState state = await queryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(3, state.DemoTick);
    }

    [Fact]
    public async Task QueryDemoState_ReplyNeverArrives_ThrowsTimeoutException()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        session.DemoStateQueryTimeout = TimeSpan.FromMilliseconds(150);
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => session.QueryDemoStateAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("QueryDemoState", ex.Message);
    }

    [Fact]
    public async Task QueryDemoState_SessionEnd_FailsPendingQueryImmediately()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        Task<DemoState> queryTask = session.QueryDemoStateAsync(TestContext.Current.CancellationToken);
        await session.OnSessionEndedAsync("session-1", 1, DateTimeOffset.UtcNow);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("ended", ex.Message);
    }

    /// <summary>
    ///     Routing through a real <see cref="Cs2GameSession" /> over the fake gRPC streams: is_paused
    ///     reaches <see cref="Cs2EngineSession.LastTickIsPaused" /> (absent field leaves it alone),
    ///     demo state lands in <see cref="Cs2EngineSession.LastDemoState" />, and a stray CommandAck
    ///     does not disturb the loop.
    /// </summary>
    [Fact]
    public async Task Session_RoutesTickIsPausedDemoStateAndCommandAck()
    {
        Cs2EngineHost host = SessionHarness.CreateEngineHost(out Cs2EngineSession session);
        FakeStreamReader reader = new();
        FakeStreamWriter writer = new();
        Cs2GameSession wireSession = new("session-1", reader, writer, CancellationToken.None,
            NullLogger<Cs2GameSession>.Instance, host);
        Task runTask = wireSession.RunAsync();

        try
        {
            // Older-plugin tick update (no is_paused): LastTickIsPaused must stay null.
            reader.Write(new CsProcessEvent { TickUpdate = new TickUpdateEvent { CurrentTick = 100 } });
            await WaitUntilAsync(() => session.LastTick == 100);
            Assert.Null(session.LastTickIsPaused);

            // Tick update carrying is_paused.
            reader.Write(new CsProcessEvent { TickUpdate = new TickUpdateEvent { CurrentTick = 101, IsPaused = true } });
            await WaitUntilAsync(() => session.LastTick == 101);
            Assert.True(session.LastTickIsPaused);

            // A later tick update without the field leaves the last known value in place (best-effort).
            reader.Write(new CsProcessEvent { TickUpdate = new TickUpdateEvent { CurrentTick = 102 } });
            await WaitUntilAsync(() => session.LastTick == 102);
            Assert.True(session.LastTickIsPaused);

            // A stray CommandAck (no pending waiter) must be dropped without breaking the loop.
            reader.Write(new CsProcessEvent { CommandAck = new CommandAckEvent { CommandId = 7, Success = true } });

            // DemoStateEvent routes into LastDemoState via the awaited async path.
            reader.Write(new CsProcessEvent
            {
                DemoState = new DemoStateEvent { IsPaused = false, DemoTick = 4242 }
            });
            await WaitUntilAsync(() => session.LastDemoState != null);
            Assert.Equal(4242, session.LastDemoState!.DemoTick);
            Assert.False(session.LastDemoState.IsPaused);
        }
        finally
        {
            reader.Complete();
            await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>State from a previous connection must not leak into the next one.</summary>
    [Fact]
    public async Task SessionStart_ResetsTickPauseDemoStateAndTimescaleLatches()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.DemoStateEvents);

        session.OnTickUpdated("session-1", 50, isPaused: true);
        await session.OnDemoStateAsync("session-1", new DemoStateEvent { DemoTick = 1, DemoTimescale = 4f });
        Assert.True(session.LastTickIsPaused);
        Assert.NotNull(session.LastDemoState);
        Assert.Equal(4f, session.LastKnownTimescale);

        await session.OnSessionStartedAsync("session-2");

        Assert.Null(session.LastTickIsPaused);
        Assert.Null(session.LastDemoState);
        Assert.Null(session.LastKnownTimescale);
    }
}
