#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Offline slow motion is a rate split, not a timescale: the engine renders at
///     FrameRate / PlaybackSpeed, ffmpeg encodes at FrameRate, and the game-time audio is
///     stretched by the speed factor. demo_timescale is measurably inert for offline capture
///     (matrix runs R7/R8, 2026-07-31) and appears nowhere here.
/// </summary>
public class PlaybackSpeedTests
{
    private static ClipCaptureContext Clip(
        InEngineTestHarness harness, double speed, int fps = 60, bool normalizedCopy = false) =>
        new()
        {
            StartTick = 49000,
            EndTick = 51000,
            ClipIndex = 0,
            Metadata = new CaptureMetadata
            {
                OutputFileName = "clip_000",
                OutputDirectory = harness.OutputDirectory,
                FrameRate = fps,
                PlaybackSpeed = speed,
                CaptureAudio = true,
                ProduceNormalizedCopy = normalizedCopy
            }
        };

    private static async Task<CaptureResult> RunClipAsync(
        InEngineTestHarness harness, ClipCaptureContext context, double wavSeconds)
    {
        IClipCapture clip = await harness.Session.BeginClipAsync(context, TestContext.Current.CancellationToken);
        await clip.OnPlaybackStartingAsync(TestContext.Current.CancellationToken);
        harness.WriteFrames("csvg_000", 120);
        harness.WriteWav("csvg_000", wavSeconds);
        await clip.OnPlaybackEndedAsync(TestContext.Current.CancellationToken);
        return await clip.FinalizeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HalfSpeed_DoublesTheCaptureRate_ButNotTheEncodeRate()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(Clip(harness, 0.5),
            TestContext.Current.CancellationToken);

        // The engine renders 120 real frames per game second; the encode side still plays them at
        // 60, which is precisely what makes the output 2x slow motion.
        Assert.Equal("host_framerate 120", clip.TickPlan.SetupCommands[0]);
    }

    [Fact]
    public async Task QuarterSpeed_QuadruplesTheCaptureRate()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(Clip(harness, 0.25),
            TestContext.Current.CancellationToken);

