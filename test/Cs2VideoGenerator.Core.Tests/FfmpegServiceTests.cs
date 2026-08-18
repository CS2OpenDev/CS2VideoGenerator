#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Cancellation, timeout and cleanup around <see cref="FfmpegService.ConcatenateAsync" />,
///     in two layers. The fake-process ones override the <c>RunConcatenationAsync</c> seam to stand in
///     for a hung or failing ffmpeg and cover the orchestration: timeout wiring, partial-output
///     cleanup, failed-result mapping. The rest need ffmpeg on PATH - they generate tiny color-bar
///     clips and concatenate them, and on Unix a never-written FIFO input hangs ffmpeg for real, which
///     is the only way to prove cancellation and timeout actually kill the process.
/// </summary>
public class FfmpegServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new("csvg-ffmpeg-tests-");

    public void Dispose()
    {
        _tempDir.Dispose();
        GC.SuppressFinalize(this);
    }

    private static FfmpegService CreateService(FfmpegOptions? options = null) =>
        new(NullLogger<FfmpegService>.Instance, Options.Create(options ?? new FfmpegOptions()));

    private string CreateDummyInput(string name)
    {
        string path = _tempDir.Combine(name);
        File.WriteAllText(path, "not a real video");
        return path;
    }

    #region Validation (no ffmpeg needed)

    [Fact]
    public async Task ConcatenateAsync_NoInputs_ReturnsFailed()
    {
        FfmpegService service = CreateService();

        FfmpegConcatResult result = await service.ConcatenateAsync(
            [], _tempDir.Combine("out.mp4"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("No input files", result.ErrorMessage);
    }

    [Fact]
    public async Task ConcatenateAsync_MissingInput_ReturnsFailed()
    {
        FfmpegService service = CreateService();
        string missing = _tempDir.Combine("does-not-exist.mp4");

        FfmpegConcatResult result = await service.ConcatenateAsync(
            [missing], _tempDir.Combine("out.mp4"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
        Assert.Contains(missing, result.ErrorMessage);
    }

    #endregion

    #region Fake-process tests (always run)

    /// <summary>
    ///     Fake that simulates a hung ffmpeg process: writes a partial output file, then blocks
    ///     until the token is cancelled (as FFMpegCore does after killing the real process, it
    ///     surfaces an <see cref="OperationCanceledException" />).
    /// </summary>
    private sealed class HangingFfmpegService(FfmpegOptions options)
        : FfmpegService(NullLogger<FfmpegService>.Instance, Options.Create(options))
    {
        public bool ProcessTerminated { get; private set; }

        protected override async Task RunConcatenationAsync(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            Cs2EncodingSettings? encoding,
            CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(outputPath, "partial output", CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ProcessTerminated = true;
                throw;
            }
        }
    }

    private sealed class SucceedingFfmpegService()
        : FfmpegService(NullLogger<FfmpegService>.Instance, Options.Create(new FfmpegOptions()))
    {
        protected override async Task RunConcatenationAsync(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            Cs2EncodingSettings? encoding,
            CancellationToken cancellationToken) =>
            await File.WriteAllTextAsync(outputPath, "concatenated", cancellationToken);
    }

    private sealed class FailingFfmpegService()
        : FfmpegService(NullLogger<FfmpegService>.Instance, Options.Create(new FfmpegOptions()))
    {
        protected override Task RunConcatenationAsync(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            Cs2EncodingSettings? encoding,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ffmpeg exited with non-zero exit-code (1 - boom)");
    }

    [Fact]
    public async Task ConcatenateAsync_Cancelled_TerminatesProcessDeletesPartialOutput_ReturnsFailed()
    {
        HangingFfmpegService service = new(new FfmpegOptions());
        string output = _tempDir.Combine("out.mp4");
        string[] inputs = [CreateDummyInput("a.mp4"), CreateDummyInput("b.mp4")];
        using CancellationTokenSource cts = new();

        Task<FfmpegConcatResult> task = service.ConcatenateAsync(inputs, output, cancellationToken: cts.Token);
        await cts.CancelAsync();
        FfmpegConcatResult result = await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.ProcessTerminated, "ffmpeg process should have been terminated");
        Assert.False(File.Exists(output), "partial output file should have been deleted");
    }

    [Fact]
    public async Task ConcatenateAsync_Timeout_TerminatesProcessDeletesPartialOutput_ReturnsFailed()
    {
        HangingFfmpegService service = new(new FfmpegOptions
        {
            ConcatTimeout = TimeSpan.FromMilliseconds(250)
        });
        string output = _tempDir.Combine("out.mp4");
        string[] inputs = [CreateDummyInput("a.mp4"), CreateDummyInput("b.mp4")];

        FfmpegConcatResult result = await service
            .ConcatenateAsync(inputs, output, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.ProcessTerminated, "ffmpeg process should have been terminated");
        Assert.False(File.Exists(output), "partial output file should have been deleted");
    }

    [Fact]
    public async Task ConcatenateAsync_AlreadyCancelledToken_ReturnsFailedWithoutRunning()
    {
        HangingFfmpegService service = new(new FfmpegOptions());
        string output = _tempDir.Combine("out.mp4");
        string[] inputs = [CreateDummyInput("a.mp4"), CreateDummyInput("b.mp4")];
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        FfmpegConcatResult result = await service
            .ConcatenateAsync(inputs, output, cancellationToken: cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ConcatenateAsync_ProcessSucceeds_ReturnsSucceeded()
    {
        SucceedingFfmpegService service = new();
        string output = _tempDir.Combine("out.mp4");
        string[] inputs = [CreateDummyInput("a.mp4"), CreateDummyInput("b.mp4")];

        FfmpegConcatResult result = await service.ConcatenateAsync(
            inputs, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task ConcatenateAsync_ProcessFails_ReturnsFailedWithMessage()
    {
        FailingFfmpegService service = new();
        string output = _tempDir.Combine("out.mp4");
        string[] inputs = [CreateDummyInput("a.mp4"), CreateDummyInput("b.mp4")];

        FfmpegConcatResult result = await service.ConcatenateAsync(
            inputs, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("non-zero exit-code", result.ErrorMessage);
    }

    #endregion

    #region Argument construction (no ffmpeg needed)

    /// <summary>
    ///     The argument string the service would run, through the internal
    ///     <see cref="FfmpegService.BuildConcatArguments" /> seam.
    /// </summary>
    private static string BuildArguments(Cs2EncodingSettings? encoding) =>
        FfmpegService.BuildConcatArguments(["/clips/a.mp4", "/clips/b.mp4"], "/out/compilation.mp4", encoding)
            .Arguments;

    [Fact]
    public void BuildConcatArguments_NoEncoding_UsesConcatDemuxerWithStreamCopy()
    {
        string args = BuildArguments(encoding: null);

        Assert.Contains("-f concat", args);
        Assert.Contains("-safe 0", args);
        Assert.Contains("-c:v copy", args);
        Assert.Contains("-c:a copy", args);
    }

    [Fact]
    public void BuildConcatArguments_AllNullEncoding_IsStreamCopy()
    {
        string args = BuildArguments(new Cs2EncodingSettings());

        Assert.Contains("-c:v copy", args);
        Assert.Contains("-c:a copy", args);
        Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-b:v", args);
        Assert.DoesNotContain("-b:a", args);
    }

    [Fact]
    public void BuildConcatArguments_CrfMode_EmitsCodecAndCrf_KeepsAudioCopy()
    {
        string args = BuildArguments(new Cs2EncodingSettings { VideoCodec = "libx264", Crf = 30 });

        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-crf 30", args);
        Assert.DoesNotContain("-c:v copy", args);
        // Audio has no settings: stays stream-copied.
        Assert.Contains("-c:a copy", args);
    }

    [Fact]
    public void BuildConcatArguments_VideoBitrateMode_EmitsBitrate()
    {
        string args = BuildArguments(new Cs2EncodingSettings { VideoCodec = "libx265", VideoBitrate = "8M" });

        Assert.Contains("-c:v libx265", args);
        Assert.Contains("-b:v 8M", args);
        Assert.DoesNotContain("-crf", args);
        Assert.Contains("-c:a copy", args);
    }

    [Fact]
    public void BuildConcatArguments_AudioSettingsOnly_ReencodesAudio_KeepsVideoCopy()
    {
        string args = BuildArguments(new Cs2EncodingSettings { AudioCodec = "aac", AudioBitrate = "192k" });

        Assert.Contains("-c:v copy", args);
        Assert.Contains("-c:a aac", args);
        Assert.Contains("-b:a 192k", args);
        Assert.DoesNotContain("-c:a copy", args);
    }

    [Fact]
    public void BuildConcatArguments_ExtraArgs_PassThroughVerbatim()
    {
        string args = BuildArguments(new Cs2EncodingSettings
        {
            ExtraInputArgs = "-hwaccel auto",
            ExtraOutputArgs = "-movflags +faststart -metadata title=\"csvg\""
        });

        Assert.Contains("-hwaccel auto", args);
        Assert.Contains("-movflags +faststart -metadata title=\"csvg\"", args);

        // Extra input args must precede the concat input; extra args alone keep stream copy.
        Assert.True(args.IndexOf("-hwaccel auto", StringComparison.Ordinal) <
                    args.IndexOf("-f concat", StringComparison.Ordinal),
            $"extra input args should precede the input: {args}");
        Assert.Contains("-c:v copy", args);
        Assert.Contains("-c:a copy", args);
    }

    #endregion

    #region Real ffmpeg tests (skipped when ffmpeg is unavailable)

    private static void SkipUnlessFfmpegAvailable() => FfmpegTools.SkipUnlessAvailable();

    /// <summary>
    ///     Generates a ~1 second color-bar test clip with a sine audio track, matching the
    ///     video+audio shape of OBS-produced clips.
    /// </summary>
    private string GenerateTestClip(string name)
    {
        string path = _tempDir.Combine(name);
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
                "-y -f lavfi -i testsrc=duration=1:size=320x240:rate=30 " +
                "-f lavfi -i sine=frequency=440:duration=1 " +
                $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);

        // Both pipes have to be drained while ffmpeg runs. It writes its banner and encode progress
        // to stderr - over 5 KB on some builds - and once the pipe buffer fills it blocks on the
        // write and never exits, which surfaces here as the wait below timing out rather than as
        // anything about ffmpeg. Same shape as FfmpegTools.Run.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        Assert.True(process.WaitForExit(60_000), "clip generation timed out");

        stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"ffmpeg exited with {process.ExitCode}: {error}");
        Assert.True(File.Exists(path));
        return path;
    }

    /// <summary>
    ///     Creates a FIFO (named pipe) that is never written to. ffmpeg blocks forever opening
    ///     it as a concat input, simulating a hung ffmpeg process. Unix only.
    /// </summary>
    private string CreateHangingFifo(string name)
    {
        string path = _tempDir.Combine(name);
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "mkfifo",
            Arguments = $"\"{path}\"",
            UseShellExecute = false
        });
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    [Fact]
    public async Task ConcatenateAsync_TwoRealClips_Succeeds()
    {
        SkipUnlessFfmpegAvailable();

        string clip1 = GenerateTestClip("clip1.mp4");
        string clip2 = GenerateTestClip("clip2.mp4");
        string output = _tempDir.Combine("compilation.mp4");
        FfmpegService service = CreateService();

        FfmpegConcatResult result = await service
            .ConcatenateAsync([clip1, clip2], output, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(result.OutputPath));

        // Output must contain both clips: larger than either input (stream copy).
        long outputLength = new FileInfo(result.OutputPath).Length;
        Assert.True(outputLength > new FileInfo(clip1).Length,
            $"output ({outputLength} bytes) should be larger than a single input clip");
        Assert.True(result.ProcessingDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcatenateAsync_TwoRealClips_ReencodeWithLibx264Crf_Succeeds()
    {
        SkipUnlessFfmpegAvailable();

        string clip1 = GenerateTestClip("clip1.mp4");
        string clip2 = GenerateTestClip("clip2.mp4");
        string output = _tempDir.Combine("compilation-reencoded.mp4");
        FfmpegService service = CreateService();

        FfmpegConcatResult result = await service
            .ConcatenateAsync([clip1, clip2],
                output,
                new Cs2EncodingSettings { VideoCodec = "libx264", Crf = 30 },
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(new FileInfo(result.OutputPath).Length > 0);
        Assert.True(result.ProcessingDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcatenateAsync_RealHungFfmpeg_CancellationKillsProcess()
    {
        SkipUnlessFfmpegAvailable();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "FIFO-based hang simulation requires Unix");

        string clip = GenerateTestClip("clip1.mp4");
        // ffmpeg demuxes clip1 instantly, then blocks forever opening the never-written FIFO.
        string fifo = CreateHangingFifo("hang.mp4");
        string output = _tempDir.Combine("compilation.mp4");
        FfmpegService service = CreateService();
        using CancellationTokenSource cts = new();

        Task<FfmpegConcatResult> task = service.ConcatenateAsync([clip, fifo], output, cancellationToken: cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted, "ffmpeg should be hung on the FIFO input");

        Stopwatch sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        FfmpegConcatResult result = await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output), "partial output file should have been deleted");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"cancellation should kill ffmpeg promptly (took {sw.Elapsed})");
    }

    [Fact]
    public async Task ConcatenateAsync_RealHungFfmpeg_TimeoutKillsProcess()
    {
        SkipUnlessFfmpegAvailable();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "FIFO-based hang simulation requires Unix");

        string clip = GenerateTestClip("clip1.mp4");
        string fifo = CreateHangingFifo("hang.mp4");
        string output = _tempDir.Combine("compilation.mp4");
        FfmpegService service = CreateService(new FfmpegOptions
        {
            ConcatTimeout = TimeSpan.FromSeconds(2)
        });

        Stopwatch sw = Stopwatch.StartNew();
        FfmpegConcatResult result = await service
            .ConcatenateAsync([clip, fifo], output, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output), "partial output file should have been deleted");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"timeout should kill ffmpeg promptly (took {sw.Elapsed})");
    }

    #endregion
}
