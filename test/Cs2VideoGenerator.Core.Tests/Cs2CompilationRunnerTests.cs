#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Tests for <see cref="Cs2CompilationRunner" />, the compilation pipeline. Driven through the
///     capture fakes and the real <see cref="ClipCaptureCoordinator" />, so what is proven here is the
///     pipeline's own contract: each demo loaded exactly once, clips recorded in tick order but
///     numbered by their original position, the first failed clip ending the run, and the
///     concatenation stage's partial-success shapes.
/// </summary>
public class Cs2CompilationRunnerTests
{
    private const string Player = "s1mple";

    /// <summary>
    ///     Clips are grouped by demo so each demo is loaded once, groups run in first-appearance order
    ///     (not sorted by path), and within a group clips run in ascending start-tick order - the whole
    ///     point of the grouping is avoiding demo reloads.
    /// </summary>
    [Fact]
    public async Task GroupsClipsByDemo_LoadingEachOnce_AndPlaysThemInAscendingTickOrder()
    {
        using TempDir dir = new();
        string demoB = CreateDemo(dir, "b");
        string demoA = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);

        Cs2Compilation compilation = Compilation(Settings(dir.Path, concatenate: false),
            Clip(demoB, 200), Clip(demoA, 100), Clip(demoB, 50));

        Cs2CompilationResult result = await Runner()
            .RunAsync(compilation, Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);

        // The demo b group first (it appears first), its clips at 50 then 200, then demo a.
        Assert.Equal([demoB, demoA], engine.LoadedDemos);
        Assert.Equal([(50, 150, (TimeSpan?)null), (200, 300, null), (100, 200, null)], engine.PlayedRanges);

