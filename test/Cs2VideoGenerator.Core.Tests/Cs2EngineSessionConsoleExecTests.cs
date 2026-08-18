#region

using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     <see cref="Cs2EngineSession.ExecuteConsoleCommandsAsync" /> ("console-exec"). The ack only
///     confirms queuing for the engine thread; the engine reports nothing back about a console
///     command, so these assert the send contract and never an engine effect.
/// </summary>
public class Cs2EngineSessionConsoleExecTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ExecuteConsoleCommands_SendsCommandsInOrder()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.ConsoleExec);

        await session.ExecuteConsoleCommandsAsync(["endmovie", "host_framerate 0"],
            TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.ExecuteConsoleCommands, command.CommandCase);
        Assert.Equal(["endmovie", "host_framerate 0"], command.ExecuteConsoleCommands.ConsoleCommands);
    }

    /// <summary>
    ///     Console execution is a state-changer with no legacy equivalent: a plugin without the
    ///     capability must produce <see cref="NotSupportedException" /> naming the token, never a
    ///     silent no-op that leaves a caller believing the engine was restored.
    /// </summary>
    [Fact]
    public async Task ExecuteConsoleCommands_WithoutCapability_ThrowsNotSupported()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.ExecuteConsoleCommandsAsync(["endmovie"], TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains(CsvgCapabilities.ConsoleExec, ex.Message);
        Assert.Empty(sent);
    }

    /// <summary>
    ///     Argument validation runs before the capability check so a caller sees the real defect
    ///     (bad arguments) rather than a capability message that would disappear on a newer plugin.
    /// </summary>
    [Fact]
    public async Task ExecuteConsoleCommands_InvalidArguments_ThrowBeforeCapabilityCheck()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.ExecuteConsoleCommandsAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.ExecuteConsoleCommandsAsync([], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.ExecuteConsoleCommandsAsync(["endmovie", "  "], TestContext.Current.CancellationToken));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task ExecuteConsoleCommands_WithCommandAck_WaitsForAcknowledgement()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1",
            CsvgCapabilities.ConsoleExec, CsvgCapabilities.CommandAck);

        Task executeTask = session.ExecuteConsoleCommandsAsync(["endmovie"],
            TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.True(command.HasCommandId);
        Assert.False(executeTask.IsCompleted);

        session.OnCommandAck("session-1", new CommandAckEvent { CommandId = command.CommandId, Success = true });
        await executeTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A refused batch must surface: an abort path that believes it issued <c>endmovie</c> when the
    ///     plugin rejected the request would leave CS2 recording and report nothing.
    /// </summary>
    [Fact]
    public async Task ExecuteConsoleCommands_FailureAck_Throws()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent = await session.ConnectWithCapabilitiesAsync("session-1",
            CsvgCapabilities.ConsoleExec, CsvgCapabilities.CommandAck);

        Task executeTask = session.ExecuteConsoleCommandsAsync(["endmovie"],
            TestContext.Current.CancellationToken);
        session.OnCommandAck("session-1", new CommandAckEvent
        {
            CommandId = sent[0].CommandId, Success = false, ErrorMessage = "console_commands must not be empty"
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executeTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("console_commands must not be empty", ex.Message);
    }

    /// <summary>
    ///     Without "command-ack" the command must go out bare - an id the plugin will never echo
    ///     would hang the caller for the full ack timeout.
    /// </summary>
    [Fact]
    public async Task ExecuteConsoleCommands_WithoutCommandAck_SendsWithoutCommandId()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.ConsoleExec);

        await session.ExecuteConsoleCommandsAsync(["endmovie"], TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(sent).HasCommandId);
    }
}
