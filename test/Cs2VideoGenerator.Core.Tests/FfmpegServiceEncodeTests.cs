#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The offline-capture additions to <see cref="FfmpegService" />: image pipe, mux, encoder probe.
///     Layered the same way <see cref="FfmpegServiceTests" /> is. Argument construction asserts on the
///     argv the pure builders produce with nothing spawned, which is the only place NVENC can be
///     covered - no machine running these is guaranteed to have it. Validation covers the never-throw
///     contract for inputs that never reach ffmpeg. The rest need ffmpeg on PATH: frames really piped
///     through and probed back, and a real mux whose stream durations have to line up.
/// </summary>
public class FfmpegServiceEncodeTests : IDisposable
{
    private readonly TempDir _tempDir = new("csvg-ffmpeg-encode-tests-");

    public void Dispose()
    {
        _tempDir.Dispose();
        GC.SuppressFinalize(this);
    }

    private static FfmpegService CreateService(FfmpegOptions? options = null) =>
        new(NullLogger<FfmpegService>.Instance, Options.Create(options ?? new FfmpegOptions()));

    #region Image pipe argument construction (no ffmpeg needed)

    private static ImagePipeEncodeRequest Request(
        ImagePipeSourceFormat format = ImagePipeSourceFormat.Targa,
        double frameRate = 60,
        ImagePipeEncoder encoder = ImagePipeEncoder.X264,
        int? quality = null,
        int? frameSizeBytes = null,
        string? pixelFormat = null,
        string? encoderPreset = null,
        IReadOnlyList<string>? extraOutputArgs = null) =>
        new()
        {
            OutputPath = "/out/clip.mp4",
            FrameRate = frameRate,
            SourceFormat = format,
            Encoder = encoder,
            Quality = quality,
            FrameSizeBytes = frameSizeBytes,
            PixelFormat = pixelFormat,
            EncoderPreset = encoderPreset,
            ExtraOutputArgs = extraOutputArgs
        };

    private static string Rendered(ImagePipeEncodeRequest request, ImagePipeEncoder resolved) =>
        string.Join(' ', FfmpegService.BuildImagePipeArguments(request, resolved));

    [Fact]
    public void BuildImagePipeArguments_EmitsImage2PipeInputShape()
    {
        string args = Rendered(Request(), ImagePipeEncoder.X264);

        Assert.Contains("-f image2pipe", args);
        Assert.Contains("-framerate 60", args);
        Assert.Contains("-vcodec targa", args);
        Assert.Contains("-i pipe:0", args);
        Assert.Contains("-vf setsar=1", args);

        // The input description has to precede the input, and the filter has to follow it.
        Assert.True(args.IndexOf("-f image2pipe", StringComparison.Ordinal) <
                    args.IndexOf("-i pipe:0", StringComparison.Ordinal),
            $"input options should precede the input: {args}");
        Assert.True(args.IndexOf("-i pipe:0", StringComparison.Ordinal) <
                    args.IndexOf("-vf setsar=1", StringComparison.Ordinal),
            $"the filter should follow the input: {args}");
    }

    [Theory]
    [InlineData(ImagePipeSourceFormat.Targa, "targa")]
    [InlineData(ImagePipeSourceFormat.Mjpeg, "mjpeg")]
    [InlineData(ImagePipeSourceFormat.Png, "png")]
    public void BuildImagePipeArguments_NamesTheDecoderForTheSourceFormat(
        ImagePipeSourceFormat format,
        string expectedDecoder)
    {
        string args = Rendered(Request(format), ImagePipeEncoder.X264);

        Assert.Contains($"-vcodec {expectedDecoder}", args);
    }

    [Fact]
    public void BuildImagePipeArguments_FrameSizeGiven_EmitsRecordLengthBeforeTheInput()
    {
        string args = Rendered(Request(frameSizeBytes: 57644), ImagePipeEncoder.X264);

        Assert.Contains("-frame_size 57644", args);
        Assert.True(args.IndexOf("-frame_size", StringComparison.Ordinal) <
                    args.IndexOf("-i pipe:0", StringComparison.Ordinal),
            $"the record length is an input option: {args}");
    }

    [Fact]
    public void BuildImagePipeArguments_NoFrameSize_OmitsRecordLength()
    {
        string args = Rendered(Request(ImagePipeSourceFormat.Mjpeg), ImagePipeEncoder.X264);

        Assert.DoesNotContain("-frame_size", args);
    }