        // One close between the two groups plus the final cleanup close.
        Assert.Equal(2, engine.CloseDemoCallCount);
        Assert.Equal(
        [
            $"loadDemo:{demoB}",
            $"session.demoLoaded:{demoB}",
            "clipStarted:2", "clipCompleted:2:True",
            "clipStarted:0", "clipCompleted:0:True",
            "closeDemo",
            $"loadDemo:{demoA}",
            $"session.demoLoaded:{demoA}",
            "clipStarted:1", "clipCompleted:1:True",
            "closeDemo"
        ], DemoAndClipEntries(journal));
    }

    /// <summary>
    ///     Output files are numbered by the clip's original position in the compilation, not the order
    ///     it happened to be recorded in: highlight_000 is the first clip in the input regardless of
    ///     which demo it came from or how late its tick range is.
    /// </summary>
    [Fact]
    public async Task ClipResults_AreNumberedAndNamedByOriginalIndex_NotProcessingOrder()
    {
        using TempDir dir = new();
        string demoB = CreateDemo(dir, "b");
        string demoA = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);

        Cs2Compilation compilation = Compilation(Settings(dir.Path, concatenate: false),
            Clip(demoB, 200), Clip(demoA, 100), Clip(demoB, 50));

        Cs2CompilationResult result = await Runner()
            .RunAsync(compilation, Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.Equal([0, 1, 2], result.ClipResults.Select(r => r.ClipIndex));
        Assert.Equal(
        [
            Path.Combine(dir.Path, "highlight_000.mp4"),
            Path.Combine(dir.Path, "highlight_001.mp4"),
            Path.Combine(dir.Path, "highlight_002.mp4")
        ], result.ClipResults.Select(r => r.OutputFilePath));

        // Recording order was 2, 0, 1 - the names still follow the input order.
        Assert.Equal(["highlight_002", "highlight_000", "highlight_001"],
            capture.Clips.Select(c => c.Context.Metadata.OutputFileName));
        Assert.Equal([2, 0, 1], capture.Clips.Select(c => c.Context.ClipIndex));
    }

    /// <summary>
    ///     The demo-loaded notification (HLAE's mirv registration gate) fires exactly once per
    ///     successful demo load - once per group, not once per clip.
    /// </summary>
    [Fact]
    public async Task NotifiesTheCaptureSession_OncePerDemoLoad()
    {
        using TempDir dir = new();
        string demoA = CreateDemo(dir, "a");
        string demoB = CreateDemo(dir, "b");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);

        // Two clips from demo a, one from demo b, interleaved in the input.
        Cs2Compilation compilation = Compilation(Settings(dir.Path, concatenate: false),
            Clip(demoA, 100), Clip(demoB, 100), Clip(demoA, 300));

        await Runner().RunAsync(compilation, Context(engine, capture, journal),
            TestContext.Current.CancellationToken);

        Assert.Equal([demoA, demoB], capture.DemosLoaded);
        Assert.Equal(engine.LoadedDemos, capture.DemosLoaded);
    }

    /// <summary>
    ///     Every clip gets its own demo options and spectator target applied before recording starts,
    ///     and the spectator re-assert the coordinator schedules lands one tick before the clip - proof
    ///     the runner passes the spectated player down into the clip plan.
    /// </summary>
    [Fact]
    public async Task AppliesDemoOptionsAndSpectatorTarget_PerClip()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal);

        Cs2Compilation compilation = Compilation(Settings(dir.Path, concatenate: false),
            Clip(demo, 100, player: "donk", options: Cs2ClipOptions.NoHudDefault),
            Clip(demo, 400, player: "ropz"));

        await Runner().RunAsync(compilation, Context(engine, capture, journal),
            TestContext.Current.CancellationToken);

        Assert.Equal([Cs2ClipOptions.NoHudDefault, Cs2ClipOptions.Default], engine.DemoOptionsSet);
        Assert.Equal(["donk", "ropz"], engine.SpectatorTargetsSet);
        Assert.Equal([99, 399], engine.ScheduledBatches.Select(batch => batch.DemoTick));
        Assert.Equal(["spec_player \"donk\"", "spec_player \"ropz\""],
            engine.ScheduledBatches.SelectMany(batch => batch.Commands));
    }

    /// <summary>
    ///     The per-clip capture metadata is built from the compilation settings in one place, with the
    ///     clip's own audio override winning over the compilation-level setting.
    /// </summary>
    [Fact]
    public async Task ClipMetadata_ComesFromTheCompilationSettings_WithThePerClipAudioOverride()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);

        Cs2CompilationSettings settings = new()
        {
            OutputDirectory = dir.Path,
            BaseFileName = "highlight",
            ContainerFormat = "mkv",
            FrameRate = 120,
            Width = 2560,
            Height = 1440,
            CaptureAudio = true,
            ConcatenateClips = false
        };

        Cs2Compilation compilation = Compilation(settings,
            Clip(demo, 100, captureAudio: false, playbackSpeed: 0.5, produceNormalizedCopy: true),
            Clip(demo, 400));

        await Runner().RunAsync(compilation, Context(engine, capture, journal),
            TestContext.Current.CancellationToken);

        CaptureMetadata first = capture.Clips[0].Context.Metadata;
        Assert.Equal(dir.Path, first.OutputDirectory);
        Assert.Equal("highlight_000", first.OutputFileName);
        Assert.Equal("mkv", first.ContainerFormat);
        Assert.Equal(120, first.FrameRate);
        Assert.Equal(2560, first.Width);
        Assert.Equal(1440, first.Height);
        Assert.False(first.CaptureAudio); // Per-clip override
        Assert.True(capture.Clips[1].Context.Metadata.CaptureAudio); // Compilation-level default

        // Per-clip speed flows through the one mapping point; a clip that set none defaults to 1x.
        Assert.Equal(0.5, first.PlaybackSpeed);
        Assert.True(first.ProduceNormalizedCopy);
        Assert.Equal(1.0, capture.Clips[1].Context.Metadata.PlaybackSpeed);
        Assert.False(capture.Clips[1].Context.Metadata.ProduceNormalizedCopy);

        // The clip plan carries the rest of the per-clip context, with no explicit playback timeout.
        Assert.Equal(Player, capture.Clips[0].Context.SpectatorTarget);
        Assert.Equal(Cs2ClipOptions.Default, capture.Clips[0].Context.ClipOptions);
        Assert.Null(capture.Clips[0].Context.PlaybackTimeout);
    }

    /// <summary>
    ///     Fail-fast: the first failed clip ends the run. It is still reported through the clip-completed
    ///     callback before the failed result is returned, nothing after it is recorded, and the demo is
    ///     deliberately left open (the cleanup close is skipped by the early return).
    /// </summary>
    [Fact]
    public async Task FirstFailedClip_EndsTheRun_AfterReportingItThroughClipCompleted()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        engine.PlaybackBehavior = (startTick, _, _, _) => startTick == 200
            ? throw new TimeoutException("playback stalled")
            : Task.CompletedTask;
        FakeCaptureSession capture = new(journal);

        Cs2Compilation compilation = Compilation(Settings(dir.Path),
            Clip(demo, 100), Clip(demo, 200), Clip(demo, 300));

        Cs2CompilationResult result = await Runner(new RecordingFfmpegService())
            .RunAsync(compilation, Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("playback stalled", result.ErrorMessage);

        // Only the clips reached so far, with the failure in the last of them.
        Assert.Equal([0, 1], result.ClipResults.Select(r => r.ClipIndex));
        Assert.False(result.ClipResults[1].Success);
        Assert.Equal("playback stalled", result.ClipResults[1].ErrorMessage);

        // The third clip was never begun, and the run reported the failure before returning.
        Assert.Equal(2, capture.Clips.Count);
        Assert.Equal(
        [
            $"loadDemo:{demo}", $"session.demoLoaded:{demo}",
            "clipStarted:0", "clipCompleted:0:True",
            "clipStarted:1", "clipCompleted:1:False"
        ], DemoAndClipEntries(journal));
        Assert.Equal(0, engine.CloseDemoCallCount);
    }

    /// <summary>
    ///     Clip-index order, not recording order, into the compilation's single output path.
    /// </summary>
    [Fact]
    public async Task Concatenation_PassesTheClipPathsInIndexOrder()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);
        RecordingFfmpegService ffmpeg = new();

        // Input order 0, 1 but recording order 1, 0 (ascending ticks within the group).
        Cs2Compilation compilation = Compilation(Settings(dir.Path), Clip(demo, 400), Clip(demo, 100));

        Cs2CompilationResult result = await Runner(ffmpeg)
            .RunAsync(compilation, Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        (IReadOnlyList<string> inputs, string outputPath, Cs2EncodingSettings? encoding) =
            Assert.Single(ffmpeg.Calls);
        Assert.Equal(
        [
            Path.Combine(dir.Path, "highlight_000.mp4"),
            Path.Combine(dir.Path, "highlight_001.mp4")
        ], inputs);
        Assert.Equal(Path.Combine(dir.Path, "highlight.mp4"), outputPath);
        Assert.Null(encoding);
        Assert.Equal(outputPath, result.ConcatenatedOutputPath);
        Assert.True(result.ConcatenationResult?.Success);
    }

    /// <summary>
    ///     A single clip with no re-encode requested is copied to the compilation output rather than run
    ///     through FFmpeg - there is nothing to concatenate.
    /// </summary>
    [Fact]
    public async Task SingleClip_WithoutReEncode_IsCopiedInsteadOfConcatenated()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");
        WriteClipFiles(dir, 1);

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);
        RecordingFfmpegService ffmpeg = new();

        Cs2CompilationResult result = await Runner(ffmpeg).RunAsync(
            Compilation(Settings(dir.Path), Clip(demo, 100)),
            Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(ffmpeg.Calls);
        Assert.True(File.Exists(Path.Combine(dir.Path, "highlight.mp4")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "highlight_000.mp4")));
        Assert.Equal(Path.Combine(dir.Path, "highlight.mp4"), result.ConcatenatedOutputPath);
    }

    /// <summary>
    ///     A single clip that does need re-encoding still goes through FFmpeg - the copy shortcut only
    ///     applies to a lossless pass-through.
    /// </summary>
    [Fact]
    public async Task SingleClip_WithReEncode_StillGoesThroughFfmpeg()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");
        WriteClipFiles(dir, 1);

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);
        RecordingFfmpegService ffmpeg = new();
        Cs2EncodingSettings encoding = new() { VideoCodec = "libx264", Crf = 20 };

        Cs2CompilationResult result = await Runner(ffmpeg).RunAsync(
            Compilation(Settings(dir.Path, encoding: encoding), Clip(demo, 100)),
            Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Same(encoding, Assert.Single(ffmpeg.Calls).Encoding);
    }

    /// <summary>With concatenation switched off, the clips themselves are the deliverable.</summary>
    [Fact]
    public async Task ConcatenationDisabled_NeverCallsFfmpeg()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);
        RecordingFfmpegService ffmpeg = new();

        Cs2CompilationResult result = await Runner(ffmpeg).RunAsync(
            Compilation(Settings(dir.Path, concatenate: false), Clip(demo, 100), Clip(demo, 200)),
            Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(ffmpeg.Calls);
        Assert.Null(result.ConcatenationResult);
        Assert.Null(result.ConcatenatedOutputPath);
        Assert.Equal(2, result.ClipResults.Count);
    }

    /// <summary>Clip files are deleted only after a successful concatenation, and only on request.</summary>
    [Fact]
    public async Task DeleteClipsAfterConcat_RemovesTheClipFilesOnceConcatenationSucceeded()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");
        WriteClipFiles(dir, 2);

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);

        Cs2CompilationResult result = await Runner(new RecordingFfmpegService()).RunAsync(
            Compilation(Settings(dir.Path, deleteAfterConcat: true), Clip(demo, 100), Clip(demo, 200)),
            Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(dir.Path, "highlight_000.mp4")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "highlight_001.mp4")));
    }

    /// <summary>
    ///     No FFmpeg service means no concatenation, but the clips were still captured: the run reports
    ///     partial success naming the missing service, and the clip files survive even though the
    ///     compilation asked for them to be deleted after concatenation.
    /// </summary>
    [Fact]
    public async Task WithoutFfmpegService_ReportsPartialSuccess_AndKeepsTheClips()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");
        WriteClipFiles(dir, 2);

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        FakeCaptureSession capture = new(journal);

        Cs2CompilationResult result = await Runner().RunAsync(
            Compilation(Settings(dir.Path, deleteAfterConcat: true), Clip(demo, 100), Clip(demo, 200)),
            Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Clips captured successfully but concatenation failed: FFmpeg service is not configured",
            result.ErrorMessage);
        Assert.False(result.ConcatenationResult?.Success);
        Assert.Equal("FFmpeg service is not configured", result.ConcatenationResult?.ErrorMessage);
        Assert.Equal(Path.Combine(dir.Path, "highlight.mp4"), result.ConcatenationResult?.OutputPath);
        Assert.Null(result.ConcatenatedOutputPath);

        // Both clips are intact: a failed concatenation must never take the sources with it.
        Assert.True(File.Exists(Path.Combine(dir.Path, "highlight_000.mp4")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "highlight_001.mp4")));
        Assert.Equal(2, result.ClipResults.Count);
        Assert.All(result.ClipResults, r => Assert.True(r.Success));
    }

    /// <summary>
    ///     Validation runs before the capture-readiness check: an invalid compilation is an
    ///     <see cref="ArgumentException" /> even when capture is not up, so the caller hears about the
    ///     input it can fix.
    /// </summary>
    [Fact]
    public async Task InvalidCompilation_ThrowsBeforeTheCaptureReadinessCheck()
    {
        using TempDir dir = new();
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);

        Cs2Compilation compilation = Compilation(Settings(dir.Path),
            Clip(Path.Combine(dir.Path, "missing.dem"), 100));

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner().RunAsync(compilation, Context(engine, capture: null, journal),
                TestContext.Current.CancellationToken));

        Assert.Contains("Invalid compilation:", ex.Message);
        Assert.Contains("Demo file not found", ex.Message);
        Assert.Empty(engine.LoadedDemos);
    }

    /// <summary>
    ///     A valid compilation with no capture session is the "capture never came up" case: it fails
    ///     before any demo is touched.
    /// </summary>
    [Fact]
    public async Task WithoutACaptureSession_ThrowsCaptureNotInitialized()
    {
        using TempDir dir = new();
        string demo = CreateDemo(dir, "a");
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner().RunAsync(Compilation(Settings(dir.Path), Clip(demo, 100)),
                Context(engine, capture: null, journal), TestContext.Current.CancellationToken));

        Assert.Contains("Capture provider not initialized", ex.Message);
        Assert.Empty(engine.LoadedDemos);
    }

    /// <summary>
    ///     A demo that will not load fails the whole run (it is not a clip failure), with the clips
    ///     captured so far returned and a best-effort close of whatever demo was still open.
    /// </summary>
    [Fact]
    public async Task DemoLoadFailure_ClosesTheOpenDemo_AndReturnsTheClipsSoFar()
    {
        using TempDir dir = new();
        string demoA = CreateDemo(dir, "a");
        string demoB = CreateDemo(dir, "b");

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        engine.LoadDemoBehavior = path => path == demoB
            ? throw new InvalidOperationException("demo failed to load")
            : Task.CompletedTask;
        FakeCaptureSession capture = new(journal);

        Cs2CompilationResult result = await Runner(new RecordingFfmpegService()).RunAsync(
            Compilation(Settings(dir.Path), Clip(demoA, 100), Clip(demoB, 100)),
            Context(engine, capture, journal), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("demo failed to load", result.ErrorMessage);
        Assert.Single(result.ClipResults);
        Assert.True(result.ClipResults[0].Success);

        // The group switch closed demo a, and the error path closed again on the way out.
        Assert.Equal(2, engine.CloseDemoCallCount);
        Assert.Equal([demoA], capture.DemosLoaded);
    }

    #region Helpers

    private static Cs2CompilationRunner Runner(IFfmpegService? ffmpegService = null) =>
        new(new ClipCaptureCoordinator(), ffmpegService);

    private static FakeEngineSession Engine(CaptureJournal journal, params string[] capabilities)
    {
        FakeEngineSession engine = new(journal) { AllowDemoControl = true };
        foreach (string capability in capabilities)
        {
            engine.AdvertisedCapabilities.Add(capability);
        }

        return engine;
    }

    private static Cs2CompilationRunContext Context(
        FakeEngineSession engine,
        ICaptureSession? capture,
        CaptureJournal journal) => new()
    {
        Engine = engine,
        Capture = capture,
        ClipStarted = (index, _) =>
        {
            journal.Add($"clipStarted:{index}");
            return Task.CompletedTask;
        },
        ClipCompleted = (index, result) =>
        {
            journal.Add($"clipCompleted:{index}:{result.Success}");
            return Task.CompletedTask;
        }
    };

    private static Cs2Compilation Compilation(Cs2CompilationSettings settings, params Cs2CompilationClip[] clips) =>
        new() { Settings = settings, Clips = clips };

    private static Cs2CompilationSettings Settings(
        string outputDirectory,
        bool concatenate = true,
        bool deleteAfterConcat = false,
        Cs2EncodingSettings? encoding = null) => new()
    {
        OutputDirectory = outputDirectory,
        BaseFileName = "highlight",
        ConcatenateClips = concatenate,
        DeleteClipsAfterConcat = deleteAfterConcat,
        Encoding = encoding
    };

    private static Cs2CompilationClip Clip(
        string demoFilePath,
        int startTick,
        string player = Player,
        Cs2ClipOptions options = Cs2ClipOptions.Default,
        bool? captureAudio = null,
        double playbackSpeed = 1.0,
        bool produceNormalizedCopy = false) => new()
    {
        PlayerSteamId = 76561198000000000,
        MatchChecksum = "checksum",
        DemoFilePath = demoFilePath,
        StartTick = startTick,
        EndTick = startTick + 100,
        PlayerNameToSpectate = player,
        ClipOptions = options,
        CaptureAudio = captureAudio,
        PlaybackSpeed = playbackSpeed,
        ProduceNormalizedCopy = produceNormalizedCopy
    };

    /// <summary>Validation only requires the file to exist; the contents are irrelevant here.</summary>
    private static string CreateDemo(TempDir dir, string name)
    {
        string path = dir.Combine($"{name}.dem");
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    /// <summary>
    ///     The fake backend reports the output path it was handed without writing a file, so the tests
    ///     that care about the files on disk (the single-clip copy, the delete-after-concat pass) write
    ///     them up front.
    /// </summary>
    private static void WriteClipFiles(TempDir dir, int count)
    {
        for (int index = 0; index < count; index++)
        {
            File.WriteAllBytes(dir.Combine($"highlight_{index:D3}.mp4"), [0x00]);
        }
    }

    /// <summary>
    ///     The journal entries this pipeline owns - demo transitions and clip callbacks - without the
    ///     per-clip coordinator/backend chatter the coordinator's own tests cover.
    /// </summary>
    private static string[] DemoAndClipEntries(CaptureJournal journal) =>
    [
        .. journal.Entries.Where(entry =>
            entry.StartsWith("loadDemo:", StringComparison.Ordinal) ||
            entry.StartsWith("session.demoLoaded:", StringComparison.Ordinal) ||
            entry.StartsWith("clipStarted:", StringComparison.Ordinal) ||
            entry.StartsWith("clipCompleted:", StringComparison.Ordinal) ||
            entry == "closeDemo")
    ];

    /// <summary>
    ///     Records what the concatenation stage was asked to do. Succeeds by default; the runner's
    ///     partial-success path is covered by running with no FFmpeg service at all.
    /// </summary>
    private sealed class RecordingFfmpegService : IFfmpegService
    {
        public List<(IReadOnlyList<string> Inputs, string OutputPath, Cs2EncodingSettings? Encoding)> Calls { get; } =
            [];

        public Task<FfmpegConcatResult> ConcatenateAsync(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            Cs2EncodingSettings? encoding = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((inputFiles, outputPath, encoding));
            return Task.FromResult(FfmpegConcatResult.Succeeded(outputPath, TimeSpan.FromSeconds(1)));
        }

        public Task<FfmpegAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The compilation runner must not probe FFmpeg availability.");

        public Task<IImagePipeEncodeSession> StartImagePipeEncodeAsync(
            ImagePipeEncodeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Encoding is a capture-session concern, not the runner's.");

        public Task<FfmpegEncodeResult> MuxAsync(
            string videoPath,
            string? audioPath,
            string outputPath,
            string audioCodec = "aac",
            int? audioBitrateKbps = null,
            double? audioTempo = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Muxing is a capture-session concern, not the runner's.");

        public Task<FfmpegEncodeResult> RemuxRetimedAsync(
            string videoPath,
            double timestampScale,
            string? audioPath,
            string outputPath,
            string audioCodec = "aac",
            int? audioBitrateKbps = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Retiming is a capture-session concern, not the runner's.");

        public Task<FfmpegEncoderProbeResult> ProbeEncodersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The compilation runner must not probe FFmpeg encoders.");
    }

    #endregion
}
