#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     One in-engine clip end to end over a real staging directory and a fake ffmpeg: frames appear,
///     get piped and unlinked, the wav is found and vetted, and the result says what was actually
///     produced. The engine itself is absent by design - everything it would do arrives as files on
///     disk, which is exactly the seam a real CS2 sits behind.
/// </summary>
public class InEngineClipCaptureTests
{
    private const string MovieName = "csvg_000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Runs a clip's whole lifecycle. <paramref name="produce" /> stands in for the engine: it runs
    ///     after the encoder is armed and before the drain is signalled, which is when a real
    ///     <c>startmovie</c> would be writing.
    /// </summary>
    private static async Task<CaptureResult> RunClipAsync(
        InEngineTestHarness harness,
        ClipCaptureContext? context = null,
        Action<InEngineTestHarness>? produce = null)
    {
        IClipCapture clip = await harness.Session.BeginClipAsync(context ?? harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);
        produce?.Invoke(harness);
        await clip.OnPlaybackEndedAsync(Ct);
        return await clip.FinalizeAsync(Ct);
    }

    #region Happy path

    [Fact]
    public async Task AClipWithFramesAndAudio_ProducesTheRequestedOutputFile()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 2.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Path.Combine(harness.OutputDirectory, "clip_000.mp4"), result.OutputFilePath);
        Assert.True(File.Exists(result.OutputFilePath));
        Assert.Equal(120, result.TotalFrames);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    ///     The contract that separates this provider from a screen recorder: the output length is
    ///     frames divided by frame rate, whatever the render took. Two seconds of game time here comes
    ///     out of a capture that ran for a fraction of that.
    /// </summary>
    [Fact]
    public async Task Duration_IsGameTime_NotWallClock()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 2.0);
        });

        Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);

        Assert.NotNull(result.Statistics);
        double wallClock = Assert.IsType<double>(result.Statistics["wallClockSeconds"]);
        Assert.True(wallClock < result.Duration.TotalSeconds,
            $"the fake render should be faster than real time, but took {wallClock} s");
        Assert.Equal(2.0, Assert.IsType<double>(result.Statistics["gameSeconds"]), 6);
        Assert.True(result.Statistics.ContainsKey("captureFps"));
        Assert.True(result.Statistics.ContainsKey("realtimeFactor"));
    }

    [Fact]
    public async Task Duration_FollowsTheClipsFrameRate()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, harness.Clip(frameRate: 30), h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 4.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(4), result.Duration);
    }

    /// <summary>
    ///     Frames have to leave the disk as they are consumed - an offline render produces far more of
    ///     them than fit.
    /// </summary>
    [Fact]
    public async Task Frames_AreUnlinkedOncePiped()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 30);
            h.WriteWav(MovieName, 0.5);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(harness.StagingDirectory));
    }

    [Fact]
    public async Task EveryFrameReachesTheEncoder_InOrder()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 10, frameBytes: 4);
            h.WriteWav(MovieName, 10 / 60.0);
        });

        Assert.True(result.Success, result.ErrorMessage);

        // Each frame's contents are its own number repeated, so the piped stream spells out the order.
        FakeImagePipeEncodeSession encode = Assert.Single(harness.Ffmpeg.EncodeSessions);
        byte[] piped = encode.WrittenBytes;
        Assert.Equal(40, piped.Length);
        for (int frame = 0; frame < 10; frame++)
        {
            Assert.Equal((byte)frame, piped[frame * 4]);
        }
    }

    [Fact]
    public async Task TheEncoderIsAskedForTheClipsFrameRateAndFormat()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        await RunClipAsync(harness, harness.Clip(frameRate: 120), h =>
        {
            h.WriteFrames(MovieName, 12);
            h.WriteWav(MovieName, 0.1);
        });

        ImagePipeEncodeRequest request = Assert.Single(harness.Ffmpeg.EncodeRequests);
        Assert.Equal(120, request.FrameRate);
        Assert.Equal(ImagePipeSourceFormat.Mjpeg, request.SourceFormat);
        Assert.Null(request.FrameSizeBytes);
    }

    /// <summary>
    ///     The intermediate is the delivered video for a single clip, so its quality settings are the
    ///     clip's quality settings. Both have to leave the configuration and reach the encoder, and the
    ///     preset has to arrive unaltered - the names belong to ffmpeg.
    /// </summary>
    [Fact]
    public async Task TheConfiguredEncoderPresetAndQuality_ReachTheEncoder()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                IntermediateEncoderPreset = "slow",
                IntermediateQuality = 14
            });

        await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 12);
            h.WriteWav(MovieName, 0.2);
        });

        ImagePipeEncodeRequest request = Assert.Single(harness.Ffmpeg.EncodeRequests);
        Assert.Equal("slow", request.EncoderPreset);
        Assert.Equal(14, request.Quality);
    }

    /// <summary>
    ///     Unset means the encoder's own default, not "no preset": the request carries the null through
    ///     and the resolution happens where the encoder is known.
    /// </summary>
    [Fact]
    public async Task NoConfiguredPreset_LeavesTheEncoderToItsDefault()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 12);
            h.WriteWav(MovieName, 0.2);
        });

        Assert.Null(Assert.Single(harness.Ffmpeg.EncodeRequests).EncoderPreset);

        // What actually ran is what the statistics report, so the console line names the resolved
        // preset rather than the empty setting.
        Assert.NotNull(result.Statistics);
        Assert.Equal("libx264", result.Statistics["encoder"]);
        Assert.Equal(FfmpegService.DefaultX264Preset, result.Statistics["encoderPreset"]);
    }

    /// <summary>
    ///     Targa is the format that cannot be split without a record length, so the length has to reach
    ///     ffmpeg.
    /// </summary>
    [Fact]
    public async Task TargaCapture_PassesTheRecordLengthToTheEncoder()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(TgaConfiguration(64));

        await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 10, frameBytes: 64);
            h.WriteWav(MovieName, 10 / 60.0);
        });

        ImagePipeEncodeRequest request = Assert.Single(harness.Ffmpeg.EncodeRequests);
        Assert.Equal(ImagePipeSourceFormat.Targa, request.SourceFormat);
        Assert.Equal(64, request.FrameSizeBytes);
    }

    #endregion

    #region Audio

    [Fact]
    public async Task TheWavIsMuxedOntoTheVideo()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        string wavPath = null!;

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 60);
            wavPath = h.WriteWav(MovieName, 1.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
        FakeFfmpegService.MuxCall mux = Assert.Single(harness.Ffmpeg.MuxCalls);
        Assert.Equal(wavPath, mux.AudioPath);
        Assert.Equal("aac", mux.AudioCodec);
    }

    /// <summary>
    ///     The wav is expected in a timestamped subdirectory, which is why the search descends.
    /// </summary>
    [Fact]
    public async Task TheWavIsFoundInATimestampedSubdirectory()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 60);
            h.WriteWav(MovieName, 1.0, "2026-07-30_120000");
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(Assert.Single(harness.Ffmpeg.MuxCalls).AudioPath);
    }

    /// <summary>
    ///     The engine writes a wav whether the clip wanted one or not, so an audio-disabled clip still
    ///     has to find it - to delete it. Nothing else ever would.
    /// </summary>
    [Fact]
    public async Task AnAudioDisabledClip_DeletesTheWavTheEngineWroteAnyway()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        string wavPath = null!;

        CaptureResult result = await RunClipAsync(harness, harness.Clip(captureAudio: false), h =>
        {
            h.WriteFrames(MovieName, 60);
            wavPath = h.WriteWav(MovieName, 1.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(wavPath));
        Assert.Null(Assert.Single(harness.Ffmpeg.MuxCalls).AudioPath);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    ///     The default: audio is confirmed working on real CS2, so a clip that asked for audio and
    ///     has none does not meet the requested spec. The failure names the audio settings and the
    ///     behavior knob.
    /// </summary>
    [Fact]
    public async Task MissingAudio_FailsTheClipByDefault()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h => h.WriteFrames(MovieName, 60));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Capture:InEngine:AudioDirectory", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("MissingAudioBehavior", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAudio_CanBeDowngradedToAWarning()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                FrameConsumerPollMs = 20,
                MissingAudioBehavior = MissingAudioBehavior.WarnAndContinue
            });

        CaptureResult result = await RunClipAsync(harness, produce: h => h.WriteFrames(MovieName, 60));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.Warnings, warning => warning.Contains("asked for audio", StringComparison.Ordinal));
        Assert.Null(Assert.Single(harness.Ffmpeg.MuxCalls).AudioPath);
    }

    [Fact]
    public async Task MissingAudio_IsNotEvenLookedForWhenTheClipDoesNotWantIt()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, harness.Clip(captureAudio: false),
            h => h.WriteFrames(MovieName, 60));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    ///     The Valve-regression guard. Audio at wall-clock length against movie-length video plays fine
    ///     and drifts further out of sync as it runs, which is why it has to be said out loud.
    /// </summary>
    [Fact]
    public async Task AWavOfTheWrongLength_WarnsNamingTheRegressionClass()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 8.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("2017 and 2020", warning, StringComparison.Ordinal);
        Assert.Contains("No frames were dropped", warning, StringComparison.Ordinal);
        Assert.Contains("8.000 s", warning, StringComparison.Ordinal);
        Assert.Contains("2.000 s", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWavShorterThanTheVideo_AlsoWarns()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 0.5);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.Warnings,
            warning => warning.Contains("Audio and video lengths disagree", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Dropped frames make the video genuinely short of the game time it covers, so the same
    ///     mismatch has a different cause and a different fix. A message that named the engine
    ///     regression here would send the reader after the wrong defect.
    /// </summary>
    [Fact]
    public async Task AWavMismatchWithDroppedFrames_BlamesTheDroppedFrames_NotTheEngine()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 30);
            h.WriteFrames(MovieName, 30, startNumber: 90);
            h.WriteWav(MovieName, 2.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(60, result.TotalFrames);
        Assert.Equal(60, result.DroppedFrames);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("60 frame(s) were dropped", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("2017 and 2020", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWavWithinToleranceProducesNoWarning()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 2.01);
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    ///     A wav still carrying its placeholder sizes has no length to check. Muxing it unchecked and
    ///     saying so beats either trusting the placeholder or throwing the audio away.
    /// </summary>
    [Fact]
    public async Task AWavTheEngineNeverFinalized_IsMuxedWithAWarningRatherThanChecked()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                FrameConsumerPollMs = 20,
                AudioFinalizationTimeoutMs = 100,
                AudioFinalizationPollMs = 10
            });

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 2.0, finalized: false);
        });

        Assert.True(result.Success, result.ErrorMessage);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("had not finished writing", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("2017 and 2020", warning, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(harness.Ffmpeg.MuxCalls).AudioPath);
    }

    /// <summary>
    ///     What is pinned here is the locator's behaviour: a file it cannot parse is reported rather
    ///     than silently dropped or trusted. The mux is a fake, so this says nothing about what real
    ///     ffmpeg does with a text file - it would fail, and the clip would fail with it.
    /// </summary>
    [Fact]
    public async Task AFileThatIsNotAWav_IsReportedRatherThanSwallowed()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                FrameConsumerPollMs = 20,
                AudioFinalizationTimeoutMs = 100,
                AudioFinalizationPollMs = 10
            });

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            File.WriteAllText(Path.Combine(h.AudioDirectory, MovieName + ".wav"), "not a wav");
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.Warnings,
            warning => warning.Contains("could not be read as a wav", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Whether the engine writes a wav at all in jpg mode is unconfirmed, so a clip that got none
    ///     must not quietly inherit the previous run's: same movie name, plausible length, wrong take,
    ///     and the duration guard would have nothing to catch. Only a wav written since the clip armed
    ///     counts as this clip's.
    /// </summary>
    [Fact]
    public async Task AWavLeftBehindByAnEarlierRun_IsNotMuxedOntoThisClip()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                FrameConsumerPollMs = 20,
                MissingAudioBehavior = MissingAudioBehavior.WarnAndContinue
            });

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);

        // Written after the sweep, so only the timestamp can reject it - which is the case that
        // matters, because a file the sweep could not delete looks exactly like this.
        string stale = harness.WriteWav(MovieName, 2.0);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        harness.WriteFrames(MovieName, 120);
        await clip.OnPlaybackEndedAsync(Ct);
        CaptureResult result = await clip.FinalizeAsync(Ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(Assert.Single(harness.Ffmpeg.MuxCalls).AudioPath);
        Assert.Contains(result.Warnings, warning => warning.Contains("asked for audio", StringComparison.Ordinal));
        Assert.True(File.Exists(stale), "an earlier run's audio is rejected, not deleted");
    }

    /// <summary>
    ///     The other half of the same guard: a wav already sitting under this clip's movie name when it
    ///     arms is swept, so it cannot be picked up at all.
    /// </summary>
    [Fact]
    public async Task AWavAlreadyPresentWhenTheClipArms_IsSwept()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        string stale = harness.WriteWav(MovieName, 9.0, "2026-07-29_100000");
        Assert.True(File.Exists(stale));

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);

        Assert.False(File.Exists(stale));

        await clip.AbortAsync(CaptureAbortReason.Cancelled, Ct);
    }

    #endregion

    #region Failure paths

    /// <summary>
    ///     The load-bearing guard. A record length that does not match the frames means either the
    ///     configured value is wrong or the frames are compressed, and piping them anyway would produce
    ///     a video of noise that decodes without complaint. The failure carries the observed size so
    ///     the fix is a copy-paste.
    /// </summary>
    [Fact]
    public async Task TargaFramesThatDisagreeWithTheRecordLength_FailTheClipLoudly()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(TgaConfiguration(64));

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrame(MovieName, 0, 64);
            h.WriteFrame(MovieName, 1, 37);
            h.WriteFrame(MovieName, 2, 64);
        });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("37 bytes", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("record length of 64 bytes", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargaFramesThatAllMatchTheRecordLength_Succeed()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(TgaConfiguration(64));

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 60, frameBytes: 64);
            h.WriteWav(MovieName, 1.0);
        });

        Assert.True(result.Success, result.ErrorMessage);
    }

    /// <summary>
    ///     Nothing appearing in staging is the shape every wrong guess about where the engine writes
    ///     takes, so the failure points at the settings that decide it.
    /// </summary>
    [Fact]
    public async Task NoFrames_FailsTheClipNamingTheUnverifiedSettings()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult result = await RunClipAsync(harness);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("wrote no frames", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("csvg_000????????.jpg", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Capture:InEngine:EngineOutput", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedMux_FailsTheClip()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            configureFfmpeg: ffmpeg => ffmpeg.MuxFailure = "the container refused the stream");

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 60);
            h.WriteWav(MovieName, 1.0);
        });

        Assert.False(result.Success);
        Assert.Contains("the container refused the stream", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEncoderThatNeverStarted_FailsTheClip()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            configureFfmpeg: ffmpeg => ffmpeg.EncodeStartFailure = "ffmpeg is not on PATH");

        CaptureResult result = await RunClipAsync(harness, produce: h => h.WriteFrames(MovieName, 60));

        Assert.False(result.Success);
        Assert.Contains("ffmpeg is not on PATH", result.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An encoder that dies mid-clip vetoes the clip, so the coordinator's playback wait unblocks in
    ///     milliseconds rather than burning the whole timeout writing frames into a dead pipe.
    /// </summary>
    [Fact]
    public async Task AnEncoderThatDiesMidClip_VetoesTheClip()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        ClipCaptureContext context = harness.Clip();
        IClipCapture clip = await harness.Session.BeginClipAsync(context, Ct);
        await clip.OnPlaybackStartingAsync(Ct);

        Assert.NotNull(harness.Ffmpeg.LastEncode);
        harness.Ffmpeg.LastEncode.FailNow("no space left on device");

        await WaitForAsync(() => context.FaultToken.Token.IsCancellationRequested);

        Assert.NotNull(context.FaultToken.Fault);
        Assert.Contains("no space left on device", context.FaultToken.Fault.Message, StringComparison.Ordinal);

        await clip.AbortAsync(CaptureAbortReason.BackendFault, Ct);
    }

    /// <summary>
    ///     A record-length mismatch has to veto the clip while it is still recording. The encoder cannot
    ///     report it - the consumer closes the pipe on its way out, so ffmpeg sees a clean end of stream
    ///     and exits happily - so without a watch on the sequence itself the render would run to the end
    ///     tick before anyone found out.
    /// </summary>
    [Fact]
    public async Task AFrameSequenceThatFailsMidPlayback_VetoesTheClip()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(TgaConfiguration(64));

        ClipCaptureContext context = harness.Clip();
        IClipCapture clip = await harness.Session.BeginClipAsync(context, Ct);
        await clip.OnPlaybackStartingAsync(Ct);

        harness.WriteFrame(MovieName, 0, 64);
        harness.WriteFrame(MovieName, 1, 37);
        harness.WriteFrame(MovieName, 2, 64);

        await WaitForAsync(() => context.FaultToken.Token.IsCancellationRequested);

        Assert.NotNull(context.FaultToken.Fault);
        Assert.Contains("37 bytes", context.FaultToken.Fault.Message, StringComparison.Ordinal);

        await clip.AbortAsync(CaptureAbortReason.BackendFault, Ct);
    }

    /// <summary>
    ///     A normal clip must not veto itself on the way out: the drain completing is how a successful
    ///     capture ends, and a veto there would turn every success into a reported fault.
    /// </summary>
    [Fact]
    public async Task ASuccessfulClipNeverVetoesItself()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        ClipCaptureContext context = harness.Clip();
        IClipCapture clip = await harness.Session.BeginClipAsync(context, Ct);
        await clip.OnPlaybackStartingAsync(Ct);
        harness.WriteFrames(MovieName, 60);
        harness.WriteWav(MovieName, 1.0);
        await clip.OnPlaybackEndedAsync(Ct);

        CaptureResult result = await clip.FinalizeAsync(Ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(context.FaultToken.Fault);
        Assert.False(context.FaultToken.Token.IsCancellationRequested);
    }

    /// <summary>
    ///     An MP4 cannot carry a utvideo stream, and the audio mux copies the video rather than
    ///     re-encoding it. Finding that out after the render would cost the whole render.
    /// </summary>
    [Fact]
    public async Task ALosslessIntermediateIntoAnMp4_FailsBeforeTheRenderRatherThanAfterIt()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                FrameConsumerPollMs = 20,
                IntermediateEncoder = ImagePipeEncoder.Utvideo
            });

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);

        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(() => clip.OnPlaybackStartingAsync(Ct));

        Assert.Contains("Utvideo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mp4", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Ffmpeg.EncodeRequests);
    }

    [Fact]
    public async Task FinalizingAClipThatNeverStarted_FailsRatherThanThrows()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);

        CaptureResult result = await clip.FinalizeAsync(Ct);

        Assert.False(result.Success);
        Assert.Contains("without ever starting", result.ErrorMessage!, StringComparison.Ordinal);
    }

    #endregion

    #region Abort

    [Fact]
    public async Task Abort_KillsTheEncoderAndSweepsStaging()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);
        harness.WriteFrames(MovieName, 5);

        await clip.AbortAsync(CaptureAbortReason.PlaybackFailed, Ct);

        // Closing the pipe is what a real ffmpeg sees first, so whether it exits 0 on the way out is
        // not the question - whether the session was released is.
        Assert.NotNull(harness.Ffmpeg.LastEncode);
        Assert.True(harness.Ffmpeg.LastEncode.IsDisposed);
        Assert.True(harness.Ffmpeg.LastEncode.Completion.IsCompleted);
        Assert.Empty(Directory.GetFiles(harness.StagingDirectory, MovieName + "*"));
    }

    [Fact]
    public async Task Abort_KeepsTheEvidenceWhenAskedTo()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration
            {
                FrameFormat = InEngineFrameFormat.Jpg,
                FrameConsumerPollMs = 20,
                KeepStagingOnFailure = true
            });

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);
        harness.WriteFrame(MovieName, 4000);

        await clip.AbortAsync(CaptureAbortReason.PlaybackFailed, Ct);

        Assert.NotEmpty(Directory.GetFiles(harness.StagingDirectory, MovieName + "*"));
    }

    [Fact]
    public async Task Abort_IsIdempotent()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);

        await clip.AbortAsync(CaptureAbortReason.Cancelled, Ct);
        await clip.AbortAsync(CaptureAbortReason.Cancelled, Ct);
        await clip.DisposeAsync();
    }

    /// <summary>
    ///     A clip dropped without a result must not leave ffmpeg running.
    /// </summary>
    [Fact]
    public async Task DisposingAnUnfinishedClip_Aborts()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        await clip.OnPlaybackStartingAsync(Ct);

        await clip.DisposeAsync();

        Assert.NotNull(harness.Ffmpeg.LastEncode);
        Assert.True(harness.Ffmpeg.LastEncode.IsDisposed);
    }

    #endregion

    #region Session

    [Fact]
    public async Task InitializeAsync_SweepsWhatAPreviousRunLeftBehind()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        File.WriteAllText(Path.Combine(harness.StagingDirectory, "csvg_007_0001.jpg"), "stale");
        File.WriteAllText(Path.Combine(harness.StagingDirectory, "somebody_elses.txt"), "keep me");

        await harness.Session.ShutdownAsync(Ct);
        await harness.Session.InitializeAsync(Ct);

        Assert.False(File.Exists(Path.Combine(harness.StagingDirectory, "csvg_007_0001.jpg")));
        Assert.True(File.Exists(Path.Combine(harness.StagingDirectory, "somebody_elses.txt")));
    }

    /// <summary>
    ///     The engine records one movie at a time, so a second concurrent clip would have its frames
    ///     folded into the first one's sequence.
    /// </summary>
    [Fact]
    public async Task ASecondConcurrentClip_IsRejected()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        await harness.Session.BeginClipAsync(harness.Clip(), Ct);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Session.BeginClipAsync(harness.Clip(clipIndex: 1), Ct));

        Assert.Contains("one movie at a time", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClipAfterTheFirstOneFinished_IsAccepted()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureResult first = await RunClipAsync(harness, harness.Clip(clipIndex: 0), h =>
        {
            h.WriteFrames("csvg_000", 60);
            h.WriteWav("csvg_000", 1.0);
        });
        Assert.True(first.Success, first.ErrorMessage);

        CaptureResult second = await RunClipAsync(harness, harness.Clip(clipIndex: 1), h =>
        {
            h.WriteFrames("csvg_001", 60);
            h.WriteWav("csvg_001", 1.0);
        });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(Path.Combine(harness.OutputDirectory, "clip_001.mp4"), second.OutputFilePath);
    }

    [Fact]
    public async Task AnUninitializedSession_RefusesToStartAClip()
    {
        FakeFfmpegService ffmpeg = new();
        using TempDir staging = new("csvg-inengine-uninit-");
        InEngineCaptureSession session = new(
            new InEngineCaptureConfiguration(),
            new CaptureSessionContext { Engine = new FakeEngineConsole() },
            ffmpeg,
            () => staging.Path,
            () => staging.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.BeginClipAsync(new ClipCaptureContext
            {
                StartTick = 100,
                EndTick = 200,
                Metadata = new CaptureMetadata { OutputFileName = "clip", OutputDirectory = staging.Path }
            }, Ct));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task AnIncoherentConfiguration_FailsBringUpNamingTheProblem()
    {
        FakeFfmpegService ffmpeg = new();
        using TempDir staging = new("csvg-inengine-badconfig-");
        InEngineCaptureSession session = new(
            new InEngineCaptureConfiguration { FrameConsumerPollMs = 0 },
            new CaptureSessionContext { Engine = new FakeEngineConsole() },
            ffmpeg,
            () => staging.Path,
            () => staging.Path);

        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync(Ct));

        Assert.Contains("FrameConsumerPollMs", ex.Message, StringComparison.Ordinal);
        await session.DisposeAsync();
    }

    /// <summary>
    ///     The default-format contract for the record length: a Targa clip computes it from the
    ///     clip's resolution when the configuration leaves it null, and a clip with neither fails at
    ///     capture start naming both remedies.
    /// </summary>
    [Fact]
    public async Task TargaRecordLength_IsDerivedFromTheClipResolution()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration { FrameConsumerPollMs = 20 });

        ClipCaptureContext clip = harness.Clip();
        clip = new ClipCaptureContext
        {
            StartTick = clip.StartTick,
            EndTick = clip.EndTick,
            ClipIndex = clip.ClipIndex,
            Metadata = clip.Metadata with { Width = 1920, Height = 1080 }
        };

        IClipCapture capture = await harness.Session.BeginClipAsync(clip, Ct);
        await capture.OnPlaybackStartingAsync(Ct);

        ImagePipeEncodeRequest request = Assert.Single(harness.Ffmpeg.EncodeRequests);
        Assert.Equal(8294418, request.FrameSizeBytes);
        await capture.AbortAsync(CaptureAbortReason.SessionShutdown, Ct);
    }

    [Fact]
    public async Task TargaClipWithNoResolutionAndNoConfiguredLength_FailsNamingBothRemedies()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration { FrameConsumerPollMs = 20 });

        IClipCapture capture = await harness.Session.BeginClipAsync(harness.Clip(), Ct);
        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(() => capture.OnPlaybackStartingAsync(Ct));

        Assert.Contains("TgaFrameSizeBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("width and height", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Bring-up runs before CS2 launches, so a staging directory that cannot be worked out has to
    ///     be reported rather than thrown from somewhere deep in the resolution.
    /// </summary>
    [Fact]
    public async Task AStagingDirectoryThatCannotBeResolved_FailsBringUpWithAnActionableMessage()
    {
        FakeFfmpegService ffmpeg = new();
        using TempDir audio = new("csvg-inengine-audio-only-");
        InEngineCaptureSession session = new(
            new InEngineCaptureConfiguration(),
            new CaptureSessionContext { Engine = new FakeEngineConsole() },
            ffmpeg,
            () => throw new InvalidOperationException("no CS2 installation was found"),
            () => audio.Path);

        InvalidOperationException ex =
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync(Ct));

        Assert.Contains("Capture:InEngine:StagingDirectory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no CS2 installation was found", ex.Message, StringComparison.Ordinal);
        await session.DisposeAsync();
    }

    /// <summary>
    ///     Bringing an already-initialized session up again is a no-op, which means it does not sweep
    ///     the staging directory a clip may be recording into.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        string frame = Path.Combine(harness.StagingDirectory, "csvg_000_0001.jpg");
        File.WriteAllText(frame, "a frame from the clip in flight");

        await harness.Session.InitializeAsync(Ct);

        Assert.True(File.Exists(frame));
    }

    #endregion

    #region Progress

    [Fact]
    public async Task ProgressReportsGameTimeAndTheStagingBacklog()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        List<CaptureProgressEventArgs> progress = [];
        harness.Session.ProgressUpdated += (_, args) => progress.Add(args);

        CaptureResult result = await RunClipAsync(harness, produce: h =>
        {
            h.WriteFrames(MovieName, 120);
            h.WriteWav(MovieName, 2.0);
        });

        Assert.True(result.Success, result.ErrorMessage);

        // The pump samples once a second and this clip finishes faster than that, so the only thing
        // worth asserting is the shape any reading would have had.
        foreach (CaptureProgressEventArgs reading in progress)
        {
            Assert.NotNull(reading.AdditionalMetrics);
            Assert.True(reading.AdditionalMetrics.ContainsKey("stagingBacklog"));
            Assert.True(reading.AdditionalMetrics.ContainsKey("expectedFrames"));
        }
    }

    #endregion

    #region Helpers

    private static InEngineCaptureConfiguration TgaConfiguration(int frameSizeBytes) => new()
    {
        FrameFormat = InEngineFrameFormat.Tga,
        FrameConsumerPollMs = 20,
        EngineOutput = new InEngineEngineOutput { TgaFrameSizeBytes = frameSizeBytes }
    };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        Assert.Fail($"the condition was still false after {timeoutMs} ms");
    }

    #endregion
}