    /// <summary>
    ///     The default is deliberately not <c>ultrafast</c>: this file is the delivered video for a
    ///     single-clip capture, and ultrafast drops the analysis CRF assumes when it picks quantizers.
    ///     It is deliberately not slower than <c>veryfast</c> either - software H.264 is where an
    ///     offline render actually waits.
    /// </summary>
    [Fact]
    public void BuildImagePipeArguments_X264_UsesVeryfastAndCrf()
    {
        string args = Rendered(Request(), ImagePipeEncoder.X264);

        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-preset veryfast", args);
        Assert.Equal("veryfast", FfmpegService.DefaultX264Preset);
        Assert.Contains($"-crf {FfmpegService.DefaultX264Quality}", args);
        Assert.Contains("-pix_fmt yuv420p", args);
        Assert.DoesNotContain("-cq", args);
        Assert.DoesNotContain("-tune", args);
    }

    /// <summary>
    ///     NVENC gets the slow end of its scale by default, plus the quality-first tuning: the render
    ///     rather than the encoder is what dominates wall clock on hardware H.264, and frames backing up
    ///     in staging is the buffer that makes spending encoder time free of any other cost.
    /// </summary>
    [Fact]
    public void BuildImagePipeArguments_Nvenc_UsesP7HqTuningAndConstantQuality()
    {
        string args = Rendered(Request(), ImagePipeEncoder.Nvenc);

        Assert.Contains("-c:v h264_nvenc", args);
        Assert.Contains("-preset p7", args);
        Assert.Equal("p7", FfmpegService.DefaultNvencPreset);
        Assert.Contains("-tune hq", args);
        Assert.Contains($"-cq {FfmpegService.DefaultNvencQuality}", args);
        Assert.Contains("-pix_fmt yuv420p", args);
        Assert.DoesNotContain("-crf", args);

        // -multipass exists to spend a bitrate budget well, and -cq is a quality target with no budget
        // to spend: it would buy encode time and little else.
        Assert.DoesNotContain("-multipass", args);
    }

    [Fact]
    public void BuildImagePipeArguments_Utvideo_HasNoQualityKnobOrPixelFormat()
    {
        string args = Rendered(Request(), ImagePipeEncoder.Utvideo);

        Assert.Contains("-c:v utvideo", args);
        Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-cq", args);
        Assert.DoesNotContain("-preset", args);
        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Theory]
    [InlineData(ImagePipeEncoder.Nvenc, "p1")]
    [InlineData(ImagePipeEncoder.Nvenc, "p7")]
    [InlineData(ImagePipeEncoder.X264, "slow")]
    [InlineData(ImagePipeEncoder.X264, "ultrafast")]
    public void BuildImagePipeArguments_ConfiguredPreset_ReachesTheArgv(
        ImagePipeEncoder encoder,
        string preset)
    {
        string args = Rendered(Request(encoderPreset: preset), encoder);

        Assert.Contains($"-preset {preset}", args);
    }

    /// <summary>
    ///     A preset this code has never heard of is ffmpeg's to reject, by name and immediately, rather
    ///     than this build's to second-guess - a whitelist here would reject presets a newer ffmpeg
    ///     supports.
    /// </summary>
    [Fact]
    public void BuildImagePipeArguments_UnknownPreset_IsPassedThroughRatherThanRejected()
    {
        string args = Rendered(Request(encoderPreset: "p11"), ImagePipeEncoder.Nvenc);

        Assert.Contains("-preset p11", args);
    }

    [Fact]
    public void BuildImagePipeArguments_PresetIsIgnoredByTheLosslessEncoder()
    {
        string args = Rendered(Request(encoderPreset: "slow"), ImagePipeEncoder.Utvideo);

        Assert.DoesNotContain("-preset", args);
        Assert.Null(FfmpegService.ResolvePreset(ImagePipeEncoder.Utvideo, "slow"));
    }

    [Theory]
    [InlineData(ImagePipeEncoder.Nvenc, null, "p7")]
    [InlineData(ImagePipeEncoder.Nvenc, "  ", "p7")]
    [InlineData(ImagePipeEncoder.Nvenc, "p5", "p5")]
    [InlineData(ImagePipeEncoder.X264, null, "veryfast")]
    [InlineData(ImagePipeEncoder.X264, "medium", "medium")]
    public void ResolvePreset_NullOrBlank_YieldsTheEncoderDefault(
        ImagePipeEncoder encoder,
        string? requested,
        string expected) =>
        Assert.Equal(expected, FfmpegService.ResolvePreset(encoder, requested));

