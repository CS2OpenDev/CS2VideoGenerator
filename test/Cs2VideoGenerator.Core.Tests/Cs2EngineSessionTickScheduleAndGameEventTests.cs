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
///     The phase-1 command surface: tick-scheduled console commands
///     (<see cref="Cs2EngineSession.ScheduleCommandsAtTickAsync" /> /
///     <see cref="Cs2EngineSession.ClearScheduledCommandsAsync" />) and opt-in game-event telemetry
///     (<see cref="Cs2EngineSession.SetGameEventSubscriptionAsync" /> /
///     <see cref="Cs2EngineSession.GameEventReceived" />). Wire shape, capability gating, argument
///     validation, and read-loop routing of <c>GameEventTelemetryEvent</c>.
/// </summary>
public class Cs2EngineSessionTickScheduleAndGameEventTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

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

    #region Tick-scheduled commands

    [Fact]
    public async Task ScheduleCommandsAtTick_SendsTickAndCommandsInOrder()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.TickScheduledCommands);

        await session.ScheduleCommandsAtTickAsync(4096, ["spec_mode 1", "spec_player \"a b\""],
            TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.ScheduleCommandsAtTick, command.CommandCase);
        Assert.Equal(4096, command.ScheduleCommandsAtTick.DemoTick);
        Assert.Equal(["spec_mode 1", "spec_player \"a b\""], command.ScheduleCommandsAtTick.ConsoleCommands);
    }

    [Fact]
    public async Task ClearScheduledCommands_SendsCommand()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.TickScheduledCommands);

        await session.ClearScheduledCommandsAsync(TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.ClearScheduledCommands, command.CommandCase);
    }

    /// <summary>
    ///     Tick scheduling is a state-changer with no legacy equivalent, so a plugin without the
    ///     capability must produce <see cref="NotSupportedException" /> naming the token - never a
    ///     silent no-op that leaves the caller believing a clip boundary is armed.
    /// </summary>
    [Fact]
    public async Task ScheduleCommandsAtTick_WithoutCapability_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.ScheduleCommandsAtTickAsync(100, ["sv_cheats 1"], TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.TickScheduledCommands, ex.Message);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task ClearScheduledCommands_WithoutCapability_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.ClearScheduledCommandsAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.TickScheduledCommands, ex.Message);
    }

    /// <summary>
    ///     Bad arguments beat the capability check, so a caller sees the real defect rather than a
    ///     capability message that would vanish on a newer plugin.
    /// </summary>
    [Fact]
    public async Task ScheduleCommandsAtTick_InvalidArguments_ThrowBeforeCapabilityCheck()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.ScheduleCommandsAtTickAsync(1, null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.ScheduleCommandsAtTickAsync(-1, ["stop"], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.ScheduleCommandsAtTickAsync(1, [], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.ScheduleCommandsAtTickAsync(1, ["stop", "  "], TestContext.Current.CancellationToken));

        Assert.Empty(sent);
    }

    /// <summary>
    ///     With "command-ack" the schedule carries a command id and completes only once the plugin
    ///     confirms registration - what a caller needs before resuming playback past the scheduled
    ///     tick.
    /// </summary>
    [Fact]
    public async Task ScheduleCommandsAtTick_WithCommandAck_WaitsForAcknowledgement()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1",
            CsvgCapabilities.TickScheduledCommands, CsvgCapabilities.CommandAck);

        Task scheduleTask = session.ScheduleCommandsAtTickAsync(96, ["sv_cheats 1"],
            TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.True(command.HasCommandId);
        Assert.False(scheduleTask.IsCompleted);

        session.OnCommandAck("session-1", new CommandAckEvent { CommandId = command.CommandId, Success = true });
        await scheduleTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A failure ack must surface: a schedule the plugin refused (bad tick, empty batch) would
    ///     otherwise leave the caller believing a clip boundary is armed when nothing will fire.
    /// </summary>
    [Fact]
    public async Task ScheduleCommandsAtTick_FailureAck_Throws()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1",
            CsvgCapabilities.TickScheduledCommands, CsvgCapabilities.CommandAck);

        Task scheduleTask = session.ScheduleCommandsAtTickAsync(96, ["sv_cheats 1"],
            TestContext.Current.CancellationToken);
        session.OnCommandAck("session-1", new CommandAckEvent
        {
            CommandId = sent[0].CommandId, Success = false, ErrorMessage = "demo_tick must be non-negative"
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduleTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("demo_tick must be non-negative", ex.Message);
    }

    /// <summary>
    ///     Without "command-ack" the command must go out bare - an id the plugin will never echo
    ///     would hang the caller for the full ack timeout.
    /// </summary>
    [Fact]
    public async Task ScheduleCommandsAtTick_WithoutCommandAck_SendsWithoutCommandId()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.TickScheduledCommands);

        await session.ScheduleCommandsAtTickAsync(96, ["sv_cheats 1"], TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(sent).HasCommandId);
    }

    #endregion

    #region Game-event telemetry

    [Fact]
    public async Task SetGameEventSubscription_EnabledWithFilter_SendsNamesOnTheWire()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.GameEventTelemetry);

        await session.SetGameEventSubscriptionAsync(true, ["player_death", "round_start"],
            TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.SetGameEventSubscription, command.CommandCase);
        Assert.True(command.SetGameEventSubscription.Enabled);
        Assert.Equal(["player_death", "round_start"], command.SetGameEventSubscription.EventNames);
    }

    /// <summary>
    ///     An empty name list is the documented "all events" wire form, and a disable never carries
    ///     a filter (the plugin drops it either way - sending one would imply it still matters).
    /// </summary>
    [Fact]
    public async Task SetGameEventSubscription_NoFilterOrDisabled_SendsEmptyNameList()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.GameEventTelemetry);

        await session.SetGameEventSubscriptionAsync(true, cancellationToken: TestContext.Current.CancellationToken);
        await session.SetGameEventSubscriptionAsync(false, ["player_death"], TestContext.Current.CancellationToken);

        Assert.Equal(2, sent.Count);
        Assert.True(sent[0].SetGameEventSubscription.Enabled);
        Assert.Empty(sent[0].SetGameEventSubscription.EventNames);
        Assert.False(sent[1].SetGameEventSubscription.Enabled);
        Assert.Empty(sent[1].SetGameEventSubscription.EventNames);
    }

    [Fact]
    public async Task SetGameEventSubscription_WithoutCapability_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetGameEventSubscriptionAsync(true, cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.GameEventTelemetry, ex.Message);
        Assert.Empty(sent);
    }

    /// <summary>
    ///     Enabling installs the plugin's game-event hook, which can genuinely fail at runtime (the
    ///     vtable lookup depends on the CS2 build). With "command-ack" that failure must reach the
    ///     caller instead of leaving a subscription that silently never delivers.
    /// </summary>
    [Fact]
    public async Task SetGameEventSubscription_FailureAck_Throws()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1",
            CsvgCapabilities.GameEventTelemetry, CsvgCapabilities.CommandAck);

        Task subscribeTask =
            session.SetGameEventSubscriptionAsync(true, cancellationToken: TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.True(command.HasCommandId);
        Assert.False(subscribeTask.IsCompleted);

        session.OnCommandAck("session-1", new CommandAckEvent
        {
            CommandId = command.CommandId,
            Success = false,
            ErrorMessage = "Game-event telemetry hook could not be installed on this platform/build"
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => subscribeTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("hook could not be installed", ex.Message);
    }

    [Fact]
    public void FromProto_MapsGameEventTelemetry()
    {
        GameEventTelemetry telemetry = GameEventTelemetry.FromProto(new GameEventTelemetryEvent
        {
            EventName = "player_death", JsonPayload = "{\"attacker\":3}"
        });

        Assert.Equal("player_death", telemetry.EventName);
        Assert.Equal("{\"attacker\":3}", telemetry.JsonPayload);
    }

    /// <summary>
    ///     Through the real <see cref="Cs2GameSession" /> read loop. One bad handler must not stop
    ///     the others or break the loop.
    /// </summary>
    [Fact]
    public async Task Session_RoutesGameEventTelemetryWithSubscriberIsolation()
    {
        Cs2EngineHost host = SessionHarness.CreateEngineHost(out Cs2EngineSession session);
        List<GameEventTelemetry> received = [];

        session.GameEventReceived += (_, _) => throw new InvalidOperationException("subscriber blew up");
        session.GameEventReceived += (_, telemetry) =>
        {
            lock (received)
            {
                received.Add(telemetry);
            }

            return Task.CompletedTask;
        };

        FakeStreamReader reader = new();
        FakeStreamWriter writer = new();
        Cs2GameSession wireSession = new("session-1", reader, writer, CancellationToken.None,
            NullLogger<Cs2GameSession>.Instance, host);
        Task runTask = wireSession.RunAsync();

        try
        {
            reader.Write(new CsProcessEvent
            {
                GameEventTelemetry = new GameEventTelemetryEvent
                {
                    EventName = "round_start", JsonPayload = "{\"timelimit\":115}"
                }
            });

            await WaitUntilAsync(() =>
            {
                lock (received)
                {
                    return received.Count == 1;
                }
            });

            Assert.Equal("round_start", received[0].EventName);
            Assert.Equal("{\"timelimit\":115}", received[0].JsonPayload);

            // The loop survives the throwing subscriber and keeps delivering.
            reader.Write(new CsProcessEvent
            {
                GameEventTelemetry = new GameEventTelemetryEvent { EventName = "player_death", JsonPayload = "{}" }
            });
            await WaitUntilAsync(() =>
            {
                lock (received)
                {
                    return received.Count == 2;
                }
            });
            Assert.Equal("player_death", received[1].EventName);
        }
        finally
        {
            reader.Complete();
            await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    #endregion

    #region MockUserActions grammar

    /// <summary>
    ///     The injector grammar helper must produce exactly what mock_plugin.cpp's
    ///     <c>game-event &lt;name&gt; [json]</c> parser expects (name = first token, remainder = payload).
    /// </summary>
    [Fact]
    public void MockUserActions_GameEvent_ProducesInjectorGrammar()
    {
        Assert.Equal("game-event player_death {}", MockUserActions.GameEvent("player_death"));
        Assert.Equal("game-event round_end {\"winner\":2}",
            MockUserActions.GameEvent("round_end", "{\"winner\":2}"));
    }

    #endregion
}
