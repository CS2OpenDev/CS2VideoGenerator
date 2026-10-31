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
///     The capability handshake. <see cref="Cs2EngineSession.PluginCapabilities" />
///     latches from process-status events (last write wins) and resets on session end; the
///     capability-gated APIs degrade to <see cref="NotSupportedException" /> against a
///     capability-less plugin.
/// </summary>
public class Cs2EngineSessionCapabilityTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void PluginCapabilities_BeforeAnyHandshake_IsEmpty()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();

        Assert.Empty(session.PluginCapabilities);
        Assert.False(session.Supports(CsvgCapabilities.CommandAck));
    }

    [Fact]
    public async Task ProcessStatus_WithCapabilities_LatchesSet()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");

        await session.OnProcessStatusChangedAsync("session-1",
            SessionHarness.ReadyStatus(CsvgCapabilities.CommandAck, CsvgCapabilities.TimescaleSet));

        Assert.Equal(2, session.PluginCapabilities.Count);
        Assert.True(session.Supports(CsvgCapabilities.CommandAck));
        Assert.True(session.Supports(CsvgCapabilities.TimescaleSet));
        Assert.False(session.Supports(CsvgCapabilities.DemoStateEvents));
    }

    /// <summary>
    ///     Every process-status event re-latches the set: last write wins, including a later status
    ///     that advertises nothing (which returns the session to the baseline).
    /// </summary>
    [Fact]
    public async Task ProcessStatus_Relatch_LastWriteWins()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");

        await session.OnProcessStatusChangedAsync("session-1", SessionHarness.ReadyStatus(CsvgCapabilities.CommandAck));
        await session.OnProcessStatusChangedAsync("session-1", SessionHarness.ReadyStatus(CsvgCapabilities.DemoStateEvents));

        Assert.False(session.Supports(CsvgCapabilities.CommandAck));
        Assert.True(session.Supports(CsvgCapabilities.DemoStateEvents));

        await session.OnProcessStatusChangedAsync("session-1", SessionHarness.ReadyStatus());
        Assert.Empty(session.PluginCapabilities);
    }

    [Fact]
    public async Task SessionEnd_ResetsCapabilitiesToEmpty()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");
        await session.OnProcessStatusChangedAsync("session-1", SessionHarness.ReadyStatus(CsvgCapabilities.CommandAck));
        Assert.True(session.Supports(CsvgCapabilities.CommandAck));

        await session.OnSessionEndedAsync("session-1", 1, DateTimeOffset.UtcNow);

        Assert.Empty(session.PluginCapabilities);
        Assert.False(session.Supports(CsvgCapabilities.CommandAck));
    }

    /// <summary>
    ///     A new session must not inherit the previous session's capability latch (the reconnected
    ///     plugin may be an older build); the set resets until the new handshake arrives.
    /// </summary>
    [Fact]
    public async Task SessionStart_ResetsCapabilities()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");
        await session.OnProcessStatusChangedAsync("session-1", SessionHarness.ReadyStatus(CsvgCapabilities.CommandAck));

        await session.OnSessionStartedAsync("session-2");

        Assert.Empty(session.PluginCapabilities);
    }

    [Fact]
    public void Supports_NullCapability_Throws()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();

        Assert.Throws<ArgumentNullException>(() => session.Supports(null!));
    }

    /// <summary>
    ///     An acknowledged seek against a plugin without "command-ack" throws
    ///     <see cref="NotSupportedException" /> naming the missing capability, rather than silently
    ///     degrading to fire-and-forget.
    /// </summary>
    [Fact]
    public async Task SetDemoTickAsync_WaitForCompletion_WithoutCommandAck_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetDemoTickAsync(100, null, waitForCompletion: true, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.CommandAck, ex.Message);
    }

    [Fact]
    public async Task SetDemoTimescaleAsync_WithoutTimescaleSet_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetDemoTimescaleAsync(2.0f, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.TimescaleSet, ex.Message);
    }

    [Fact]
    public async Task QueryDemoStateAsync_WithoutDemoStateEvents_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectSenderAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.QueryDemoStateAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.DemoStateEvents, ex.Message);
    }

    /// <summary>
    ///     The non-waiting overload keeps working against a capability-less plugin. That is the
    ///     documented legacy degradation: fire-and-forget, no command id.
    /// </summary>
    [Fact]
    public async Task SetDemoTickAsync_NotWaiting_WithoutCapabilities_SendsLegacyCommand()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectSenderAsync("session-1");

        SeekResult result = await session
            .SetDemoTickAsync(250, null, waitForCompletion: false, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.ObservedDemoTick);
        Assert.Null(result.Error);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.SetDemoTick, command.CommandCase);
        Assert.Equal(250, command.SetDemoTick.DemoTick);
        Assert.False(command.HasCommandId);
        Assert.False(command.SetDemoTick.HasPauseAfterSeek);
    }
}
