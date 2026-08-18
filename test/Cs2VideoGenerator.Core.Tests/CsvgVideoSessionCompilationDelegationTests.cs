#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     What <see cref="CsvgVideoSession.RunCompilationAsync" /> owns now that the pipeline lives in
///     <see cref="Cs2CompilationRunner" />: handing the runner the current engine and capture sessions,
///     deciding whether capture counts as initialized, forwarding the two clip events, and keeping
///     <see cref="CsvgVideoSession.LastCaptureResult" /> current while a compilation runs.
/// </summary>
public class CsvgVideoSessionCompilationDelegationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     The clip events reach the session's subscribers, and the clip's capture outcome is visible as
    ///     <see cref="CsvgVideoSession.LastCaptureResult" /> - the runner captures clips, but the video
    ///     session's bookkeeping still tracks them. The clip is failed on purpose (a backend that cannot
    ///     begin a clip), which also exercises the fail-fast result travelling back out.
    /// </summary>
    [Fact]
    public async Task CaptureCompilation_ForwardsTheClipEvents_AndRecordsTheLastCaptureResult()
    {
        using TempDir dir = new();
        string demoPath = CreateDemo(dir);

        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);

        await using (client)
        {
            await StartConnectedSessionAsync(client, processManager, demoLoadReplies: true);

            // A backend that cannot begin a clip fails it immediately, so the compilation's fail-fast
            // path runs without waiting on a playback that no plugin is driving.
            captureFactory.CreatedSessions[0].BeginClipFailure = new InvalidOperationException("backend down");

            List<int> started = [];
            List<(int Index, bool Success)> completed = [];

            // Read (never assert) inside the handler: a throwing subscriber is isolated and swallowed by
            // design, so an assertion in here could never fail the test.
            CaptureResult? clipCaptureResult = null;
            CaptureResult? lastCaptureResultDuringDispatch = null;
            client.CompilationClipStarted += (index, _) =>
            {
                started.Add(index);
                return Task.CompletedTask;
            };
            client.CompilationClipCompleted += (index, result) =>
            {
                completed.Add((index, result.Success));
                clipCaptureResult = result.CaptureResult;
                lastCaptureResultDuringDispatch = client.LastCaptureResult;
                return Task.CompletedTask;
            };

            Cs2CompilationResult result = await client
                .RunCompilationAsync(Compilation(dir.Path, demoPath), TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("backend down", result.ErrorMessage);
            Assert.Equal([0], started);
            Assert.Equal([(0, false)], completed);

            // The capture outcome was already recorded when subscribers ran, exactly as it is on the
            // single-clip path.
            Assert.NotNull(clipCaptureResult);
            Assert.Same(clipCaptureResult, lastCaptureResultDuringDispatch);
            Assert.Same(clipCaptureResult, client.LastCaptureResult);
            Assert.False(client.LastCaptureResult?.Success);

            // The clip ran on the current session's backend, against the loaded demo.
            Assert.Equal([demoPath], captureFactory.CreatedSessions[0].DemosLoaded);
        }
    }

    /// <summary>
    ///     A stop tears the capture backend down but keeps the session object around, so readiness is the
    ///     initialization flag: a compilation started after a stop must be refused rather than run
    ///     against a backend that has already shut down.
    /// </summary>
    [Fact]
    public async Task CaptureCompilation_AfterAStop_ReportsThatCaptureIsNotInitialized()
    {
        using TempDir dir = new();
        string demoPath = CreateDemo(dir);

        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> processManager,
            out FakeCaptureSessionFactory captureFactory);

        await using (client)
        {
            await StartConnectedSessionAsync(client, processManager, demoLoadReplies: false);
            await client.StopAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.RunCompilationAsync(Compilation(dir.Path, demoPath),
                    TestContext.Current.CancellationToken));

            Assert.Contains("Capture provider not initialized", ex.Message);
            Assert.Equal(1, captureFactory.CreatedSessions[0].ShutdownCallCount);
            Assert.Empty(captureFactory.CreatedSessions[0].Clips);
        }
    }

    /// <summary>
    ///     An invalid compilation is reported as such even when capture was never brought up: the input
    ///     the caller can fix wins over the state it cannot.
    /// </summary>
    [Fact]
    public async Task CaptureCompilation_WithAnInvalidCompilation_ThrowsBeforeComplainingAboutCapture()
    {
        using TempDir dir = new();
        CsvgVideoSession client = CreateClient(out Mock<ICs2ProcessManager> _, out FakeCaptureSessionFactory captureFactory);

        await using (client)
        {
            ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                client.RunCompilationAsync(Compilation(dir.Path, dir.Combine("missing.dem")),
                    TestContext.Current.CancellationToken));

            Assert.Contains("Demo file not found", ex.Message);
            Assert.Empty(captureFactory.CreatedSessions);
        }
    }

    #region Helpers

    private static CsvgVideoSession CreateClient(
        out Mock<ICs2ProcessManager> processManager,
        out FakeCaptureSessionFactory captureFactory)
    {
        captureFactory = new FakeCaptureSessionFactory();
        return SessionHarness.CreateVideoSession(out processManager, captureFactory: captureFactory,
            coordinator: new ClipCaptureCoordinator());
    }

    /// <summary>
    ///     Brings a session up with capture initialized. With <paramref name="demoLoadReplies" /> the
    ///     "plugin" answers a load command with the DemoLoaded status the engine session waits for.
    /// </summary>
    private static async Task StartConnectedSessionAsync(
        CsvgVideoSession client,
        Mock<ICs2ProcessManager> processManager,
        bool demoLoadReplies)
    {
        const string sessionId = "session-1";
        await client
            .StartConnectedAsync(processManager, sessionId, async request =>
            {
                if (demoLoadReplies && request.CommandCase == CsControlRequest.CommandOneofCase.LoadDemo)
                {
                    await client.EngineSession().OnDemoPlaybackStatusChangedAsync(sessionId,
                        new DemoPlaybackStatusChange
                        {
                            NewStatus = DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded
                        });
                }
            }, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, client.State);
    }

    private static string CreateDemo(TempDir dir)
    {
        string path = dir.Combine("match.dem");
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    private static Cs2Compilation Compilation(string outputDirectory, string demoPath) => new()
    {
        Settings = new Cs2CompilationSettings
        {
            OutputDirectory = outputDirectory,
            BaseFileName = "highlight",
            ConcatenateClips = false
        },
        Clips =
        [
            new Cs2CompilationClip
            {
                PlayerSteamId = 76561198000000000,
                MatchChecksum = "checksum",
                DemoFilePath = demoPath,
                StartTick = 100,
                EndTick = 200,
                PlayerNameToSpectate = "s1mple"
            }
        ]
    };

    #endregion
}
