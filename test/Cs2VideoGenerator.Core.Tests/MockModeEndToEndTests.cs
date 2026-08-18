#region

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Collection for the mock-mode end-to-end test. The bundled mock_server binary
///     hardcodes its gRPC endpoint to 127.0.0.1:50051 (see
///     plugin/Cs2VideoGenerator.Plugin.Grpc/src/mock/mock_plugin.cpp - it parses only
///     --traceparent, no port argument), so the host under test must bind that exact port.
///     Parallelization is disabled: the test owns a machine-global resource (the port) and a
///     real child process, so nothing else may run concurrently with it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MockModeEndToEndCollection
{
    public const string Name = "MockModeEndToEnd (port 50051)";
}

/// <summary>
///     True end-to-end mock-mode smoke test: a real ASP.NET Core gRPC host built via
///     <c>AddCs2VideoGeneratorCore</c>, a real <see cref="MockCs2ProcessManager" /> launching the
///     real native mock_server binary, and the full public session lifecycle driven through
///     <see cref="CsvgVideoSession" />. This exercises the session composition,
///     Cs2GameService/Cs2GameSession and the process lifecycle together over an actual gRPC
///     connection - no fakes.
///     Skips cleanly when the mock_server binary is not present (CI downloads native artifacts
///     before dotnet test, so it runs there) or when port 50051 is already in use.
/// </summary>
[Collection(MockModeEndToEndCollection.Name)]
public class MockModeEndToEndTests
{
    /// <summary>The port the mock_server binary hardcodes (mock_plugin.cpp).</summary>
    private const int MockServerPort = 50051;

    /// <summary>Environment variable that overrides mock_server discovery (CI flexibility).</summary>
    private const string MockServerPathEnvVar = "CSVG_MOCK_SERVER_PATH";

    /// <summary>
    ///     Whole-test budget. Since the engine-thread frame hook landed the nominal run is a few
    ///     seconds: the plugin reports Ready as soon as it has read engine state and seen an engine
    ///     frame (the 5s sleep is now only a fallback cap), and the demo-load waits poll at engine
    ///     frame granularity instead of the old fixed sleeps. The budget stays generous because it
    ///     also covers a cold CI machine launching a real child process.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Frame-pump cadence for the mock's stand-in engine thread (mock_plugin.cpp). The default
    ///     (unset) is 250 Hz; "0" disables the pump entirely, which is how the direct-execute
    ///     fallback - the path a real plugin takes before its FrameStageNotify hook is installed -
    ///     gets exercised end to end.
    /// </summary>
    private const string FramePumpHzEnvVar = "CSVG_MOCK_FRAME_HZ";

    /// <summary>
    ///     The full lifecycle over both engine-command execution paths: the engine-thread frame hook
    ///     (default) and the direct-execute fallback used when no frame hook is installed. Both must
    ///     behave identically from the host's point of view - the fallback is what a real plugin runs
    ///     on before ClientFullyConnect installs the hook, so a regression there is invisible until a
    ///     real session's first demo load.
    /// </summary>
    [Theory]
    [InlineData(null)] // Frame hook installed (default 250 Hz pump)
    [InlineData("0")] // No frame hook: direct-execute fallback + polling-thread snapshots
    public async Task MockMode_FullSessionLifecycle_EndToEndSmokeTest(string? framePumpHz)
    {
        string mockServerPath = SkipUnlessMockServerCanRun();

        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutCts.CancelAfter(TestTimeout);
        CancellationToken ct = timeoutCts.Token;

        // mock_server inherits the test process's environment; this collection disables
        // parallelization, so mutating it here is safe as long as it is restored.
        string? previousFramePumpHz = Environment.GetEnvironmentVariable(FramePumpHzEnvVar);
        Environment.SetEnvironmentVariable(FramePumpHzEnvVar, framePumpHz);
        try
        {
            await RunFullLifecycleAsync(mockServerPath, framePumpHz, ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FramePumpHzEnvVar, previousFramePumpHz);
        }
    }

