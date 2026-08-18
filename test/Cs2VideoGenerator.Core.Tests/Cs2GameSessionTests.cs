#region

using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Grpc;
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
///     <see cref="Cs2GameSession" /> teardown races and send semantics. RequestStop and
///     SendAsync racing a natural session end must never throw
///     <see cref="ObjectDisposedException" />, and sends on a disconnected session must fail fast
///     instead of silently succeeding.
/// </summary>
public class Cs2GameSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A host whose current session receives whatever the read loop pushes in.</summary>
    private static ICs2EngineHost CreateEngineHost()
    {
        Cs2EngineHost host = new(NullLogger.Instance, Options.Create(new Cs2VideoGeneratorOptions()),
            new Mock<ICs2ProcessManager>().Object);
        host.CreateSession(new EngineSessionOptions());
        return host;
    }

    private static Cs2GameSession CreateSession(string sessionId, FakeStreamReader reader, FakeStreamWriter writer,
        ICs2EngineHost engineHost) =>
        new(sessionId, reader, writer, CancellationToken.None, NullLogger<Cs2GameSession>.Instance, engineHost);

    /// <summary>
    ///     A plugin that connects before anything created an engine session has nowhere to deliver
    ///     its events. Drop them and keep reading: throwing would kill the read loop and with it the
    ///     connection, which becomes usable the moment a session appears.
    /// </summary>
    [Fact]
    public async Task ReadLoop_WithNoEngineSessionOnTheHost_DropsEventsInsteadOfFaulting()
    {
        FakeStreamReader reader = new();
        FakeStreamWriter writer = new();

        // A host that has never created a session: Current is null.
        Cs2EngineHost emptyHost = new(NullLogger.Instance, Options.Create(new Cs2VideoGeneratorOptions()),
            new Mock<ICs2ProcessManager>().Object);
        Assert.Null(emptyHost.Current);

        Cs2GameSession session = CreateSession("session-1", reader, writer, emptyHost);
        Task runTask = session.RunAsync();

        reader.Write(new CsProcessEvent { TickUpdate = new TickUpdateEvent { CurrentTick = 1234 } });
        reader.Write(new CsProcessEvent { ProcessStatus = SessionHarness.ReadyStatus() });
        reader.Write(new CsProcessEvent { CommandAck = new CommandAckEvent { CommandId = 1, Success = true } });
        reader.Complete();

        // The loop drains every event and completes normally rather than propagating a null reference.
        await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Empty(writer.Written);
    }

    /// <summary>
    ///     After the session ends naturally (client stream completed) SendAsync must throw a clear
    ///     session-disconnected error immediately - not silently succeed (the old behavior, which made
    ///     callers burn their full per-command timeout) and not throw ObjectDisposedException.
    /// </summary>
    [Fact]
    public async Task SendAsync_AfterSessionEnded_FailsFastWithDisconnectedError()
    {
        FakeStreamReader reader = new();
        FakeStreamWriter writer = new();
        Cs2GameSession session = CreateSession("session-1", reader, writer, CreateEngineHost());

        Task runTask = session.RunAsync();
        reader.Complete();
        await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAsync(new CsControlRequest { PauseDemo = new PauseDemoCommand() })
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("disconnected", ex.Message);
        Assert.Empty(writer.Written);
    }

    /// <summary>
    ///     RequestStop after the session already ended (CTSes disposed by the read loop's teardown)
    ///     must be a safe no-op, never an ObjectDisposedException - otherwise StopSessionAsync aborts
    ///     before it can stop the CS2 process, orphaning it.
    /// </summary>
    [Fact]
    public async Task RequestStop_AfterSessionEnded_DoesNotThrow()
    {
        FakeStreamReader reader = new();
        FakeStreamWriter writer = new();
        Cs2GameSession session = CreateSession("session-1", reader, writer, CreateEngineHost());

        Task runTask = session.RunAsync();
        reader.Complete();
        await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        session.RequestStop(); // must not throw
    }

    /// <summary>
    ///     A stop request racing a natural session end must never surface an ObjectDisposedException
    ///     from the disposed cancellation token sources. Looped to give the race a real chance to bite.
    /// </summary>
    [Fact]
    public async Task RequestStop_RacingNaturalEnd_NeverThrows()
    {
        ICs2EngineHost engineHost = CreateEngineHost();

        for (int i = 0; i < 200; i++)
        {
            FakeStreamReader reader = new();
            FakeStreamWriter writer = new();
            Cs2GameSession session = CreateSession($"session-{i}", reader, writer, engineHost);

            Task runTask = session.RunAsync();

            using ManualResetEventSlim go = new(false);
            Task stopTask = Task.Run(() =>
            {
                go.Wait();
                session.RequestStop();
            }, TestContext.Current.CancellationToken);
            Task endTask = Task.Run(() =>
            {
                go.Wait();
                reader.Complete();
            }, TestContext.Current.CancellationToken);

            go.Set();
            await Task.WhenAll(stopTask, endTask).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     A failed write must surface to the caller (with the cause preserved) and mark the session
    ///     disconnected so subsequent sends fail fast too.
    /// </summary>
    [Fact]
    public async Task SendAsync_WriteFailure_ThrowsAndMarksSessionDisconnected()
    {
        FakeStreamReader reader = new();
        FakeStreamWriter writer = new() { ThrowOnWrite = new IOException("stream broken") };
        Cs2GameSession session = CreateSession("session-1", reader, writer, CreateEngineHost());
        Task runTask = session.RunAsync();

        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SendAsync(new CsControlRequest { PauseDemo = new PauseDemoCommand() })
                    .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.IsType<IOException>(ex.InnerException);

            // The session is now considered disconnected: the next send fails fast without writing.
            writer.ThrowOnWrite = null;
            InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SendAsync(new CsControlRequest { ResumeDemo = new ResumeDemoCommand() })
                    .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Contains("disconnected", second.Message);
            Assert.Empty(writer.Written);
        }
        finally
        {
            reader.Complete();
            await runTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     The same race one level up. A plugin disconnect racing StopAsync must never throw and must
    ///     still stop CS2 - the disposed-CTS race used to make the captured stop delegate throw
    ///     ObjectDisposedException and skip process shutdown, orphaning the game.
    /// </summary>
    [Fact]
    public async Task StopSession_RacingNaturalSessionEnd_NeverThrows_AndAlwaysStopsProcess()
    {
        for (int i = 0; i < 50; i++)
        {
            await using CsvgVideoSession videoSession =
                SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);

            FakeStreamReader reader = new();
            FakeStreamWriter writer = new();
            Task? runTask = null;

            string sessionId = $"session-{i}";
            processManager
                .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int? _, int? _, bool _, CancellationToken _) =>
                {
                    // Start a real session read loop; it registers the command sender + stop delegate
                    // and dispatches the Ready status pushed through the stream below.
                    Cs2GameSession session = CreateSession(sessionId, reader, writer, videoSession.EngineHost);
                    runTask = session.RunAsync();
                    reader.Write(new CsProcessEvent
                    {
                        ProcessStatus = new Cs2ProcessStatus
                        {
                            Status = Cs2ProcessStatus.Types.ProcessStatus.Ready
                        }
                    });
                    return Task.CompletedTask;
                });

            await videoSession.StartAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(CsvgSessionState.Connected, videoSession.State);

            // Race the natural end (stream completes) against the graceful stop.
            Task stopTask = videoSession.StopAsync(TestContext.Current.CancellationToken);
            reader.Complete();

            await stopTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken); // must not throw
            await runTask!.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(CsvgSessionState.Disconnected, videoSession.State);
            processManager.Verify(m => m.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }

}
