#region

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Models;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     FFmpeg service implementation using FFMpegCore.
/// </summary>
public partial class FfmpegService : IFfmpegService
{
    /// <summary>
    ///     Constant-quality level used for NVENC when the request does not name one.
    /// </summary>
    public const int DefaultNvencQuality = 19;

    /// <summary>
    ///     Constant rate factor used for x264 when the request does not name one.
    /// </summary>
    public const int DefaultX264Quality = 18;

    /// <summary>
    ///     NVENC preset used when the request does not name one: the slowest of the p1-p7 scale.
    /// </summary>
    /// <remarks>
    ///     This encode is offline, and for a single-clip capture it is also the final quality - nothing
    ///     re-encodes after it. Frames pile up in staging when the encoder is behind the render, so a
    ///     slower preset costs wall clock and nothing else. On NVENC that cost is small (the render, not
    ///     a dedicated silicon block, is what dominates) while the gain at a near-lossless <c>-cq</c> is
    ///     real, so the trade is worth taking by default.
    /// </remarks>
    public const string DefaultNvencPreset = "p7";

    /// <summary>
    ///     x264 preset used when the request does not name one.
    /// </summary>
    /// <remarks>
    ///     <c>veryfast</c> rather than <c>ultrafast</c>: ultrafast's quality loss at a given CRF is real
    ///     (it drops the analysis that CRF assumes when picking its quantizers), and this file is the
    ///     delivered video for a single clip. It is deliberately not slower than that either - software
    ///     H.264 is where an offline render actually waits, and anything past <c>veryfast</c> can fall
    ///     behind a fast render by hours over a long compilation. Set
    ///     <c>Capture:InEngine:IntermediateEncoderPreset</c> to trade that wall clock for quality.
    /// </remarks>
    public const string DefaultX264Preset = "veryfast";

    /// <summary>
    ///     Log level and progress flags shared by every invocation this service builds itself.
    ///     Progress lines are noise here; errors are what ends up in a failed result's message.
    /// </summary>
    private static readonly string[] CommonArguments = ["-y", "-hide_banner", "-nostats", "-loglevel", "error"];

    private readonly SemaphoreSlim _encoderProbeGate = new(1, 1);
    private readonly ILogger<FfmpegService> _logger;
    private readonly FfmpegOptions _options;
    private FfmpegEncoderProbeResult? _encoderProbe;

    /// <summary>
    ///     Creates the service and configures FFMpegCore's global options: when
    ///     <see cref="FfmpegOptions.BinaryDirectory" /> is set, ffmpeg/ffprobe are loaded from
    ///     that directory; otherwise they are resolved from PATH. The binary-folder configuration is
    ///     process-global (FFMpegCore's <c>GlobalFFOptions</c>).
    /// </summary>
    /// <param name="logger">Logger for FFmpeg operations.</param>
    /// <param name="options">FFmpeg options (binary directory, concat timeout).</param>
    public FfmpegService(ILogger<FfmpegService> logger, IOptions<FfmpegOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        ConfigureFfmpeg();
    }

    private void ConfigureFfmpeg()
    {
        if (!string.IsNullOrWhiteSpace(_options.BinaryDirectory))
        {
            GlobalFFOptions.Configure(new FFOptions
            {
                BinaryFolder = _options.BinaryDirectory,
                TemporaryFilesFolder = Path.GetTempPath()
            });
            LogFfmpegConfigured(_options.BinaryDirectory);
        }
        // Otherwise, FFMpegCore will look for ffmpeg in PATH
    }

    /// <inheritdoc />
    public async Task<FfmpegConcatResult> ConcatenateAsync(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        Cs2EncodingSettings? encoding = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("FfmpegService.Concatenate", ActivityKind.Internal);
        activity?.SetTag("ffmpeg.input.count", inputFiles.Count);
        activity?.SetTag("ffmpeg.output.path", outputPath);
        activity?.SetTag("ffmpeg.encoding.reencode", encoding?.RequiresReEncode ?? false);

        Stopwatch sw = Stopwatch.StartNew();

        if (inputFiles.Count == 0)
        {
            return FfmpegConcatResult.Failed(outputPath, "No input files provided");
        }

        List<string> missingFiles = inputFiles.Where(f => !File.Exists(f)).ToList();
        if (missingFiles.Count > 0)
        {
            return FfmpegConcatResult.Failed(outputPath,
                $"Input file(s) not found: {string.Join(", ", missingFiles)}");
        }

        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        LogConcatenationStarting(inputFiles.Count, outputPath);

        // The concat demuxer resolves relative paths against the temporary list
        // file's directory, not the process working directory - use absolute paths.
        string[] absoluteInputs = inputFiles.Select(Path.GetFullPath).ToArray();
        outputPath = Path.GetFullPath(outputPath);

        // Enforce the configured timeout via a linked token so both caller
        // cancellation and timeout expiry kill the ffmpeg process.
        TimeSpan timeout = _options.ConcatTimeout;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(timeout);
        }