    private static async Task RunFullLifecycleAsync(string mockServerPath, string? framePumpHz,
        CancellationToken ct)
    {

        // the real host, wired the way the CLI wires it
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cs2VideoGenerator:MockMode"] = "true",
            ["Cs2VideoGenerator:ExternalMockServerPath"] = mockServerPath
            // VideoCaptureProvider is intentionally not configured: no capture provider is
            // registered, so this is a playback-only session (record: false).
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // mock_server's stdout is piped into this category by the process lifecycle manager, and
        // it carries the plugin's own execution-path markers - the only place outside the plugin
        // process where "queued for the engine thread" and "executed inline" are distinguishable.
        MockServerOutput mockOutput = new();
        builder.Logging.AddFilter(MockServerOutputCategory, LogLevel.Information);
        builder.Logging.AddProvider(mockOutput);
        builder.Services.AddCs2VideoGeneratorCore(builder.Configuration);
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenLocalhost(MockServerPort, o => o.Protocols = HttpProtocols.Http2));

        await using WebApplication app = builder.Build();
        app.MapGrpcService<Grpc.Cs2GameService>();
        await app.StartAsync(ct);

        CsvgVideoSession client = app.Services.GetRequiredService<CsvgVideoSession>();
        ICs2ProcessManager processManager = app.Services.GetRequiredService<ICs2ProcessManager>();
        Assert.IsType<MockCs2ProcessManager>(processManager); // mock-mode DI wiring took effect
        IMockUserActionInjector injector = app.Services.GetRequiredService<IMockUserActionInjector>();

        List<GameEventTelemetry> gameEvents = [];
        client.GameEventReceived += (_, telemetry) =>
        {
            lock (gameEvents)
            {
                gameEvents.Add(telemetry);
            }

            return Task.CompletedTask;
        };

        object gate = new();
        List<(CsvgSessionState OldState, CsvgSessionState NewState)> transitions = [];
        client.StateChanged += (oldState, newState) =>
        {
            lock (gate)
            {
                transitions.Add((oldState, newState));
            }

            return Task.CompletedTask;
        };

        int sessionStartedCount = 0;
        client.SessionStarted += _ =>
        {
            Interlocked.Increment(ref sessionStartedCount);
            return Task.CompletedTask;
        };

        int sessionEndedCount = 0;
        client.SessionEnded += (_, _, _) =>
        {
            Interlocked.Increment(ref sessionEndedCount);
            return Task.CompletedTask;
        };