    [Theory]
    [InlineData(ImagePipeEncoder.Nvenc, "-cq 12")]
    [InlineData(ImagePipeEncoder.X264, "-crf 12")]
    public void BuildImagePipeArguments_QualityOverride_ReplacesTheDefault(
        ImagePipeEncoder encoder,
        string expected)
    {
        string args = Rendered(Request(quality: 12), encoder);

        Assert.Contains(expected, args);
    }

    [Fact]
    public void BuildImagePipeArguments_PixelFormatOverride_IsHonoredEvenForLosslessEncoders()
    {
        Assert.Contains("-pix_fmt yuv444p", Rendered(Request(pixelFormat: "yuv444p"), ImagePipeEncoder.X264));
        Assert.Contains("-pix_fmt gbrp", Rendered(Request(pixelFormat: "gbrp"), ImagePipeEncoder.Utvideo));
    }

    [Fact]
    public void BuildImagePipeArguments_FractionalFrameRate_IsFormattedInvariantly()
    {
        string args = Rendered(Request(frameRate: 59.94), ImagePipeEncoder.X264);

        Assert.Contains("-framerate 59.94", args);
    }

    [Fact]
    public void BuildImagePipeArguments_ExtraOutputArgs_PrecedeTheOutputPath()
    {
        IReadOnlyList<string> argv = FfmpegService.BuildImagePipeArguments(
            Request(extraOutputArgs: ["-movflags", "+faststart"]), ImagePipeEncoder.X264);

        Assert.Equal("/out/clip.mp4", argv[^1]);
        Assert.Equal("+faststart", argv[^2]);
        Assert.Equal("-movflags", argv[^3]);
    }

    [Fact]
    public void BuildImagePipeArguments_PathWithSpaces_StaysOneArgvEntry()
    {
        const string path = "/Users/someone/Counter-Strike Global Offensive/csvg/clip 001.mp4";

        IReadOnlyList<string> argv = FfmpegService.BuildImagePipeArguments(
            Request() with { OutputPath = path }, ImagePipeEncoder.X264);

        Assert.Equal(path, argv[^1]);
        Assert.Single(argv, argument => argument == path);
    }