        try
        {
            await RunConcatenationAsync(absoluteInputs, outputPath, encoding, linkedCts.Token).ConfigureAwait(false);

            sw.Stop();

            if (!File.Exists(outputPath))
            {
                return FfmpegConcatResult.Failed(outputPath, "FFmpeg completed but output file was not created");
            }

            FileInfo outputInfo = new(outputPath);
            if (outputInfo.Length == 0)
            {
                return FfmpegConcatResult.Failed(outputPath, "FFmpeg created an empty output file");
            }

            LogConcatenationComplete(outputPath, sw.Elapsed, outputInfo.Length);
            activity?.SetTag("ffmpeg.duration.ms", sw.ElapsedMilliseconds);
            activity?.SetTag("ffmpeg.output.size", outputInfo.Length);

            return FfmpegConcatResult.Succeeded(outputPath, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();

            // The ffmpeg process has been killed (CancellableThrough); remove the partial output.
            DeleteFileQuietly(outputPath);

            if (cancellationToken.IsCancellationRequested)
            {
                LogConcatenationCancelled(outputPath);
                activity?.SetStatus(ActivityStatusCode.Error, "Concatenation cancelled");
                return FfmpegConcatResult.Failed(outputPath,
                    "Concatenation was cancelled; the FFmpeg process was terminated and partial output deleted");
            }

            LogConcatenationTimedOut(outputPath, timeout);
            activity?.SetStatus(ActivityStatusCode.Error, "Concatenation timed out");
            return FfmpegConcatResult.Failed(outputPath,
                $"Concatenation timed out after {timeout} (Ffmpeg:ConcatTimeout); " +
                "the FFmpeg process was terminated and partial output deleted");
        }
        catch (FFMpegException ex)
        {
            sw.Stop();
            LogConcatenationError(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return FfmpegConcatResult.Failed(outputPath, ex.Message,
                string.IsNullOrEmpty(ex.FFMpegErrorOutput) ? null : ex.FFMpegErrorOutput);
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogConcatenationError(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return FfmpegConcatResult.Failed(outputPath, ex.Message);
        }
    }

    /// <summary>
    ///     Runs the ffmpeg concatenation process itself. Uses the concat demuxer
    ///     (<c>-f concat -safe 0 -i list.txt</c>) with stream copy by default
    ///     (<c>-c:v copy -c:a copy</c>), which produces the same lossless output as
    ///     <c>FFMpeg.Join</c> for identical-codec clips without Join's intermediate
    ///     .ts remux files. When <paramref name="encoding" /> carries video/audio
    ///     settings, the corresponding stream is re-encoded instead (see
    ///     <see cref="BuildConcatArguments" />). The token passed here kills the
    ///     ffmpeg process when cancelled (real cancellation, not cooperative).
    ///     Virtual so tests can substitute a fake process.
    /// </summary>
    /// <param name="inputFiles">Absolute paths of the clips to concatenate, in order.</param>
    /// <param name="outputPath">Absolute path of the output file.</param>
    /// <param name="encoding">Optional encode controls; null = lossless stream copy.</param>
    /// <param name="cancellationToken">Token that kills the ffmpeg process when cancelled.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled and the process killed.</exception>
    /// <exception cref="FFMpegException">ffmpeg exited with a non-zero exit code.</exception>
    protected virtual async Task RunConcatenationAsync(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        Cs2EncodingSettings? encoding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await BuildConcatArguments(inputFiles, outputPath, encoding)
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously(throwOnError: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds the ffmpeg invocation for concatenation. Streams without encode settings
    ///     are copied (<c>-c:v copy</c> / <c>-c:a copy</c>); streams with settings are
    ///     re-encoded with the requested codec/quality flags. Extra input/output args are
    ///     appended verbatim (power-user escape hatch, no validation or escaping).
    ///     Internal (not private) so argument construction is unit-testable.
    /// </summary>
    internal static FFMpegArgumentProcessor BuildConcatArguments(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        Cs2EncodingSettings? encoding) =>
        FFMpegArguments
            .FromDemuxConcatInput(inputFiles, inputOptions =>
            {
                if (!string.IsNullOrWhiteSpace(encoding?.ExtraInputArgs))
                {
                    inputOptions.WithCustomArgument(encoding.ExtraInputArgs);
                }
            })
            .OutputToFile(outputPath, overwrite: true, options => ApplyOutputOptions(options, encoding));

    /// <summary>
    ///     Applies output encode options: per-stream re-encode flags when settings are
    ///     present, stream copy otherwise, plus verbatim extra output args.
    /// </summary>
    private static void ApplyOutputOptions(FFMpegArgumentOptions options, Cs2EncodingSettings? encoding)
    {
        // Video stream: re-encode when any video setting is present, else lossless copy.
        if (encoding?.HasVideoSettings == true)
        {
            if (!string.IsNullOrWhiteSpace(encoding.VideoCodec))
            {
                options.WithVideoCodec(encoding.VideoCodec);
            }

            if (encoding.Crf is { } crf)
            {
                options.WithConstantRateFactor(crf);
            }

            if (!string.IsNullOrWhiteSpace(encoding.VideoBitrate))
            {
                options.WithCustomArgument($"-b:v {encoding.VideoBitrate}");
            }
        }
        else
        {
            options.CopyChannel(Channel.Video);
        }

        // Audio stream: re-encode when any audio setting is present, else lossless copy.
        if (encoding?.HasAudioSettings == true)
        {
            if (!string.IsNullOrWhiteSpace(encoding.AudioCodec))
            {
                options.WithAudioCodec(encoding.AudioCodec);
            }

            if (!string.IsNullOrWhiteSpace(encoding.AudioBitrate))
            {
                options.WithCustomArgument($"-b:a {encoding.AudioBitrate}");
            }
        }
        else
        {
            options.CopyChannel(Channel.Audio);
        }

        if (!string.IsNullOrWhiteSpace(encoding?.ExtraOutputArgs))
        {
            options.WithCustomArgument(encoding.ExtraOutputArgs);
        }
    }

    /// <inheritdoc />
    public async Task<FfmpegAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("FfmpegService.CheckAvailability", ActivityKind.Internal);

        try
        {
            // FFMpegCore exposes no GetVersion(), hence the hand-rolled probe.
            string ffmpegPath = ResolveFfmpegPath();

            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using Process? process = Process.Start(psi);
                if (process == null)
                {
                    return FfmpegAvailabilityResult.Failed("Failed to start FFmpeg process");
                }

                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    return FfmpegAvailabilityResult.Failed($"FFmpeg exited with code {process.ExitCode}");
                }

                string? version = ParseVersion(output);

                activity?.SetTag("ffmpeg.available", true);
                activity?.SetTag("ffmpeg.version", version);
                activity?.SetTag("ffmpeg.path", ffmpegPath);

                LogFfmpegAvailable(version ?? "unknown", ffmpegPath);
                return FfmpegAvailabilityResult.Succeeded(version ?? "unknown", ffmpegPath);
            }
            catch (Exception ex)
            {
                return FfmpegAvailabilityResult.Failed($"Failed to execute FFmpeg: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            activity?.SetTag("ffmpeg.available", false);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return FfmpegAvailabilityResult.Failed($"Failed to check FFmpeg availability: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<IImagePipeEncodeSession> StartImagePipeEncodeAsync(
        ImagePipeEncodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using Activity? activity = CsvgTracing.StartActivity("FfmpegService.ImagePipeEncode", ActivityKind.Internal);
        activity?.SetTag("ffmpeg.output.path", request.OutputPath);
        activity?.SetTag("ffmpeg.pipe.format", request.SourceFormat.ToString());

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return FailedImagePipeSession(string.Empty, request.Encoder, "No output path provided");
        }

        string outputPath = Path.GetFullPath(request.OutputPath);

        if (request.FrameRate <= 0)
        {
            return FailedImagePipeSession(outputPath, request.Encoder,
                $"Frame rate must be positive (got {request.FrameRate.ToString(CultureInfo.InvariantCulture)})");
        }

        ImagePipeEncoder encoder = request.Encoder;

        try
        {
            if (encoder == ImagePipeEncoder.Auto)
            {
                FfmpegEncoderProbeResult probe = await ProbeEncodersAsync(cancellationToken).ConfigureAwait(false);
                encoder = ResolveEncoder(ImagePipeEncoder.Auto, probe.Encoders);
                LogEncoderResolved(encoder.ToString(), probe.Success);
            }

            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            IReadOnlyList<string> arguments =
                BuildImagePipeArguments(request with { OutputPath = outputPath }, encoder);

            ProcessStartInfo psi = new()
            {
                FileName = ResolveFfmpegPath(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            Process process = Process.Start(psi) ?? throw new InvalidOperationException(
                "Process.Start returned no process for ffmpeg");

            string? preset = ResolvePreset(encoder, request.EncoderPreset);
            activity?.SetTag("ffmpeg.encoder", encoder.ToString());
            activity?.SetTag("ffmpeg.encoder.preset", preset);
            LogImagePipeStarted(outputPath, encoder.ToString(), preset ?? "(none)", request.FrameRate);

            return new ImagePipeEncodeSession(process, outputPath, encoder, preset, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogImagePipeStartFailed(ex, outputPath);
            return FailedImagePipeSession(outputPath, encoder, $"Failed to start FFmpeg: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<FfmpegEncodeResult> MuxAsync(
        string videoPath,
        string? audioPath,
        string outputPath,
        string audioCodec = "aac",
        int? audioBitrateKbps = null,
        double? audioTempo = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("FfmpegService.Mux", ActivityKind.Internal);
        activity?.SetTag("ffmpeg.output.path", outputPath);
        activity?.SetTag("ffmpeg.mux.audio", audioPath != null);

        Stopwatch sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return FfmpegEncodeResult.Failed(outputPath, $"Video file not found: {videoPath}");
        }

        if (audioPath != null && !File.Exists(audioPath))
        {
            return FfmpegEncodeResult.Failed(outputPath, $"Audio file not found: {audioPath}");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return FfmpegEncodeResult.Failed(string.Empty, "No output path provided");
        }

        videoPath = Path.GetFullPath(videoPath);
        audioPath = audioPath == null ? null : Path.GetFullPath(audioPath);
        outputPath = Path.GetFullPath(outputPath);

        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        TimeSpan timeout = _options.MuxTimeout;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(timeout);
        }

        LogMuxStarting(videoPath, audioPath ?? "(none)", outputPath);

        IReadOnlyList<string> arguments =
            BuildMuxArguments(videoPath, audioPath, outputPath, audioCodec, audioBitrateKbps, audioTempo);

        try
        {
            FfmpegProcessOutcome outcome = await RunFfmpegAsync(
                ResolveFfmpegPath(), arguments, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (outcome.ExitCode != 0)
            {
                DeleteFileQuietly(outputPath);
                activity?.SetStatus(ActivityStatusCode.Error, $"exit code {outcome.ExitCode}");
                return FfmpegEncodeResult.Failed(outputPath,
                    $"FFmpeg exited with code {outcome.ExitCode}", outcome.StandardError, sw.Elapsed);
            }

            if (!File.Exists(outputPath))
            {
                return FfmpegEncodeResult.Failed(outputPath,
                    "FFmpeg completed but output file was not created", outcome.StandardError, sw.Elapsed);
            }

            if (new FileInfo(outputPath).Length == 0)
            {
                DeleteFileQuietly(outputPath);
                return FfmpegEncodeResult.Failed(outputPath,
                    "FFmpeg created an empty output file", outcome.StandardError, sw.Elapsed);
            }

            LogMuxComplete(outputPath, sw.Elapsed);
            return FfmpegEncodeResult.Succeeded(outputPath, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            DeleteFileQuietly(outputPath);

            if (cancellationToken.IsCancellationRequested)
            {
                LogMuxCancelled(outputPath);
                activity?.SetStatus(ActivityStatusCode.Error, "Mux cancelled");
                return FfmpegEncodeResult.Failed(outputPath,
                    "Mux was cancelled; the FFmpeg process was terminated and partial output deleted",
                    duration: sw.Elapsed);
            }

            LogMuxTimedOut(outputPath, timeout);
            activity?.SetStatus(ActivityStatusCode.Error, "Mux timed out");
            return FfmpegEncodeResult.Failed(outputPath,
                $"Mux timed out after {timeout} (Ffmpeg:MuxTimeout); " +
                "the FFmpeg process was terminated and partial output deleted", duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            DeleteFileQuietly(outputPath);
            LogMuxError(ex, outputPath);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return FfmpegEncodeResult.Failed(outputPath, ex.Message, duration: sw.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<FfmpegEncodeResult> RemuxRetimedAsync(
        string videoPath,
        double timestampScale,
        string? audioPath,
        string outputPath,
        string audioCodec = "aac",
        int? audioBitrateKbps = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("FfmpegService.RemuxRetimed", ActivityKind.Internal);
        activity?.SetTag("ffmpeg.output.path", outputPath);
        activity?.SetTag("ffmpeg.retime.scale", timestampScale);

        if (timestampScale <= 0 || !double.IsFinite(timestampScale))
        {
            return FfmpegEncodeResult.Failed(outputPath, $"Timestamp scale must be positive (got {timestampScale})");
        }

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return FfmpegEncodeResult.Failed(outputPath, $"Video file not found: {videoPath}");
        }

        if (audioPath != null && !File.Exists(audioPath))
        {
            return FfmpegEncodeResult.Failed(outputPath, $"Audio file not found: {audioPath}");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return FfmpegEncodeResult.Failed(string.Empty, "No output path provided");
        }

        outputPath = Path.GetFullPath(outputPath);
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        IReadOnlyList<string> arguments = BuildRetimedRemuxArguments(
            Path.GetFullPath(videoPath), timestampScale,
            audioPath == null ? null : Path.GetFullPath(audioPath),
            outputPath, audioCodec, audioBitrateKbps);

        Stopwatch sw = Stopwatch.StartNew();
        TimeSpan timeout = _options.MuxTimeout;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(timeout);
        }

        try
        {
            FfmpegProcessOutcome outcome = await RunFfmpegAsync(
                ResolveFfmpegPath(), arguments, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (outcome.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                DeleteFileQuietly(outputPath);
                activity?.SetStatus(ActivityStatusCode.Error, $"exit code {outcome.ExitCode}");
                return FfmpegEncodeResult.Failed(outputPath,
                    $"FFmpeg retimed remux failed (exit {outcome.ExitCode})", outcome.StandardError, sw.Elapsed);
            }

            return FfmpegEncodeResult.Succeeded(outputPath, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            DeleteFileQuietly(outputPath);
            activity?.SetStatus(ActivityStatusCode.Error, "Retimed remux cancelled or timed out");
            return FfmpegEncodeResult.Failed(outputPath,
                cancellationToken.IsCancellationRequested
                    ? "Retimed remux was cancelled; the FFmpeg process was terminated and partial output deleted"
                    : $"Retimed remux timed out after {timeout} (Ffmpeg:MuxTimeout); partial output deleted",
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            DeleteFileQuietly(outputPath);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return FfmpegEncodeResult.Failed(outputPath, ex.Message, duration: sw.Elapsed);
        }
    }

    /// <summary>
    ///     Builds the argv for the retimed remux: input timestamps scaled (<c>-itsscale</c>, which
    ///     must precede the <c>-i</c> it applies to), video stream copied, audio - when present -
    ///     encoded to the target codec but deliberately not tempo-processed. Internal for the
    ///     argument tests.
    /// </summary>
    internal static IReadOnlyList<string> BuildRetimedRemuxArguments(
        string videoPath,
        double timestampScale,
        string? audioPath,
        string outputPath,
        string audioCodec,
        int? audioBitrateKbps)
    {
        List<string> arguments =
        [
            .. CommonArguments,
            "-itsscale", timestampScale.ToString("0.######", CultureInfo.InvariantCulture),
            "-i", videoPath
        ];

        if (audioPath == null)
        {
            arguments.AddRange(["-an", "-c:v", "copy", outputPath]);
            return arguments;
        }

        arguments.AddRange(["-i", audioPath, "-c:v", "copy", "-c:a", audioCodec]);

        if (audioBitrateKbps is { } kbps)
        {
            arguments.AddRange(["-b:a", $"{kbps.ToString(CultureInfo.InvariantCulture)}k"]);
        }

        arguments.AddRange(["-map", "0:v:0", "-map", "1:a:0", outputPath]);
        return arguments;
    }

    /// <inheritdoc />
    public async Task<FfmpegEncoderProbeResult> ProbeEncodersAsync(CancellationToken cancellationToken = default)
    {
        if (_encoderProbe is { } cached)
        {
            return cached;
        }

        await _encoderProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_encoderProbe is { } raced)
            {
                return raced;
            }

            FfmpegProcessOutcome outcome = await RunFfmpegAsync(
                ResolveFfmpegPath(), ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);

            if (outcome.ExitCode != 0)
            {
                return FfmpegEncoderProbeResult.Failed(
                    $"ffmpeg -encoders exited with code {outcome.ExitCode}: {outcome.StandardError}");
            }

            FfmpegEncoderProbeResult result =
                FfmpegEncoderProbeResult.Succeeded(ParseEncoders(outcome.StandardOutput));
            LogEncodersProbed(result.Encoders.Count, result.Has("h264_nvenc"));

            // Only a successful probe is cached: a transient failure must not poison Auto selection
            // for the lifetime of the process.
            _encoderProbe = result;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FfmpegEncoderProbeResult.Failed("Encoder probe was cancelled");
        }
        catch (Exception ex)
        {
            LogEncoderProbeFailed(ex);
            return FfmpegEncoderProbeResult.Failed($"Failed to probe FFmpeg encoders: {ex.Message}");
        }
        finally
        {
            _encoderProbeGate.Release();
        }
    }

    /// <summary>
    ///     Builds the argv for an image-sequence encode: a frame stream on stdin in a named image
    ///     format, played out at the capture frame rate, with square pixels forced (<c>setsar=1</c>)
    ///     because a frame dump carries no aspect metadata. Formats ffmpeg cannot split on its own
    ///     also get a record length (see <see cref="ImagePipeEncodeRequest.FrameSizeBytes" />).
    ///     Returns argv rather than a command line so staging paths with spaces need no quoting.
    ///     Internal so argument construction is unit-testable without spawning ffmpeg.
    /// </summary>
    /// <param name="request">The encode request; <see cref="ImagePipeEncodeRequest.Encoder" /> is ignored.</param>
    /// <param name="resolvedEncoder">
    ///     The encoder to emit flags for. Must already be resolved - <see cref="ImagePipeEncoder.Auto" />
    ///     is not an encoder.
    /// </param>
    internal static IReadOnlyList<string> BuildImagePipeArguments(
        ImagePipeEncodeRequest request,
        ImagePipeEncoder resolvedEncoder)
    {
        if (resolvedEncoder == ImagePipeEncoder.Auto)
        {
            throw new ArgumentException("Auto must be resolved before building arguments", nameof(resolvedEncoder));
        }

        List<string> arguments;

        if (request.RawVideo is { } raw)
        {
            // Headerless raw frames: the demuxer must be told the geometry, and the source-format
            // decoder and record length have no meaning - the frame size is width x height x bpp.
            arguments =
            [
                .. CommonArguments,
                "-f", "rawvideo",
                "-pix_fmt", raw.PixelFormat,
                "-video_size",
                $"{raw.Width.ToString(CultureInfo.InvariantCulture)}x{raw.Height.ToString(CultureInfo.InvariantCulture)}",
                "-framerate", request.FrameRate.ToString("0.####", CultureInfo.InvariantCulture)
            ];
        }
        else
        {
            arguments =
            [
                .. CommonArguments,
                "-f", "image2pipe",
                "-framerate", request.FrameRate.ToString("0.####", CultureInfo.InvariantCulture),
                "-vcodec", SourceDecoderName(request.SourceFormat)
            ];

            if (request.FrameSizeBytes is { } frameSize)
            {
                arguments.AddRange(["-frame_size", frameSize.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        arguments.AddRange(["-i", "pipe:0", "-vf", "setsar=1"]);

        arguments.AddRange(EncoderArguments(resolvedEncoder, request));

        if (request.ExtraOutputArgs is { Count: > 0 })
        {
            arguments.AddRange(request.ExtraOutputArgs);
        }

        arguments.Add(request.OutputPath);
        return arguments;
    }

    /// <summary>
    ///     Builds the argv for muxing audio onto an encoded video. No <c>-shortest</c> and no duration
    ///     compensation by design - see <see cref="IFfmpegService.MuxAsync" />. A null
    ///     <paramref name="audioPath" /> degrades to a copy-everything remux, which keeps whatever
    ///     streams the video already had.
    /// </summary>
    internal static IReadOnlyList<string> BuildMuxArguments(
        string videoPath,
        string? audioPath,
        string outputPath,
        string audioCodec,
        int? audioBitrateKbps,
        double? audioTempo = null)
    {
        List<string> arguments = [.. CommonArguments, "-i", videoPath];

        if (audioPath == null)
        {
            arguments.AddRange(["-c", "copy", outputPath]);
            return arguments;
        }

        arguments.AddRange(["-i", audioPath, "-c:v", "copy", "-c:a", audioCodec]);

        if (audioBitrateKbps is { } kbps)
        {
            arguments.AddRange(["-b:a", $"{kbps.ToString(CultureInfo.InvariantCulture)}k"]);
        }

        if (AudioTempoFilter(audioTempo) is { } filter)
        {
            arguments.AddRange(["-af", filter]);
        }

        arguments.AddRange(["-map", "0:v:0", "-map", "1:a:0", outputPath]);
        return arguments;
    }

    /// <summary>
    ///     The <c>atempo</c> filter chain for a tempo factor, or null when no filter is needed.
    ///     atempo accepts 0.5-100 per instance, so slower factors chain: 0.25 becomes
    ///     <c>atempo=0.5,atempo=0.5</c>. Internal for the argument tests.
    /// </summary>
    internal static string? AudioTempoFilter(double? tempo)
    {
        if (tempo is not { } factor || Math.Abs(factor - 1.0) < 0.0001)
        {
            return null;
        }

        if (factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tempo), factor, "Audio tempo must be positive");
        }

        List<string> chain = [];
        while (factor < 0.5)
        {
            chain.Add("atempo=0.5");
            factor /= 0.5;
        }

        chain.Add("atempo=" + factor.ToString("0.####", CultureInfo.InvariantCulture));
        return string.Join(',', chain);
    }

    /// <summary>
    ///     Turns <see cref="ImagePipeEncoder.Auto" /> into a concrete encoder given what the local
    ///     ffmpeg build offers: hardware H.264 when it is there, software H.264 otherwise. An
    ///     explicitly requested encoder is returned untouched, so asking for something the build does
    ///     not have fails loudly at encode time rather than being silently substituted.
    /// </summary>
    internal static ImagePipeEncoder ResolveEncoder(
        ImagePipeEncoder requested,
        IReadOnlySet<string> availableEncoders) =>
        requested != ImagePipeEncoder.Auto
            ? requested
            : availableEncoders.Contains(EncoderName(ImagePipeEncoder.Nvenc))
                ? ImagePipeEncoder.Nvenc
                : ImagePipeEncoder.X264;

    /// <summary>
    ///     Parses the encoder names out of <c>ffmpeg -encoders</c> output. Each listing line starts
    ///     with a six-character capability column followed by the encoder name.
    /// </summary>
    internal static IReadOnlySet<string> ParseEncoders(string encodersOutput)
    {
        HashSet<string> encoders = new(StringComparer.Ordinal);

        foreach (Match match in EncoderListingRegex().Matches(encodersOutput))
        {
            encoders.Add(match.Groups["name"].Value);
        }

        return encoders;
    }

    /// <summary>
    ///     The ffmpeg encoder name behind each <see cref="ImagePipeEncoder" /> value, as
    ///     <see cref="FfmpegEncoderProbeResult.Has" /> spells it. Public so diagnostics can ask what a
    ///     configured encoder would resolve to without hard-coding ffmpeg's names.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="encoder" /> is <see cref="ImagePipeEncoder.Auto" />, which names no encoder
    ///     until it has been resolved against a probe.
    /// </exception>
    public static string EncoderName(ImagePipeEncoder encoder) => encoder switch
    {
        ImagePipeEncoder.Nvenc => "h264_nvenc",
        ImagePipeEncoder.X264 => "libx264",
        ImagePipeEncoder.Utvideo => "utvideo",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Not a concrete encoder")
    };

    /// <summary>
    ///     The ffmpeg decoder name for each supported frame-image format, used as the input
    ///     <c>-vcodec</c> so the image2pipe demuxer knows what is arriving.
    /// </summary>
    // Which of these the engine can actually write is unverified - tga is certain, jpg likely,
    // png unknown. The set is a parameter of the request, not a choice made here.
    private static string SourceDecoderName(ImagePipeSourceFormat format) => format switch
    {
        ImagePipeSourceFormat.Targa => "targa",
        ImagePipeSourceFormat.Mjpeg => "mjpeg",
        ImagePipeSourceFormat.Png => "png",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown frame image format")
    };

    /// <summary>
    ///     The preset an encoder will actually be given: the requested one when there is one, the
    ///     encoder's default otherwise, and nothing at all for an encoder that has no such dial.
    ///     A requested preset is passed through unexamined - see
    ///     <see cref="ImagePipeEncodeRequest.EncoderPreset" /> for why the names are ffmpeg's to reject.
    ///     Public for the same reason as <see cref="EncoderName" />: diagnostics report what a capture
    ///     would do, and they must not answer that from a second copy of these defaults.
    /// </summary>
    public static string? ResolvePreset(ImagePipeEncoder encoder, string? requestedPreset)
    {
        if (encoder == ImagePipeEncoder.Utvideo)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedPreset))
        {
            return requestedPreset.Trim();
        }

        return encoder switch
        {
            ImagePipeEncoder.Nvenc => DefaultNvencPreset,
            ImagePipeEncoder.X264 => DefaultX264Preset,
            _ => null
        };
    }

    /// <summary>
    ///     Per-encoder output flags. Quality-targeted encoders take the request's quality and preset
    ///     (or their own defaults); utvideo is lossless and has neither.
    /// </summary>
    private static IReadOnlyList<string> EncoderArguments(ImagePipeEncoder encoder, ImagePipeEncodeRequest request)
    {
        string codec = EncoderName(encoder);
        string? preset = ResolvePreset(encoder, request.EncoderPreset);

        return encoder switch
        {
            // -tune hq asks NVENC for the quality-first rate-control tuning rather than a low-latency
            // one: nothing here is streaming, so latency is not a currency worth spending quality on.
            // Deliberately no -multipass: its two passes exist to spend a bitrate budget well, and
            // -cq is a quality target with no budget to spend, so it would buy encode time and
            // little else.
            ImagePipeEncoder.Nvenc =>
            [
                "-c:v", codec, "-preset", preset!, "-tune", "hq",
                "-cq", (request.Quality ?? DefaultNvencQuality).ToString(CultureInfo.InvariantCulture),
                .. PixelFormatArguments(request.PixelFormat ?? "yuv420p")
            ],
            ImagePipeEncoder.X264 =>
            [
                "-c:v", codec, "-preset", preset!,
                "-crf", (request.Quality ?? DefaultX264Quality).ToString(CultureInfo.InvariantCulture),
                .. PixelFormatArguments(request.PixelFormat ?? "yuv420p")
            ],
            ImagePipeEncoder.Utvideo => ["-c:v", codec, .. PixelFormatArguments(request.PixelFormat)],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Not a concrete encoder")
        };
    }

    private static string[] PixelFormatArguments(string? pixelFormat) =>
        string.IsNullOrWhiteSpace(pixelFormat) ? [] : ["-pix_fmt", pixelFormat];

    /// <summary>
    ///     Runs ffmpeg to completion, draining both output streams so a chatty run cannot deadlock on
    ///     a full pipe buffer. Cancellation kills the whole process tree and surfaces as
    ///     <see cref="OperationCanceledException" />; callers map that to a failed result.
    /// </summary>
    private static async Task<FfmpegProcessOutcome> RunFfmpegAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo psi = new()
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException(
            "Process.Start returned no process for ffmpeg");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process).ConfigureAwait(false);
            throw;
        }

        return new FfmpegProcessOutcome(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    /// <summary>
    ///     Kills a process and everything it spawned, then waits for it to actually go. Ignores the
    ///     races that make either half throw (already exited, exited between the check and the kill)
    ///     and gives up waiting rather than hanging, so no caller can be held up by a zombie ffmpeg.
    /// </summary>
    internal static async Task KillAndReapAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or gone by the time the kill landed.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort reaping.
        }
    }

    /// <summary>
    ///     A session for a request that never got as far as a running ffmpeg. It satisfies the
    ///     never-throw contract: the caller gets the failure through
    ///     <see cref="IImagePipeEncodeSession.Completion" /> like any other, and writes to
    ///     <see cref="IImagePipeEncodeSession.Input" /> go nowhere instead of faulting.
    /// </summary>
    private static IImagePipeEncodeSession FailedImagePipeSession(
        string outputPath,
        ImagePipeEncoder encoder,
        string error) =>
        new FailedImagePipeEncodeSession(encoder, FfmpegEncodeResult.Failed(outputPath, error));

    /// <summary>
    ///     Resolves the ffmpeg executable: the configured binary directory when FFMpegCore has one,
    ///     otherwise the bare name for PATH lookup.
    /// </summary>
    private static string ResolveFfmpegPath() =>
        GlobalFFOptions.Current.BinaryFolder is { Length: > 0 } folder
            ? Path.Combine(folder, GetFfmpegExecutableName())
            : GetFfmpegExecutableName();

    private static string GetFfmpegExecutableName() =>
        OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    [GeneratedRegex(@"^\s*[VAS][F.][S.][X.][B.][D.]\s+(?<name>[A-Za-z0-9_\-.]+)", RegexOptions.Multiline)]
    private static partial Regex EncoderListingRegex();

    private readonly record struct FfmpegProcessOutcome(int ExitCode, string StandardOutput, string StandardError);

    private static string? ParseVersion(string output)
    {
        // Match "ffmpeg version X.Y.Z" or "ffmpeg version N-XXXXX-XXXXX"
        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(output, @"ffmpeg version (\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Debug, Message = "FFmpeg configured with binary directory: {BinaryDirectory}")]
    partial void LogFfmpegConfigured(string binaryDirectory);

    [LoggerMessage(Level = LogLevel.Information, Message = "FFmpeg available: version {Version} at {Path}")]
    partial void LogFfmpegAvailable(string version, string path);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Starting FFmpeg concatenation: {InputCount} files -> {OutputPath}")]
    partial void LogConcatenationStarting(int inputCount, string outputPath);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "FFmpeg concatenation complete: {OutputPath} ({Duration}, {Size} bytes)")]
    partial void LogConcatenationComplete(string outputPath, TimeSpan duration, long size);

    [LoggerMessage(Level = LogLevel.Error, Message = "FFmpeg concatenation error")]
    partial void LogConcatenationError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "FFmpeg concatenation cancelled; killed ffmpeg and deleted partial output {OutputPath}")]
    partial void LogConcatenationCancelled(string outputPath);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "FFmpeg concatenation timed out after {Timeout}; killed ffmpeg and deleted partial output {OutputPath}")]
    partial void LogConcatenationTimedOut(string outputPath, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Starting FFmpeg image pipe encode: {Encoder} preset {Preset} at {FrameRate} fps -> {OutputPath}")]
    partial void LogImagePipeStarted(string outputPath, string encoder, string preset, double frameRate);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to start the FFmpeg image pipe encode for {OutputPath}")]
    partial void LogImagePipeStartFailed(Exception exception, string outputPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved image pipe encoder to {Encoder} (probe ran: {Probed})")]
    partial void LogEncoderResolved(string encoder, bool probed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FFmpeg reports {Count} encoders (NVENC available: {HasNvenc})")]
    partial void LogEncodersProbed(int count, bool hasNvenc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to probe FFmpeg encoders")]
    partial void LogEncoderProbeFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Muxing {VideoPath} + {AudioPath} -> {OutputPath}")]
    partial void LogMuxStarting(string videoPath, string audioPath, string outputPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mux complete: {OutputPath} ({Duration})")]
    partial void LogMuxComplete(string outputPath, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mux failed for {OutputPath}")]
    partial void LogMuxError(Exception exception, string outputPath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Mux cancelled; killed ffmpeg and deleted partial output {OutputPath}")]
    partial void LogMuxCancelled(string outputPath);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Mux timed out after {Timeout}; killed ffmpeg and deleted partial output {OutputPath}")]
    partial void LogMuxTimedOut(string outputPath, TimeSpan timeout);

    #endregion
}

/// <summary>
///     A live <c>image2pipe</c> encode: the caller writes frames to the process's standard input and
///     awaits the outcome. Kept as a plain <see cref="Process" /> rather than an FFMpegCore pipe
///     because FFMpegCore's pipe sources pull from a producer delegate, and offline capture needs the
///     opposite - a stream the frame consumer pushes into for the whole length of a clip.
/// </summary>
internal sealed partial class ImagePipeEncodeSession : IImagePipeEncodeSession
{
    /// <summary>
    ///     How much ffmpeg stderr to keep for diagnostics. Enough for the error and its context,
    ///     bounded so a pathological run cannot grow without limit.
    /// </summary>
    private const int MaxCapturedErrorChars = 8192;

    /// <summary>
    ///     How much of the captured stderr goes into the failure log line. The capture itself is
    ///     already bounded; this bounds again because a log line is read, not scrolled - and ffmpeg
    ///     says why it is giving up at the end of what it wrote, so the tail is the part that matters.
    /// </summary>
    private const int LoggedErrorTailChars = 2000;

    private readonly CancellationTokenSource _cts;
    private readonly StringBuilder _errorOutput = new();
    private readonly ILogger _logger;
    private readonly string _outputPath;
    private readonly Process _process;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _disposed;

    public ImagePipeEncodeSession(
        Process process,
        string outputPath,
        ImagePipeEncoder encoder,
        string? preset,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _process = process;
        _outputPath = outputPath;
        _logger = logger;
        Encoder = encoder;
        Preset = preset;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        process.ErrorDataReceived += OnErrorDataReceived;
        process.OutputDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        Input = process.StandardInput.BaseStream;
        Completion = Task.Run(WaitForEncodeAsync);
    }

    /// <inheritdoc />
    public Stream Input { get; }

    /// <inheritdoc />
    public Task<FfmpegEncodeResult> Completion { get; }

    /// <inheritdoc />
    public ImagePipeEncoder Encoder { get; }

    /// <inheritdoc />
    public string? Preset { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // An undisposed encode is an abandoned one: cancel, which kills the process tree, then wait
        // for the completion task so no ffmpeg outlives the session.
        if (!Completion.IsCompleted)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Completion reports failures as results; anything else is not the disposer's problem.
        }

        _process.ErrorDataReceived -= OnErrorDataReceived;
        _process.Dispose();
        _cts.Dispose();
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null)
        {
            return;
        }

        lock (_errorOutput)
        {
            if (_errorOutput.Length < MaxCapturedErrorChars)
            {
                _errorOutput.AppendLine(e.Data);
            }
        }
    }

    private string CapturedError()
    {
        lock (_errorOutput)
        {
            return _errorOutput.ToString();
        }
    }

    /// <summary>
    ///     Reports a failed encode: the reason and what ffmpeg said about it go in one Error-level log
    ///     line, and the full captured stderr stays on the result. The exit code alone is not a
    ///     diagnosis - ffmpeg's <c>-22</c> for an option this build will not take reads identically to
    ///     <c>-22</c> for a frame it could not decode, and only the stderr distinguishes them.
    /// </summary>
    private FfmpegEncodeResult Failed(string reason, string stderr)
    {
        LogEncodeFailed(_outputPath, Encoder.ToString(), Preset ?? "(none)", reason, ErrorTail(stderr));
        return FfmpegEncodeResult.Failed(_outputPath, reason, stderr, _stopwatch.Elapsed);
    }

    /// <summary>
    ///     The last <see cref="LoggedErrorTailChars" /> characters of ffmpeg's stderr, marked when
    ///     something was cut, or a note that it said nothing at all - which is itself a finding
    ///     (ffmpeg that dies without a word was killed rather than refused).
    /// </summary>
    private static string ErrorTail(string stderr)
    {
        string trimmed = stderr.TrimEnd();

        if (trimmed.Length == 0)
        {
            return "(ffmpeg wrote nothing to stderr)";
        }

        return trimmed.Length <= LoggedErrorTailChars
            ? trimmed
            : "..." + trimmed[^LoggedErrorTailChars..];
    }

    private async Task<FfmpegEncodeResult> WaitForEncodeAsync()
    {
        try
        {
            try
            {
                await _process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await FfmpegService.KillAndReapAsync(_process).ConfigureAwait(false);
                FfmpegService.DeleteFileQuietly(_outputPath);
                LogEncodeCancelled(_outputPath);
                return FfmpegEncodeResult.Failed(_outputPath,
                    "Image pipe encode was cancelled; the FFmpeg process was terminated and partial output deleted",
                    CapturedError(), _stopwatch.Elapsed);
            }

            // Blocking wait with no timeout is the documented way to be sure the asynchronous
            // stderr reader has flushed everything before the captured text is read.
            _process.WaitForExit();
            _stopwatch.Stop();

            string error = CapturedError();

            if (_process.ExitCode != 0)
            {
                FfmpegService.DeleteFileQuietly(_outputPath);
                return Failed($"FFmpeg exited with code {_process.ExitCode}", error);
            }

            if (!File.Exists(_outputPath))
            {
                return Failed("FFmpeg completed but output file was not created", error);
            }

            if (new FileInfo(_outputPath).Length == 0)
            {
                FfmpegService.DeleteFileQuietly(_outputPath);
                return Failed("FFmpeg created an empty output file", error);
            }

            LogEncodeComplete(_outputPath, _stopwatch.Elapsed);
            return FfmpegEncodeResult.Succeeded(_outputPath, _stopwatch.Elapsed, error);
        }
        catch (Exception ex)
        {
            _stopwatch.Stop();
            await FfmpegService.KillAndReapAsync(_process).ConfigureAwait(false);
            FfmpegService.DeleteFileQuietly(_outputPath);
            string error = CapturedError();
            LogEncodeError(ex, _outputPath, ErrorTail(error));
            return FfmpegEncodeResult.Failed(_outputPath, ex.Message, error, _stopwatch.Elapsed);
        }
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Image pipe encode complete: {OutputPath} ({Duration})")]
    partial void LogEncodeComplete(string outputPath, TimeSpan duration);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Image pipe encode failed: {OutputPath} ({Encoder} preset {Preset}): {Reason}. " +
                  "ffmpeg stderr tail:\n{StandardError}")]
    partial void LogEncodeFailed(
        string outputPath,
        string encoder,
        string preset,
        string reason,
        string standardError);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Image pipe encode cancelled; killed ffmpeg and deleted partial output {OutputPath}")]
    partial void LogEncodeCancelled(string outputPath);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Image pipe encode error for {OutputPath}. ffmpeg stderr tail:\n{StandardError}")]
    partial void LogEncodeError(Exception exception, string outputPath, string standardError);

    #endregion
}

/// <summary>
///     Stand-in returned when ffmpeg could not be started at all, so the caller can treat a
///     start failure exactly like an encode failure.
/// </summary>
internal sealed class FailedImagePipeEncodeSession(ImagePipeEncoder encoder, FfmpegEncodeResult result)
    : IImagePipeEncodeSession
{
    /// <inheritdoc />
    public Stream Input { get; } = Stream.Null;

    /// <inheritdoc />
    public Task<FfmpegEncodeResult> Completion { get; } = Task.FromResult(result);

    /// <inheritdoc />
    public ImagePipeEncoder Encoder { get; } = encoder;

    /// <inheritdoc />
    // No ffmpeg ran, so no preset was resolved: reporting one would name a setting that never took
    // effect.
    public string? Preset => null;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
///     Configuration options for FFmpeg.
/// </summary>
public class FfmpegOptions
{
    /// <summary>
    ///     Configuration section name.
    /// </summary>
    public const string SectionName = "Ffmpeg";

    /// <summary>
    ///     Directory containing FFmpeg binaries (ffmpeg, ffprobe).
    ///     If not set, FFMpegCore will look for binaries in PATH.
    /// </summary>
    public string? BinaryDirectory { get; set; }

    /// <summary>
    ///     Maximum time a single concatenation may run before the ffmpeg process is
    ///     killed and the operation reports failure. Default concatenation is a
    ///     stream-copy remux and runs at disk speed, so even multi-hour compilations
    ///     finish well within the 30-minute default; the timeout exists to catch a hung
    ///     ffmpeg (e.g. a corrupt clip), not to bound legitimate work. When re-encoding
    ///     via <see cref="Cs2EncodingSettings" />, ffmpeg runs at encode speed instead —
    ///     raise this timeout for long compilations with slow encoders.
    ///     Set to zero or a negative value to disable the timeout.
    /// </summary>
    public TimeSpan ConcatTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Maximum time a single mux may run before the ffmpeg process is killed and the operation
    ///     reports failure. Muxing copies the video stream and at most re-encodes a short audio
    ///     track, so it runs at disk speed; like <see cref="ConcatTimeout" /> this bound exists to
    ///     catch a hung ffmpeg rather than to limit legitimate work.
    ///     Set to zero or a negative value to disable the timeout.
    /// </summary>
    public TimeSpan MuxTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