        int? mockServerPid = null;
        try
        {
            Assert.Equal(CsvgSessionState.Disconnected, client.State);

            // Launches the real mock_server, which connects back over gRPC and reports Ready (~5s
            // after the stream is established). Capture init is skipped on purpose: this smoke test
            // covers the session lifecycle, and the OBS provider is registered even
            // without capture config.
            await client.StartAsync(new EngineSessionOptions(),
                initializeCapture: false, ct);

            Assert.Equal(CsvgSessionState.Connected, client.State);
            Assert.NotNull(client.Engine.SessionId);
            Assert.Equal(Cs2ProcessStatus.Types.ProcessStatus.Ready, client.Engine.LastProcessStatus?.Status);
            Assert.Equal(1, Volatile.Read(ref sessionStartedCount));

            mockServerPid = processManager.ProcessId;
            Assert.NotNull(mockServerPid);
            Assert.True(processManager.IsRunning, "mock_server should be running after the session start");
            Assert.True(IsProcessAlive(mockServerPid.Value), "mock_server PID should be alive after start");

            // Phase-1 capability tokens are advertised by the shared plugin client, so they must be
            // present regardless of which engine-command execution path this run exercises.
            Assert.True(client.Engine.Supports(CsvgCapabilities.TickScheduledCommands));
            Assert.True(client.Engine.Supports(CsvgCapabilities.GameEventTelemetry));
            Assert.True(client.Engine.Supports(CsvgCapabilities.ConsoleExec));

            // Telemetry is off until asked for: nothing has been forwarded yet, and it stays that
            // way until a subscription is sent.
            await injector.InjectAsync(MockUserActions.GameEvent("player_death", "{\"before\":true}"), ct);
            await Task.Delay(250, ct);
            lock (gameEvents)
            {
                Assert.Empty(gameEvents);
            }

            // since v1.1 the shared plugin client validates the demo file exists before issuing
            // playdemo ("load-failure-detection"), so the path has to be a real file. Its contents
            // are irrelevant; the mock engine never opens it.
            string tempDemoPath = Path.Combine(Path.GetTempPath(), $"csvg-smoke-{Guid.NewGuid():N}.dem");
            await File.WriteAllBytesAsync(tempDemoPath, [0x00], ct);
            try
            {
                await client.Engine.LoadDemoAsync(tempDemoPath, ct);
                Assert.Equal(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded,
                    client.Engine.LastDemoPlaybackStatusChange?.NewStatus);
            }
            finally
            {
                File.Delete(tempDemoPath);
            }

            // only events fired while the subscription is active reach the host, and the filter is
            // honored
            await client.Engine.SetGameEventSubscriptionAsync(true, ["round_start"], ct);
            await injector.InjectAsync(MockUserActions.GameEvent("player_death", "{\"filtered\":true}"), ct);
            await injector.InjectAsync(MockUserActions.GameEvent("round_start", "{\"timelimit\":115}"), ct);
            await WaitUntilAsync(() =>
            {
                lock (gameEvents)
                {
                    return gameEvents.Count > 0;
                }
            }, ct);
            await Task.Delay(250, ct); // Let a wrongly-forwarded filtered event arrive, if any
            lock (gameEvents)
            {
                GameEventTelemetry telemetry = Assert.Single(gameEvents);
                Assert.Equal("round_start", telemetry.EventName);
                Assert.Equal("{\"timelimit\":115}", telemetry.JsonPayload);
            }

            // Tick-scheduled commands: registration is acknowledged by the plugin, and the demo is
            // still paused at tick 0, so the schedule is armed ahead of the range played below.
            await client.Engine.ScheduleCommandsAtTickAsync(128, ["spec_mode 1"], ct);
            await client.Engine.ClearScheduledCommandsAsync(ct);

            // Immediate console execution ("console-exec"), after the schedules are cleared so the
            // jump below cannot fire one. The command is blind - the ack only promises it was queued
            // for the engine thread - so the proof it actually reached the engine is the engine's own
            // state moving: the mock is paused at tick 0, so nothing but this command can seek it.
            const int consoleExecTick = 640;
            await client.Engine.ExecuteConsoleCommandsAsync([$"demo_gototick {consoleExecTick}"], ct);
            await WaitForDemoTickAsync(client, consoleExecTick, ct);

            // ...and it got there over the engine-thread queue, not from the gRPC reader thread.
            // With the frame pump running the plugin queues and drains at FRAME_START; with the pump
            // disabled it takes the direct-execute fallback, which is the designed behavior for a
            // plugin whose frame hook is not installed. Either way the command is routed by
            // ExecuteEngineCommand - a handler calling ExecuteClientCmd itself would log neither.
            string expectedMarker = framePumpHz is null ? "[CS2 - Engine Execute]" : "[CS2 - Direct Execute]";
            string rejectedMarker = framePumpHz is null ? "[CS2 - Direct Execute]" : "[CS2 - Engine Execute]";
            string executedLine = $"{expectedMarker} demo_gototick {consoleExecTick}";
            string rejectedLine = $"{rejectedMarker} demo_gototick {consoleExecTick}";
            await WaitUntilAsync(() => mockOutput.Lines.Any(line => line.Contains(executedLine, StringComparison.Ordinal)),
                ct);
            Assert.DoesNotContain(mockOutput.Lines,
                line => line.Contains(rejectedLine, StringComparison.Ordinal));

            // Play a small tick range (2 seconds at the mock's 64 ticks/s) without recording.
            // The plugin auto-pauses at the end tick and force-sends the final tick update.
            DemoPlaybackResult playback = await client.PlayTickRangeAsync(64, 192, cancellationToken: ct);

            Assert.True(playback.Success, $"Playback failed: {playback.ErrorMessage}");
            Assert.False(playback.WasCaptured);
            Assert.Equal(64, playback.StartTick);
            Assert.Equal(192, playback.EndTick);
            Assert.NotNull(client.Engine.LastTick);
            Assert.True(client.Engine.LastTick >= 192, $"LastTick {client.Engine.LastTick} should be >= end tick 192");
            Assert.Equal(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused,
                client.Engine.LastDemoPlaybackStatusChange?.NewStatus);
            Assert.Equal(CsvgSessionState.Connected, client.State);

            // graceful gRPC session stop, then the process manager stops mock_server
            await client.StopAsync(ct);

            Assert.Equal(CsvgSessionState.Disconnected, client.State);
            Assert.Null(client.Engine.SessionId);
            Assert.Equal(1, Volatile.Read(ref sessionEndedCount));

            // No orphaned mock_server process after a clean stop.
            await AssertProcessExitsAsync(mockServerPid.Value, TimeSpan.FromSeconds(10));

            (CsvgSessionState, CsvgSessionState)[] observed;
            lock (gate)
            {
                observed = [.. transitions];
            }

            Assert.Equal(
            [
                (CsvgSessionState.Disconnected, CsvgSessionState.Connecting),
                (CsvgSessionState.Connecting, CsvgSessionState.Connected),
                (CsvgSessionState.Connected, CsvgSessionState.Disconnecting),
                (CsvgSessionState.Disconnecting, CsvgSessionState.Disconnected)
            ], observed);

            Assert.Equal(1, Volatile.Read(ref sessionEndedCount));
        }
        finally
        {
            // Safety net: never leave a mock_server behind, even when an assert above failed.
            try
            {
                await client.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch
            {
                // Best effort - the direct kill below is the last resort.
            }

            KillIfAlive(mockServerPid ?? processManager.ProcessId);
        }
    }