    [Fact]
    public void BuildImagePipeArguments_UnresolvedAuto_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            FfmpegService.BuildImagePipeArguments(Request(), ImagePipeEncoder.Auto));

    #endregion

    #region Mux argument construction (no ffmpeg needed)

    private static string RenderedMux(
        string? audioPath = "/clips/audio.wav",
        string audioCodec = "aac",
        int? bitrateKbps = null) =>
        string.Join(' ', FfmpegService.BuildMuxArguments(
            "/clips/video.mp4", audioPath, "/out/clip.mp4", audioCodec, bitrateKbps));

    [Fact]
    public void BuildMuxArguments_WithAudio_CopiesVideoAndMapsOneStreamFromEach()
    {
        string args = RenderedMux();

        Assert.Contains("-i /clips/video.mp4", args);
        Assert.Contains("-i /clips/audio.wav", args);
        Assert.Contains("-c:v copy", args);
        Assert.Contains("-c:a aac", args);
        Assert.Contains("-map 0:v:0", args);
        Assert.Contains("-map 1:a:0", args);

        // The video has to be input 0 for those maps to mean what they say.
        Assert.True(args.IndexOf("/clips/video.mp4", StringComparison.Ordinal) <
                    args.IndexOf("/clips/audio.wav", StringComparison.Ordinal),
            $"video must be the first input: {args}");
    }

    [Fact]
    public void BuildMuxArguments_NeverTrimsOrPadsToMatchDurations()
    {
        string args = RenderedMux();

        // Deliberate: the engine mixes audio per simulated frame, so the wav and the video already
        // share a timebase. Any of these would introduce drift rather than remove it.
        Assert.DoesNotContain("-shortest", args);
        Assert.DoesNotContain("-apad", args);
        Assert.DoesNotContain("-async", args);
        Assert.DoesNotContain("-t ", args);
    }

    [Fact]
    public void BuildMuxArguments_Bitrate_EmitsKilobitsPerSecond()
    {
        Assert.Contains("-b:a 192k", RenderedMux(bitrateKbps: 192));
        Assert.DoesNotContain("-b:a", RenderedMux());
    }

    [Fact]
    public void BuildMuxArguments_AudioCodecCopy_IsPassedThrough() =>
        Assert.Contains("-c:a copy", RenderedMux(audioCodec: "copy"));

    [Fact]
    public void BuildMuxArguments_NoAudio_IsAPlainRemuxOfEveryStream()
    {
        string args = RenderedMux(audioPath: null);

        Assert.Contains("-i /clips/video.mp4", args);
        Assert.Contains("-c copy", args);
        Assert.DoesNotContain("-map", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.EndsWith("/out/clip.mp4", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMuxArguments_PathsWithSpaces_StayOneArgvEntryEach()
    {
        const string video = "/Games/Counter-Strike Global Offensive/clip 1.mp4";
        const string audio = "/Games/Counter-Strike Global Offensive/clip 1.wav";

        IReadOnlyList<string> argv = FfmpegService.BuildMuxArguments(
            video, audio, "/out/clip 1.mp4", "aac", null);

        Assert.Single(argv, argument => argument == video);
        Assert.Single(argv, argument => argument == audio);
        Assert.Equal("/out/clip 1.mp4", argv[^1]);
    }

    #endregion

    #region Encoder resolution and probe parsing (no ffmpeg needed)

    private const string SampleEncoderListing = """
                                                Encoders:
                                                 V..... = Video
                                                 A..... = Audio
                                                 S..... = Subtitle
                                                 .F.... = Frame-level multithreading
                                                 ..S... = Slice-level multithreading
                                                 ...X.. = Codec is experimental
                                                 ....B. = Supports draw_horiz_band
                                                 .....D = Supports direct rendering method 1
                                                 ------
                                                 V....D libx264              libx264 H.264 / AVC (codec h264)
                                                 V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
                                                 V....D utvideo              Ut Video
                                                 V....D mjpeg                MJPEG (Motion JPEG)
                                                 A....D aac                  AAC (Advanced Audio Coding)
                                                """;

    [Fact]
    public void ParseEncoders_ReadsTheNamesAndIgnoresTheLegend()
    {
        IReadOnlySet<string> encoders = FfmpegService.ParseEncoders(SampleEncoderListing);

        Assert.Contains("libx264", encoders);
        Assert.Contains("h264_nvenc", encoders);
        Assert.Contains("utvideo", encoders);
        Assert.Contains("mjpeg", encoders);
        Assert.Contains("aac", encoders);

        // The legend lines are not encoders.
        Assert.DoesNotContain("Video", encoders);
        Assert.DoesNotContain("Audio", encoders);
        Assert.DoesNotContain("=", encoders);
        Assert.Equal(5, encoders.Count);
    }

    [Fact]
    public void ParseEncoders_EmptyOutput_YieldsNoEncoders() =>
        Assert.Empty(FfmpegService.ParseEncoders(string.Empty));

    [Fact]
    public void ResolveEncoder_Auto_PrefersNvencWhenTheBuildHasIt()
    {
        HashSet<string> withNvenc = ["libx264", "h264_nvenc"];

        Assert.Equal(ImagePipeEncoder.Nvenc,
            FfmpegService.ResolveEncoder(ImagePipeEncoder.Auto, withNvenc));
    }

    [Fact]
    public void ResolveEncoder_Auto_FallsBackToX264WithoutNvenc()
    {
        HashSet<string> withoutNvenc = ["libx264", "utvideo"];

        Assert.Equal(ImagePipeEncoder.X264,
            FfmpegService.ResolveEncoder(ImagePipeEncoder.Auto, withoutNvenc));
    }

    [Fact]
    public void ResolveEncoder_Auto_WithNothingProbed_StillPicksTheSoftwareEncoder()
    {
        // A failed probe reports an empty set; Auto must still name something ffmpeg is
        // overwhelmingly likely to have rather than give up.
        Assert.Equal(ImagePipeEncoder.X264,
            FfmpegService.ResolveEncoder(ImagePipeEncoder.Auto, new HashSet<string>()));
    }

    [Theory]
    [InlineData(ImagePipeEncoder.Nvenc)]
    [InlineData(ImagePipeEncoder.X264)]
    [InlineData(ImagePipeEncoder.Utvideo)]
    public void ResolveEncoder_ExplicitChoice_IsNeverSubstituted(ImagePipeEncoder requested)
    {
        // Even when the build does not have it: an explicit request that cannot work should fail
        // loudly at encode time, not silently become something else.
        Assert.Equal(requested, FfmpegService.ResolveEncoder(requested, new HashSet<string>()));
    }

    [Fact]
    public void EncoderName_MapsEveryConcreteEncoder()
    {
        Assert.Equal("h264_nvenc", FfmpegService.EncoderName(ImagePipeEncoder.Nvenc));
        Assert.Equal("libx264", FfmpegService.EncoderName(ImagePipeEncoder.X264));
        Assert.Equal("utvideo", FfmpegService.EncoderName(ImagePipeEncoder.Utvideo));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegService.EncoderName(ImagePipeEncoder.Auto));
    }

    #endregion

    #region Validation (no ffmpeg needed)

    [Fact]
    public async Task StartImagePipeEncodeAsync_InvalidFrameRate_ReturnsAFailedSessionWithoutSpawning()
    {
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(
            Request(frameRate: 0), TestContext.Current.CancellationToken);

        FfmpegEncodeResult result = await session.Completion;
        Assert.False(result.Success);
        Assert.Contains("Frame rate", result.ErrorMessage);
        Assert.Same(Stream.Null, session.Input);
    }

    [Fact]
    public async Task StartImagePipeEncodeAsync_NoOutputPath_ReturnsAFailedSession()
    {
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(
            Request() with { OutputPath = "   " }, TestContext.Current.CancellationToken);

        FfmpegEncodeResult result = await session.Completion;
        Assert.False(result.Success);
        Assert.Contains("output path", result.ErrorMessage);
    }

    [Fact]
    public async Task MuxAsync_MissingVideo_ReturnsFailed()
    {
        FfmpegService service = CreateService();

        FfmpegEncodeResult result = await service.MuxAsync(
            _tempDir.Combine("nope.mp4"), null, _tempDir.Combine("out.mp4"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Video file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task MuxAsync_MissingAudio_ReturnsFailed()
    {
        FfmpegService service = CreateService();
        string video = _tempDir.Combine("video.mp4");
        await File.WriteAllTextAsync(video, "not a real video", TestContext.Current.CancellationToken);

        FfmpegEncodeResult result = await service.MuxAsync(
            video, _tempDir.Combine("nope.wav"), _tempDir.Combine("out.mp4"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Audio file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task MuxAsync_AlreadyCancelledToken_ReturnsFailedWithoutRunning()
    {
        FfmpegService service = CreateService();
        string video = _tempDir.Combine("video.mp4");
        string output = _tempDir.Combine("out.mp4");
        await File.WriteAllTextAsync(video, "not a real video", TestContext.Current.CancellationToken);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        FfmpegEncodeResult result = await service
            .MuxAsync(video, null, output, cancellationToken: cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    #endregion

    #region Real ffmpeg (skipped when ffmpeg is unavailable)

    /// <summary>
    ///     Feeds a directory of frame images into an encode session in sequence order and returns the
    ///     encode result, closing the pipe so ffmpeg sees the end of the stream.
    /// </summary>
    private static async Task<FfmpegEncodeResult> PipeFramesAsync(
        IImagePipeEncodeSession session,
        string frameDirectory,
        string extension,
        CancellationToken cancellationToken)
    {
        foreach (string frame in Directory.GetFiles(frameDirectory, $"*.{extension}").Order(StringComparer.Ordinal))
        {
            await using FileStream source = File.OpenRead(frame);
            await source.CopyToAsync(session.Input, cancellationToken);
        }

        await session.Input.FlushAsync(cancellationToken);
        await session.Input.DisposeAsync();

        return await session.Completion.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
    }

    [Fact]
    public async Task ProbeEncodersAsync_RealFfmpeg_FindsTheEncodersEveryBuildHas()
    {
        FfmpegTools.SkipUnlessAvailable();
        FfmpegService service = CreateService();

        FfmpegEncoderProbeResult result = await service.ProbeEncodersAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Has("mjpeg"), "every ffmpeg build has the mjpeg encoder");
        Assert.True(result.Has("libx264"), "the test host's ffmpeg is expected to have libx264");
        Assert.True(result.Encoders.Count > 50, $"only {result.Encoders.Count} encoders were parsed");
    }

    [Fact]
    public async Task ProbeEncodersAsync_RealFfmpeg_CachesASuccessfulProbe()
    {
        FfmpegTools.SkipUnlessAvailable();
        FfmpegService service = CreateService();

        FfmpegEncoderProbeResult first = await service.ProbeEncodersAsync(TestContext.Current.CancellationToken);
        FfmpegEncoderProbeResult second = await service.ProbeEncodersAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task StartImagePipeEncodeAsync_RealTargaFrames_ProducesEveryFrameAtTheRequestedRate()
    {
        FfmpegTools.SkipUnlessAvailable();

        const int frameCount = 24;
        const int frameRate = 24;
        string frames = _tempDir.Combine("tga-frames");
        FfmpegTools.GenerateFrameSequence(frames, "tga", frameCount, frameRate);
        int frameSize = (int)new FileInfo(Path.Combine(frames, "frame0001.tga")).Length;

        string output = _tempDir.Combine("from-tga.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = frameRate,
            SourceFormat = ImagePipeSourceFormat.Targa,
            FrameSizeBytes = frameSize,
            Encoder = ImagePipeEncoder.X264
        }, TestContext.Current.CancellationToken);

        FfmpegEncodeResult result = await PipeFramesAsync(
            session, frames, "tga", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(frameCount, FfmpegTools.CountVideoFrames(output));
        Assert.Equal("24/1", FfmpegTools.VideoFrameRate(output));
    }

    [Fact]
    public async Task StartImagePipeEncodeAsync_RealJpegFrames_ProducesEveryFrame()
    {
        FfmpegTools.SkipUnlessAvailable();

        const int frameCount = 30;
        const int frameRate = 30;
        string frames = _tempDir.Combine("jpg-frames");
        FfmpegTools.GenerateFrameSequence(frames, "jpg", frameCount, frameRate);

        string output = _tempDir.Combine("from-jpg.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = frameRate,
            SourceFormat = ImagePipeSourceFormat.Mjpeg,
            Encoder = ImagePipeEncoder.X264
        }, TestContext.Current.CancellationToken);

        FfmpegEncodeResult result = await PipeFramesAsync(
            session, frames, "jpg", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(frameCount, FfmpegTools.CountVideoFrames(output));
        Assert.Equal("30/1", FfmpegTools.VideoFrameRate(output));
    }

    /// <summary>
    ///     A configured preset has to survive the trip to a real ffmpeg, not just to an argv: presets
    ///     are the one encode setting this code passes through without validating, so the proof that a
    ///     name is accepted is an encode that produced a file. Kept tiny (a handful of 160x120 frames)
    ///     because <c>slower</c> is exactly the setting that makes an encode take time.
    /// </summary>
    [Fact]
    public async Task StartImagePipeEncodeAsync_RealSlowPreset_EncodesAndReportsThePresetItUsed()
    {
        FfmpegTools.SkipUnlessAvailable();

        const int frameCount = 6;
        string frames = _tempDir.Combine("slow-preset-frames");
        FfmpegTools.GenerateFrameSequence(frames, "jpg", frameCount, 24);
        string output = _tempDir.Combine("slow-preset.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = 24,
            SourceFormat = ImagePipeSourceFormat.Mjpeg,
            Encoder = ImagePipeEncoder.X264,
            EncoderPreset = "slower"
        }, TestContext.Current.CancellationToken);

        Assert.Equal("slower", session.Preset);

        FfmpegEncodeResult result = await PipeFramesAsync(
            session, frames, "jpg", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(frameCount, FfmpegTools.CountVideoFrames(output));
    }

    /// <summary>
    ///     ffmpeg is the authority on preset names, and it says so on stderr. The failure has to reach
    ///     the caller as a failed result carrying that text - an exit code alone does not distinguish a
    ///     rejected option from an undecodable frame.
    /// </summary>
    [Fact]
    public async Task StartImagePipeEncodeAsync_RealUnknownPreset_FailsWithFfmpegsOwnComplaint()
    {
        FfmpegTools.SkipUnlessAvailable();

        string frames = _tempDir.Combine("bad-preset-frames");
        FfmpegTools.GenerateFrameSequence(frames, "jpg", 4, 24);
        string output = _tempDir.Combine("bad-preset.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = 24,
            SourceFormat = ImagePipeSourceFormat.Mjpeg,
            Encoder = ImagePipeEncoder.X264,
            EncoderPreset = "definitely-not-a-preset"
        }, TestContext.Current.CancellationToken);

        FfmpegEncodeResult result;
        try
        {
            result = await PipeFramesAsync(session, frames, "jpg", TestContext.Current.CancellationToken);
        }
        catch (IOException)
        {
            // ffmpeg refused the option and closed the pipe before the frames landed.
            result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
        }

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FfmpegOutput),
            "ffmpeg's stderr is the only thing that says which option it refused");
        Assert.False(File.Exists(output), "a failed encode should not leave an output file behind");
    }

    [Fact]
    public async Task StartImagePipeEncodeAsync_AutoEncoder_ResolvesToSomethingTheBuildHas()
    {
        FfmpegTools.SkipUnlessAvailable();

        string frames = _tempDir.Combine("auto-frames");
        FfmpegTools.GenerateFrameSequence(frames, "jpg", 12, 24);
        string output = _tempDir.Combine("auto.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = 24,
            SourceFormat = ImagePipeSourceFormat.Mjpeg,
            Encoder = ImagePipeEncoder.Auto
        }, TestContext.Current.CancellationToken);

        Assert.NotEqual(ImagePipeEncoder.Auto, session.Encoder);

        FfmpegEncoderProbeResult probe = await service.ProbeEncodersAsync(TestContext.Current.CancellationToken);
        Assert.True(probe.Has(FfmpegService.EncoderName(session.Encoder)),
            $"Auto resolved to {session.Encoder}, which this ffmpeg build does not have");

        FfmpegEncodeResult result = await PipeFramesAsync(
            session, frames, "jpg", TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task StartImagePipeEncodeAsync_Cancelled_KillsFfmpegAndDeletesPartialOutput()
    {
        FfmpegTools.SkipUnlessAvailable();

        string frames = _tempDir.Combine("cancel-frames");
        FfmpegTools.GenerateFrameSequence(frames, "jpg", 30, 30);
        string output = _tempDir.Combine("cancelled.mp4");
        FfmpegService service = CreateService();
        using CancellationTokenSource cts = new();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = 30,
            SourceFormat = ImagePipeSourceFormat.Mjpeg,
            Encoder = ImagePipeEncoder.X264
        }, cts.Token);

        foreach (string frame in Directory.GetFiles(frames, "*.jpg").Order(StringComparer.Ordinal).Take(5))
        {
            await using FileStream source = File.OpenRead(frame);
            await source.CopyToAsync(session.Input, TestContext.Current.CancellationToken);
        }

        await session.Input.FlushAsync(TestContext.Current.CancellationToken);

        Stopwatch sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        FfmpegEncodeResult result = await session.Completion
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output), "partial output file should have been deleted");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"cancellation should kill ffmpeg promptly (took {sw.Elapsed})");
    }

    [Fact]
    public async Task StartImagePipeEncodeAsync_GarbageOnThePipe_ReturnsFailedAndLeavesNoOutput()
    {
        FfmpegTools.SkipUnlessAvailable();

        string output = _tempDir.Combine("garbage.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = 30,
            SourceFormat = ImagePipeSourceFormat.Mjpeg,
            Encoder = ImagePipeEncoder.X264
        }, TestContext.Current.CancellationToken);

        try
        {
            await session.Input.WriteAsync(
                new byte[4096], TestContext.Current.CancellationToken);
            await session.Input.DisposeAsync();
        }
        catch (IOException)
        {
            // ffmpeg may have given up and closed the pipe before the write landed.
        }

        FfmpegEncodeResult result = await session.Completion
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(output), "a failed encode should not leave an output file behind");
    }

    [Fact]
    public async Task MuxAsync_RealSilentWavOntoRealVideo_KeepsBothDurations()
    {
        FfmpegTools.SkipUnlessAvailable();

        const double seconds = 2.0;
        string video = _tempDir.Combine("video.mp4");
        string audio = _tempDir.Combine("silence.wav");
        string output = _tempDir.Combine("muxed.mp4");
        FfmpegTools.GenerateSilentVideo(video, seconds);
        FfmpegTools.GenerateSilentWav(audio, seconds);

        FfmpegService service = CreateService();

        FfmpegEncodeResult result = await service
            .MuxAsync(video, audio, output, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, FfmpegTools.StreamCount(output, "v"));
        Assert.Equal(1, FfmpegTools.StreamCount(output, "a"));

        double videoDuration = FfmpegTools.StreamDuration(output, "v:0");
        double audioDuration = FfmpegTools.StreamDuration(output, "a:0");

        // Nothing was trimmed or padded: both streams keep the length they came in with, which is
        // the whole point of muxing without -shortest.
        Assert.True(Math.Abs(videoDuration - seconds) < 0.1,
            $"video duration drifted: {videoDuration} vs {seconds}");
        Assert.True(Math.Abs(audioDuration - seconds) < 0.1,
            $"audio duration drifted: {audioDuration} vs {seconds}");
        Assert.True(Math.Abs(videoDuration - audioDuration) < 0.1,
            $"streams disagree: video {videoDuration}, audio {audioDuration}");
    }

    [Fact]
    public async Task MuxAsync_RealVideoWithoutAudio_IsAPlainRemux()
    {
        FfmpegTools.SkipUnlessAvailable();

        const double seconds = 1.0;
        string video = _tempDir.Combine("only-video.mp4");
        string output = _tempDir.Combine("remuxed.mp4");
        FfmpegTools.GenerateSilentVideo(video, seconds);

        FfmpegService service = CreateService();

        FfmpegEncodeResult result = await service
            .MuxAsync(video, null, output, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, FfmpegTools.StreamCount(output, "a"));
        Assert.Equal(FfmpegTools.CountVideoFrames(video), FfmpegTools.CountVideoFrames(output));
    }

    [Fact]
    public async Task MuxAsync_RealHungFfmpeg_CancellationKillsProcess()
    {
        FfmpegTools.SkipUnlessAvailable();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "FIFO-based hang simulation requires Unix");

        string video = _tempDir.Combine("video.mp4");
        FfmpegTools.GenerateSilentVideo(video, 1.0);

        // ffmpeg blocks forever opening a FIFO nothing ever writes to.
        string fifo = _tempDir.Combine("hang.wav");
        FfmpegTools.Run("mkfifo", [fifo], 10_000);

        string output = _tempDir.Combine("hung.mp4");
        FfmpegService service = CreateService();
        using CancellationTokenSource cts = new();

        Task<FfmpegEncodeResult> task = service.MuxAsync(video, fifo, output, cancellationToken: cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted, "ffmpeg should be hung on the FIFO input");

        Stopwatch sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        FfmpegEncodeResult result = await task.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output), "partial output file should have been deleted");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"cancellation should kill ffmpeg promptly (took {sw.Elapsed})");
    }

    [Fact]
    public async Task MuxAsync_RealHungFfmpeg_TimeoutKillsProcess()
    {
        FfmpegTools.SkipUnlessAvailable();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "FIFO-based hang simulation requires Unix");

        string video = _tempDir.Combine("video.mp4");
        FfmpegTools.GenerateSilentVideo(video, 1.0);
        string fifo = _tempDir.Combine("hang.wav");
        FfmpegTools.Run("mkfifo", [fifo], 10_000);

        string output = _tempDir.Combine("timed-out.mp4");
        FfmpegService service = CreateService(new FfmpegOptions { MuxTimeout = TimeSpan.FromSeconds(2) });

        Stopwatch sw = Stopwatch.StartNew();
        FfmpegEncodeResult result = await service
            .MuxAsync(video, fifo, output, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MuxTimeout", result.ErrorMessage);
        Assert.False(File.Exists(output), "partial output file should have been deleted");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"timeout should kill ffmpeg promptly (took {sw.Elapsed})");
    }

    /// <summary>
    ///     The pair working together: a consumer draining a staging directory straight into the
    ///     encoder's standard input, which is exactly how an offline capture is wired.
    /// </summary>
    [Fact]
    public async Task FrameSequenceConsumer_IntoImagePipe_EncodesEveryStagedFrame()
    {
        FfmpegTools.SkipUnlessAvailable();

        const int frameCount = 24;
        const int frameRate = 24;
        string staging = _tempDir.Combine("staging");
        FfmpegTools.GenerateFrameSequence(staging, "tga", frameCount, frameRate);
        int frameSize = (int)new FileInfo(Path.Combine(staging, "frame0001.tga")).Length;

        string output = _tempDir.Combine("streamed.mp4");
        FfmpegService service = CreateService();

        await using IImagePipeEncodeSession session = await service.StartImagePipeEncodeAsync(new ImagePipeEncodeRequest
        {
            OutputPath = output,
            FrameRate = frameRate,
            SourceFormat = ImagePipeSourceFormat.Targa,
            FrameSizeBytes = frameSize,
            Encoder = ImagePipeEncoder.X264
        }, TestContext.Current.CancellationToken);

        await using FrameSequenceConsumer consumer = new(
            staging,
            new FrameSequencePattern { Prefix = "frame", DigitCount = 4, Extension = "tga", FirstFrameNumber = 1 },
            session.Input,
            TimeSpan.FromMilliseconds(25));

        consumer.Start(TestContext.Current.CancellationToken);
        FrameSequenceConsumeResult consumed = await consumer.StopAsync()
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(consumed.Success, consumed.ErrorMessage);
        Assert.Equal(frameCount, consumed.FramesConsumed);
        Assert.Equal(0, consumed.DroppedFrames);
        Assert.Empty(Directory.GetFiles(staging));

        FfmpegEncodeResult result = await session.Completion
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(frameCount, FfmpegTools.CountVideoFrames(output));
        Assert.Equal("24/1", FfmpegTools.VideoFrameRate(output));
    }

    #endregion
}