        Assert.Equal("host_framerate 240", clip.TickPlan.SetupCommands[0]);
    }

    [Fact]
    public async Task RealTime_IsUnchanged()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(Clip(harness, 1.0),
            TestContext.Current.CancellationToken);

        Assert.Equal("host_framerate 60", clip.TickPlan.SetupCommands[0]);
    }

    [Fact]
    public async Task ANonPositiveSpeed_IsRejectedUpFront()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Session.BeginClipAsync(Clip(harness, 0), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Session.BeginClipAsync(Clip(harness, -1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheAudioTempoChain_MatchesTheSpeedFactor()
    {
        Assert.Null(FfmpegService.AudioTempoFilter(null));
        Assert.Null(FfmpegService.AudioTempoFilter(1.0));
        Assert.Equal("atempo=0.5", FfmpegService.AudioTempoFilter(0.5));
        Assert.Equal("atempo=0.5,atempo=0.5", FfmpegService.AudioTempoFilter(0.25));
        Assert.Equal("atempo=0.5,atempo=0.6", FfmpegService.AudioTempoFilter(0.3));
        Assert.Equal("atempo=2", FfmpegService.AudioTempoFilter(2.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegService.AudioTempoFilter(0));
    }

    /// <summary>
    ///     The normalized copy is produced while the pristine sources still exist: the intermediate
    ///     video retimed by the speed factor, and the original wav with no tempo processing - the
    ///     G-block analysis measured what every avoided atempo pass is worth.
    /// </summary>
    [Fact]
    public async Task ANormalizedCopy_RetimesTheIntermediate_AndUsesTheOriginalWav()
    {
        // Speed 2 at 60 fps: 120 frames = 4 s of game time (30 fps capture), 2 s of output.
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        CaptureResult result = await RunClipAsync(
            harness, Clip(harness, 2, normalizedCopy: true), wavSeconds: 4.0);

        Assert.True(result.Success, result.ErrorMessage);

        FakeFfmpegService.RetimeCall retime = Assert.Single(harness.Ffmpeg.RetimeCalls);
        Assert.Equal(2, retime.TimestampScale);
        Assert.EndsWith(".wav", retime.AudioPath);
        Assert.EndsWith("clip_000.normalized.mp4", retime.OutputPath);

        // The speed clip's own audio got the one mux-time tempo pass; the copy's got none.
        Assert.Equal(2, Assert.Single(harness.Ffmpeg.MuxCalls).AudioTempo);

        string normalized = Assert.Single(result.AdditionalOutputs);
        Assert.True(File.Exists(normalized));
    }

    [Fact]
    public async Task ANormalizedCopy_IsSkippedAtRealTime()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        CaptureResult result = await RunClipAsync(
            harness, Clip(harness, 1, normalizedCopy: true), wavSeconds: 2.0);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(harness.Ffmpeg.RetimeCalls);
        Assert.Empty(result.AdditionalOutputs);
    }

    [Fact]
    public async Task AFailedNormalizedCopy_WarnsWithoutFailingTheClip()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            configureFfmpeg: f => f.RetimeFailure = "no retime today");
        CaptureResult result = await RunClipAsync(
            harness, Clip(harness, 2, normalizedCopy: true), wavSeconds: 4.0);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.AdditionalOutputs);
        Assert.Contains(result.Warnings, w => w.Contains("normalized real-time copy"));
    }

    /// <summary>
    ///     KeepEngineAudio moves the pristine game-time wav next to the finished clip instead of
    ///     deleting it - the zero-passes source for any external post-processing.
    /// </summary>
    [Fact]
    public async Task KeepEngineAudio_MovesTheWavNextToTheOutput()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameConsumerPollMs = 20,
                FrameFormat = InEngineFrameFormat.Jpg,
                KeepEngineAudio = true
            });
        CaptureResult result = await RunClipAsync(harness, Clip(harness, 1), wavSeconds: 2.0);

        Assert.True(result.Success, result.ErrorMessage);
        string kept = Assert.Single(result.AdditionalOutputs);
        Assert.Equal(Path.Combine(harness.OutputDirectory, "clip_000.engine-audio.wav"), kept);
        Assert.True(File.Exists(kept));
        Assert.Empty(Directory.GetFiles(harness.AudioDirectory, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public void TheMuxArguments_CarryTheTempoFilter_OnlyWhenStretching()
    {
        List<string> stretched = [.. FfmpegService.BuildMuxArguments(
            "v.mkv", "a.wav", "out.mp4", "aac", null, 0.5)];
        int af = stretched.IndexOf("-af");
        Assert.True(af >= 0);
        Assert.Equal("atempo=0.5", stretched[af + 1]);

        List<string> plain = [.. FfmpegService.BuildMuxArguments(
            "v.mkv", "a.wav", "out.mp4", "aac", null)];
        Assert.DoesNotContain("-af", plain);
    }

    /// <summary>
    ///     The retimed remux is lossless by construction: timestamps scaled before the input they
    ///     apply to, video stream copied, and no tempo filter anywhere near the audio.
    /// </summary>
    [Fact]
    public void TheRetimedRemuxArguments_ScaleTimestampsAndCopyTheVideo()
    {
        List<string> arguments = [.. FfmpegService.BuildRetimedRemuxArguments(
            "v.mkv", 2, "a.wav", "out.mp4", "aac", null)];

        int its = arguments.IndexOf("-itsscale");
        int input = arguments.IndexOf("-i");
        Assert.True(its >= 0 && its < input, "-itsscale must precede the -i it applies to");
        Assert.Equal("2", arguments[its + 1]);
        Assert.Equal("copy", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-af", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("atempo"));

        List<string> videoOnly = [.. FfmpegService.BuildRetimedRemuxArguments(
            "v.mkv", 0.5, null, "out.mp4", "aac", null)];
        Assert.Contains("-an", videoOnly);
        Assert.Equal("0.5", videoOnly[videoOnly.IndexOf("-itsscale") + 1]);
    }
}