    #region Compilation

    /// <summary>
    ///     Whether a real ffmpeg is on PATH. Mirrors the availability gate in
    ///     <c>FfmpegServiceTests</c>: with ffmpeg the compilation below produces real (tiny) clip files
    ///     and its concatenation runs for real; without it the clips are placeholders and concatenation
    ///     is switched off rather than asserted against a missing binary.
    /// </summary>
    private static readonly Lazy<bool> FfmpegAvailable = new(() =>
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process == null)
            {
                return false;
            }

            // Drain before waiting: `ffmpeg -version` prints its whole configure line, which on a
            // full build is already most of the pipe buffer. An undrained pipe would hang this
            // probe rather than answer it.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            process.WaitForExit(10_000);
            if (!process.HasExited)
            {
                return false;
            }

            stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    ///     The compilation pipeline end to end: a real DI-composed graph (<see cref="CsvgVideoSession" /> →
    ///     <see cref="Cs2CompilationRunner" /> → <see cref="ClipCaptureCoordinator" /> → capture backend
    ///     → the real mock plugin over gRPC) runs a three-clip, two-demo compilation. The capture
    ///     backend is a stub registered through the documented host-override seam (an unkeyed
    ///     <see cref="ICaptureSessionFactory" /> registered before the core registrations wins over the
    ///     keyed registry's selection forwarder), so every step except the screen recorder itself is the
    ///     production one: demo grouping and reload, per-clip demo options and spectator target, the
    ///     coordinator's spectator re-assert scheduling, original-index output naming, and the ffmpeg
    ///     concatenation.
    /// </summary>
    [Fact]
    public async Task MockMode_MultiDemoCompilation_CapturesEveryClipAndConcatenates()
    {
        string mockServerPath = SkipUnlessMockServerCanRun();

        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutCts.CancelAfter(TestTimeout);
        CancellationToken ct = timeoutCts.Token;

        using TempDir workspace = new("csvg-e2e-compilation-");
        bool ffmpegAvailable = FfmpegAvailable.Value;
        StubCaptureSessionFactory captureFactory = new(ffmpegAvailable);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cs2VideoGenerator:MockMode"] = "true",
            ["Cs2VideoGenerator:ExternalMockServerPath"] = mockServerPath
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // registered before the core services: the capture registrations are TryAdds, so a
        // host-supplied unkeyed factory wins over the keyed registry's provider forwarder
        builder.Services.AddSingleton<ICaptureSessionFactory>(captureFactory);
        builder.Services.AddCs2VideoGeneratorCore(builder.Configuration);
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenLocalhost(MockServerPort, o => o.Protocols = HttpProtocols.Http2));

        await using WebApplication app = builder.Build();
        app.MapGrpcService<Grpc.Cs2GameService>();
        await app.StartAsync(ct);

        CsvgVideoSession client = app.Services.GetRequiredService<CsvgVideoSession>();
        Assert.Same(captureFactory, app.Services.GetRequiredService<ICaptureSessionFactory>());
        Assert.True(client.HasCaptureProvider);

        ICs2ProcessManager processManager = app.Services.GetRequiredService<ICs2ProcessManager>();

        // the plugin validates that a demo file exists before issuing playdemo; the contents are
        // irrelevant, the mock engine never opens them
        string firstDemo = workspace.Combine("first.dem");
        string secondDemo = workspace.Combine("second.dem");
        await File.WriteAllBytesAsync(firstDemo, [0x00], ct);
        await File.WriteAllBytesAsync(secondDemo, [0x00], ct);

        Cs2CompilationSettings settings = new()
        {
            OutputDirectory = workspace.Path,
            BaseFileName = "compilation",
            FrameRate = 30,
            CaptureAudio = false,
            // Concatenation needs real video files to copy streams from, which needs ffmpeg to make
            // them; without it the clips are placeholders and the pipeline runs concat-free.
            ConcatenateClips = ffmpegAvailable
        };

        // Clip 1 sits on the second demo while clips 0 and 2 sit on the first: the runner groups by
        // demo (first demo's two clips, then a close + reload for the second) yet numbers outputs by
        // the original clip order.
        Cs2Compilation compilation = new()
        {
            Settings = settings,
            Clips =
            [
                NewClip(firstDemo, 64, 128, "player-one"),
                NewClip(secondDemo, 64, 128, "player-two"),
                NewClip(firstDemo, 256, 320, "player-three")
            ]
        };

        List<int> clipsStarted = [];
        List<Cs2ClipResult> clipsCompleted = [];
        client.CompilationClipStarted += (index, _) =>
        {
            lock (clipsStarted)
            {
                clipsStarted.Add(index);
            }

            return Task.CompletedTask;
        };
        client.CompilationClipCompleted += (_, result) =>
        {
            lock (clipsCompleted)
            {
                clipsCompleted.Add(result);
            }

            return Task.CompletedTask;
        };

        int? mockServerPid = null;
        try
        {
            await client.StartAsync(new EngineSessionOptions(),
                initializeCapture: true, ct);

            Assert.Equal(CsvgSessionState.Connected, client.State);
            mockServerPid = processManager.ProcessId;

            // Capture came up before CS2 was launched, exactly once, on the current engine session.
            StubCaptureSession captureSession = Assert.Single(captureFactory.CreatedSessions);
            Assert.Equal(1, captureSession.InitializeCallCount);

            Cs2CompilationResult result = await client.RunCompilationAsync(compilation, ct);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(3, result.TotalClips);
            Assert.Equal(3, result.CompletedClips);

            for (int clipIndex = 0; clipIndex < 3; clipIndex++)
            {
                Cs2ClipResult clipResult = result.ClipResults[clipIndex];
                string expectedPath = workspace.Combine($"compilation_{clipIndex:D3}.mp4");

                Assert.Equal(clipIndex, clipResult.ClipIndex);
                Assert.True(clipResult.Success, clipResult.ErrorMessage);
                Assert.Equal(expectedPath, clipResult.OutputFilePath);
                Assert.True(File.Exists(expectedPath), $"clip {clipIndex} output missing: {expectedPath}");
                Assert.True(new FileInfo(expectedPath).Length > 0, $"clip {clipIndex} output is empty");
            }

            // Each demo was loaded once, in group order, and the backend was told about both.
            Assert.Equal([firstDemo, secondDemo], captureSession.DemosLoaded);

            // Playback ran the requested ranges, grouped by demo and ascending within a group.
            Assert.Equal([(64, 128), (256, 320), (64, 128)], captureSession.PlayedClipRanges);

            // Every clip reported start and completion through the client's events, in processing
            // order (grouped by demo: the first demo's two clips, then the second demo's). The
            // output files above are the ones numbered by original order.
            Assert.Equal([0, 2, 1], clipsStarted);
            Assert.Equal([0, 2, 1], clipsCompleted.Select(clip => clip.ClipIndex));
            Assert.All(clipsCompleted, clip => Assert.True(clip.Success));

            if (ffmpegAvailable)
            {
                string concatenatedPath = settings.GetConcatenatedOutputPath();
                Assert.NotNull(result.ConcatenationResult);
                Assert.True(result.ConcatenationResult.Success, result.ConcatenationResult.ErrorMessage);
                Assert.Equal(concatenatedPath, result.ConcatenatedOutputPath);
                Assert.True(File.Exists(concatenatedPath), $"concatenated output missing: {concatenatedPath}");

                // The concatenation carries all three clips, so it outweighs any single one of them.
                long largestClip = Enumerable.Range(0, 3)
                    .Max(i => new FileInfo(workspace.Combine($"compilation_{i:D3}.mp4")).Length);
                Assert.True(new FileInfo(concatenatedPath).Length > largestClip,
                    "concatenated output should be larger than the largest single clip");
            }
            else
            {
                Assert.Null(result.ConcatenationResult);
            }

            await client.StopAsync(ct);
            Assert.Equal(CsvgSessionState.Disconnected, client.State);
            Assert.Equal(1, captureSession.ShutdownCallCount);
        }
        finally
        {
            try
            {
                await client.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch
            {
                // Best effort - the direct kill below is the last resort.
            }

            KillIfAlive(mockServerPid ?? processManager.ProcessId);
        }
    }

    private static Cs2CompilationClip NewClip(string demoPath, int startTick, int endTick, string playerName) =>
        new()
        {
            PlayerSteamId = 76561198000000000 + startTick,
            MatchChecksum = "e2e",
            DemoFilePath = demoPath,
            StartTick = startTick,
            EndTick = endTick,
            PlayerNameToSpectate = playerName
        };

    /// <summary>
    ///     Hands out <see cref="StubCaptureSession" />s. Stands in for a screen recorder in the
    ///     compilation end-to-end test; everything else in the pipeline is the production component.
    /// </summary>
    private sealed class StubCaptureSessionFactory(bool ffmpegAvailable) : ICaptureSessionFactory
    {
        /// <summary>The sessions created so far - one per CS2 run.</summary>
        public List<StubCaptureSession> CreatedSessions { get; } = [];

        /// <inheritdoc />
        public string ProviderName => "StubFileRecorder";

        /// <inheritdoc />
        public bool IsSupportedOnCurrentPlatform => true;

        /// <inheritdoc />
        public Task<CaptureAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CaptureAvailabilityResult.Succeeded("stub-1.0"));

        /// <inheritdoc />
        public ICaptureSession Create(CaptureSessionContext context)
        {
            StubCaptureSession session = new(ProviderName, ffmpegAvailable);
            CreatedSessions.Add(session);
            return session;
        }
    }

    /// <summary>
    ///     A capture session that writes one small but genuine video file per clip (via ffmpeg when it
    ///     is available, a placeholder file otherwise), so the compilation pipeline's per-clip outputs
    ///     and its concatenation are exercised for real.
    /// </summary>
    private sealed class StubCaptureSession(string providerName, bool ffmpegAvailable) : ICaptureSession
    {
        /// <summary>How many times <see cref="InitializeAsync" /> ran.</summary>
        public int InitializeCallCount { get; private set; }

        /// <summary>How many times <see cref="ShutdownAsync" /> ran.</summary>
        public int ShutdownCallCount { get; private set; }

        /// <summary>Demo paths this backend was told about, in order.</summary>
        public List<string> DemosLoaded { get; } = [];

        /// <summary>The tick range of every clip this session recorded, in order.</summary>
        public List<(int StartTick, int EndTick)> PlayedClipRanges { get; } = [];

        /// <inheritdoc />
        public string ProviderName => providerName;

        /// <inheritdoc />
        public CaptureTraits Traits => new()
        {
            TimingModel = CaptureTimingModel.WallClock,
            UsesEngine = false,
            HonorsFrameRate = false
        };

        // A file-writing stub has no progress counters and no backend to fail, so it never raises
        // either diagnostic event; they exist only to satisfy the interface.
#pragma warning disable CS0067

        /// <inheritdoc />
        public event EventHandler<CaptureProgressEventArgs>? ProgressUpdated;

        /// <inheritdoc />
        public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

#pragma warning restore CS0067

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnDemoLoadedAsync(string demoFilePath, CancellationToken cancellationToken = default)
        {
            DemosLoaded.Add(demoFilePath);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IClipCapture> BeginClipAsync(ClipCaptureContext context,
            CancellationToken cancellationToken = default)
        {
            PlayedClipRanges.Add((context.StartTick, context.EndTick));
            return Task.FromResult<IClipCapture>(new StubClipCapture(context, ffmpegAvailable));
        }

        /// <inheritdoc />
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCallCount++;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    ///     One clip's stub recording: times the playback bracket and produces the output file the
    ///     pipeline expects at <see cref="CaptureMetadata.GetFullOutputPath" />.
    /// </summary>
    private sealed class StubClipCapture(ClipCaptureContext context, bool ffmpegAvailable) : IClipCapture
    {
        private readonly Stopwatch _stopwatch = new();

        /// <inheritdoc />
        public TickCommandPlan? TickPlan => null;

        /// <inheritdoc />
        public Task OnPlaybackStartingAsync(CancellationToken cancellationToken = default)
        {
            _stopwatch.Restart();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnPlaybackEndedAsync(CancellationToken cancellationToken = default)
        {
            _stopwatch.Stop();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<CaptureResult> FinalizeAsync(CancellationToken cancellationToken = default)
        {
            string outputPath = context.Metadata.GetFullOutputPath();
            Directory.CreateDirectory(context.Metadata.OutputDirectory);

            if (ffmpegAvailable)
            {
                await WriteTinyClipAsync(outputPath, cancellationToken);
            }
            else
            {
                await File.WriteAllTextAsync(outputPath, "stub clip (no ffmpeg available)", cancellationToken);
            }

            return new CaptureResult
            {
                Success = true,
                OutputFilePath = outputPath,
                Duration = _stopwatch.Elapsed,
                FileSizeBytes = new FileInfo(outputPath).Length,
                TotalFrames = context.EndTick - context.StartTick
            };
        }

        /// <inheritdoc />
        public Task AbortAsync(CaptureAbortReason reason, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>
        ///     Renders a half-second color-bar clip with ffmpeg - small enough to be cheap, real enough
        ///     for the concat demuxer to stream-copy.
        /// </summary>
        private static async Task WriteTinyClipAsync(string outputPath, CancellationToken cancellationToken)
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    "-y -f lavfi -i testsrc=duration=0.5:size=160x120:rate=30 " +
                    $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            Assert.NotNull(process);

            // Both pipes have to be drained while ffmpeg runs. Its stderr banner and progress can
            // exceed the pipe buffer, and a blocked write means the process never exits - observed
            // as the await below hanging until the test's own budget cancels it.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);

            Assert.True(process.ExitCode == 0, $"ffmpeg exited with {process.ExitCode}: {stderr.Result}");
        }
    }

    #endregion

    #region mock_server discovery

    /// <summary>
    ///     The skip discipline every case in this class shares: the native mock_server binary has to
    ///     exist (and be executable), and the port it hardcodes has to be free. Returns the binary's
    ///     path.
    /// </summary>
    private static string SkipUnlessMockServerCanRun()
    {
        string? mockServerPath = ResolveMockServerPath();
        Assert.SkipWhen(mockServerPath is null,
            $"mock_server binary not found. Set {MockServerPathEnvVar} or build the native mock server " +
            "(artifacts/build/Cs2VideoGenerator.Plugin.GRPC/<config>/bin/<rid>/).");
        EnsureExecutable(mockServerPath);

        Assert.SkipUnless(IsPortFree(MockServerPort),
            $"Port {MockServerPort} is already in use; the mock_server binary hardcodes 127.0.0.1:{MockServerPort} " +
            "so this test cannot use a dynamic port.");

        return mockServerPath;
    }

    /// <summary>
    ///     Locates the native mock_server binary: the <c>CSVG_MOCK_SERVER_PATH</c> environment
    ///     variable wins; otherwise the native build output is probed for the current RID
    ///     (computed the same way as <c>NativeAssetProvider</c>) by walking up from the test
    ///     directory, which finds <c>artifacts/build/...</c> both at the repo root (CI downloads
    ///     native artifacts there before dotnet test) and above a git worktree checkout.
    /// </summary>
    private static string? ResolveMockServerPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(MockServerPathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        string rid = NativeAssetProvider.GetRuntimeIdentifier();
        string fileName = OperatingSystem.IsWindows() ? "mock_server.exe" : "mock_server";

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (string config in (string[])["Release", "Debug"])
            {
                string candidate = Path.Combine(dir.FullName,
                    "artifacts", "build", "Cs2VideoGenerator.Plugin.GRPC", config, "bin", rid, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     CI artifact downloads (actions/download-artifact) do not preserve the Unix executable
    ///     bit, so restore it before launching. Best effort - if it cannot be set, the launch
    ///     failure will surface with a clear error anyway.
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
            {
                File.SetUnixFileMode(path,
                    mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    #endregion

    #region Process / port helpers

    /// <summary>Polls until the condition holds or the test budget runs out.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    /// <summary>
    ///     Polls engine truth (a real <c>QueryDemoState</c> round trip, not the tick-update stream)
    ///     until the demo sits at <paramref name="tick" />. The caller's token carries the test budget,
    ///     so a command that never reaches the engine surfaces as a timeout rather than a hang.
    /// </summary>
    private static async Task WaitForDemoTickAsync(CsvgVideoSession client, int tick,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DemoState state = await client.Engine.QueryDemoStateAsync(cancellationToken);
            if (state.DemoTick == tick)
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    /// <summary>The log category the process lifecycle manager pipes mock_server's stdout into.</summary>
    private const string MockServerOutputCategory = "Cs2VideoGenerator.Core.MockCs2Process";

    /// <summary>
    ///     Captures the log lines the host writes, which is how mock_server's own stdout becomes
    ///     assertable from the test process.
    /// </summary>
    private sealed class MockServerOutput : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        /// <summary>The lines captured so far.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        private sealed class CapturingLogger(MockServerOutput owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Add(formatter(state, exception));
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            TcpListener listener = new(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // No such process
        }
        catch (InvalidOperationException)
        {
            return false; // Process has exited
        }
    }

    private static async Task AssertProcessExitsAsync(int pid, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!IsProcessAlive(pid))
            {
                return;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        Assert.Fail($"mock_server (pid {pid}) is still running after StopSessionAsync - orphaned process.");
    }

    private static void KillIfAlive(int? pid)
    {
        if (pid is null)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(pid.Value);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    #endregion
}
