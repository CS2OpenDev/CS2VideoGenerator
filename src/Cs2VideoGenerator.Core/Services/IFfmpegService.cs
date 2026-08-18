#region

using Cs2VideoGenerator.Core.Models;

#endregion

namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Service for FFmpeg video processing operations.
/// </summary>
public interface IFfmpegService
{
    /// <summary>
    ///     Concatenates multiple video files into a single output file.
    ///     Uses the FFmpeg concat demuxer; by default (no <paramref name="encoding" />, or all-null settings)
    ///     the streams are copied losslessly, which is fast when source files have compatible codecs.
    ///     When <paramref name="encoding" /> carries video/audio settings, the corresponding stream(s)
    ///     are re-encoded with those settings (see <see cref="Cs2EncodingSettings" />).
    ///     The operation is bounded by <see cref="FfmpegOptions.ConcatTimeout" />. On cancellation or timeout the
    ///     ffmpeg process is killed, any partial output file is deleted, and a failed result is returned
    ///     (no exception is thrown), so callers keep the individual clips (partial-success contract).
    /// </summary>
    /// <param name="inputFiles">Ordered list of input file paths to concatenate.</param>
    /// <param name="outputPath">Path for the concatenated output file.</param>
    /// <param name="encoding">Optional encode controls for the output; null = lossless stream copy.</param>
    /// <param name="cancellationToken">Cancellation token; cancelling kills the ffmpeg process.</param>
    /// <returns>Result of the concatenation operation.</returns>
    Task<FfmpegConcatResult> ConcatenateAsync(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        Cs2EncodingSettings? encoding = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks if FFmpeg is available and returns version information.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing availability status and version info.</returns>
    Task<FfmpegAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts an encoder that reads a frame-image sequence from its standard input and writes a
    ///     video file. Offline capture dumps one image per simulated frame - far too much data to keep
    ///     on disk - so frames are streamed in and deleted as they go; the returned session is the
    ///     write end of that pipe.
    ///     The call returns as soon as ffmpeg is running: writing to
    ///     <see cref="IImagePipeEncodeSession.Input" /> feeds frames, closing it signals end of stream,
    ///     and <see cref="IImagePipeEncodeSession.Completion" /> then reports how the encode finished.
    ///     Same contract as <see cref="ConcatenateAsync" />: nothing throws, cancelling kills ffmpeg,
    ///     and a partial output file is deleted. A failure to even start ffmpeg is reported the same
    ///     way - the session comes back with an already-failed completion and an input stream that
    ///     discards what is written to it.
    /// </summary>
    /// <param name="request">Frame source format, frame rate, encoder choice and output path.</param>
    /// <param name="cancellationToken">Cancellation token; cancelling kills the ffmpeg process.</param>
    /// <returns>A live encode session. The caller owns it and must dispose it.</returns>
    Task<IImagePipeEncodeSession> StartImagePipeEncodeAsync(
        ImagePipeEncodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Muxes an audio track onto an already-encoded video without touching the video stream
    ///     (<c>-c:v copy</c>). A null <paramref name="audioPath" /> is a plain remux of the video.
    ///     Deliberately emits no <c>-shortest</c> and no duration compensation: offline capture mixes
    ///     audio per simulated frame, so the wav and the video share a timebase and trimming either
    ///     one would introduce the drift it looks like it is fixing.
    ///     Bounded by <see cref="FfmpegOptions.MuxTimeout" />, and carries the same never-throw /
    ///     kill-on-cancel / delete-partial-output contract as <see cref="ConcatenateAsync" />.
    /// </summary>
    /// <param name="videoPath">Path to the encoded video file.</param>
    /// <param name="audioPath">Path to the audio file, or null to remux the video alone.</param>
    /// <param name="outputPath">Path for the muxed output file.</param>
    /// <param name="audioCodec">Audio codec for the muxed track (for example <c>aac</c> or <c>copy</c>).</param>
    /// <param name="audioBitrateKbps">Optional audio bitrate in kbit/s; omitted lets ffmpeg choose.</param>
    /// <param name="audioTempo">
    ///     Optional tempo factor applied to the audio (<c>atempo</c>, pitch-preserving): 0.5 doubles
    ///     its length, 2 halves it. Used by slow-motion capture, where the engine's wav covers game
    ///     time but the video plays it out slower - stretching the audio by the playback speed is
    ///     what keeps the two the same length. Values below atempo's 0.5 floor are applied as a
    ///     filter chain. Null (or 1) applies no filter; requires <paramref name="audioPath" />.
    /// </param>
    /// <param name="cancellationToken">Cancellation token; cancelling kills the ffmpeg process.</param>
    /// <returns>Result of the mux operation.</returns>
    Task<FfmpegEncodeResult> MuxAsync(
        string videoPath,
        string? audioPath,
        string outputPath,
        string audioCodec = "aac",
        int? audioBitrateKbps = null,
        double? audioTempo = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rewrites a video's playback speed by scaling its timestamps (<c>-itsscale</c>) with the
    ///     video stream copied rather than re-encoded, and muxes an audio track onto the result
    ///     untouched.
    ///     This is how a speed capture's normalized real-time copy is made: scaling a 2x clip's
    ///     timestamps by 2 yields the 1x cut losslessly, and the engine's original game-time wav is
    ///     already real-time, so neither stream is degraded. Same never-throw / kill-on-cancel /
    ///     delete-partial-output contract as <see cref="MuxAsync" />.
    /// </summary>
    /// <param name="videoPath">Path to the encoded video file whose timing is rewritten.</param>
    /// <param name="timestampScale">
    ///     Factor applied to the video's timestamps: the clip's playback speed (2 stretches a 2x
    ///     clip to real time). Must be positive.
    /// </param>
    /// <param name="audioPath">Audio to mux on unmodified, or null for a video-only result.</param>
    /// <param name="outputPath">Path for the retimed output file.</param>
    /// <param name="audioCodec">Audio codec for the muxed track.</param>
    /// <param name="audioBitrateKbps">Optional audio bitrate in kbit/s.</param>
    /// <param name="cancellationToken">Cancellation token; cancelling kills the ffmpeg process.</param>
    /// <returns>Result of the remux.</returns>
    /// <remarks>
    ///     A default interface implementation throws <see cref="NotSupportedException" /> so an
    ///     existing custom <see cref="IFfmpegService" /> keeps compiling across this addition; only
    ///     the offline slow-motion / normalized-copy path calls it, and only against the built-in
    ///     service, which overrides it. Override to enable those features on a custom service.
    /// </remarks>
    Task<FfmpegEncodeResult> RemuxRetimedAsync(
        string videoPath,
        double timestampScale,
        string? audioPath,
        string outputPath,
        string audioCodec = "aac",
        int? audioBitrateKbps = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This IFfmpegService does not support retimed remuxing (used by offline slow-motion and " +
            "normalized-copy capture). Override RemuxRetimedAsync to enable it.");

    /// <summary>
    ///     Reports which encoders the local ffmpeg build offers, parsed from <c>ffmpeg -encoders</c>.
    ///     Drives <see cref="ImagePipeEncoder.Auto" /> and the doctor check: whether a machine has
    ///     NVENC is a property of the build and the driver, so it has to be asked rather than assumed.
    ///     A successful probe is cached for the lifetime of the service; a failed one is retried.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The encoder set, or a failed result carrying the reason the probe did not run.</returns>
    Task<FfmpegEncoderProbeResult> ProbeEncodersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Image format of the frames fed into <see cref="IFfmpegService.StartImagePipeEncodeAsync" />,
///     naming the decoder ffmpeg is told to expect on the pipe.
/// </summary>
public enum ImagePipeSourceFormat
{
    /// <summary>Uncompressed Targa (<c>.tga</c>).</summary>
    Targa,

    /// <summary>Baseline JPEG (<c>.jpg</c>).</summary>
    Mjpeg,

    /// <summary>PNG (<c>.png</c>).</summary>
    Png
}

/// <summary>
///     Which encoder the image pipe should hand frames to.
/// </summary>
public enum ImagePipeEncoder
{
    /// <summary>
    ///     Pick the best encoder the local ffmpeg build actually has, via
    ///     <see cref="IFfmpegService.ProbeEncodersAsync" />.
    /// </summary>
    Auto,

    /// <summary>NVIDIA hardware H.264 (<c>h264_nvenc</c>).</summary>
    Nvenc,

    /// <summary>Software H.264 (<c>libx264</c>).</summary>
    X264,

    /// <summary>Lossless intermediate (<c>utvideo</c>), for editing rather than delivery.</summary>
    Utvideo
}

/// <summary>
///     Everything the image-pipe encoder needs to know before the first frame arrives.
/// </summary>
public record ImagePipeEncodeRequest
{
    /// <summary>
    ///     Path of the video file to write. The directory is created if missing and an existing
    ///     file is overwritten.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    ///     Output frame rate. For offline capture this is the simulated frame rate the engine was
    ///     rendering at, so the piped frames are played back at the speed they represent.
    /// </summary>
    public required double FrameRate { get; init; }

    /// <summary>
    ///     Image format of the frames on the pipe. Ignored when <see cref="RawVideo" /> is set - a
    ///     raw stream has no image format, only a pixel layout.
    /// </summary>
    public required ImagePipeSourceFormat SourceFormat { get; init; }

    /// <summary>
    ///     When set, the pipe carries headerless raw frames of exactly
    ///     <c>Width x Height x bytes-per-pixel</c> instead of image files, and ffmpeg reads it with
    ///     the <c>rawvideo</c> demuxer. This is the present-hook capture path: the plugin streams
    ///     pitch-stripped backbuffer rows, so there is no per-frame header for ffmpeg to find and
    ///     the geometry must be declared up front. <see cref="SourceFormat" /> and
    ///     <see cref="FrameSizeBytes" /> are both ignored.
    /// </summary>
    public RawVideoInput? RawVideo { get; init; }

    /// <summary>
    ///     Size of one frame in bytes, for formats ffmpeg cannot split by itself.
    ///     JPEG and PNG carry their own start and end markers, so they need nothing here. Targa does
    ///     not, and ffmpeg has no Targa parser: fed a bare stream of them it reads arbitrary chunks
    ///     and decodes none. Telling it the record length is what makes a Targa pipe work at all,
    ///     and it only holds while every frame is the same size - that is, while the frames are
    ///     uncompressed.
    /// </summary>
    // Unverified for the engine's own output. Confirm that startmovie writes uncompressed
    // Targa (run-length encoding would make frames vary in size and this fixed length wrong), and
    // what the per-frame byte count is for a given resolution.
    public int? FrameSizeBytes { get; init; }

    /// <summary>
    ///     Encoder to use; <see cref="ImagePipeEncoder.Auto" /> resolves against the probed encoder set.
    /// </summary>
    public ImagePipeEncoder Encoder { get; init; } = ImagePipeEncoder.Auto;

    /// <summary>
    ///     Quality level for the quality-targeted encoders: <c>-cq</c> for NVENC, <c>-crf</c> for x264.
    ///     Null uses the encoder's default (<see cref="FfmpegService.DefaultNvencQuality" /> /
    ///     <see cref="FfmpegService.DefaultX264Quality" />). Ignored by lossless encoders.
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>
    ///     Encoder preset - the speed-for-quality dial - or null for the encoder's own default
    ///     (<see cref="FfmpegService.DefaultNvencPreset" /> / <see cref="FfmpegService.DefaultX264Preset" />).
    ///     NVENC spells them <c>p1</c> (fastest) to <c>p7</c> (slowest); x264 spells them
    ///     <c>ultrafast</c> to <c>placebo</c>. Ignored by lossless encoders, which have no such dial.
    /// </summary>
    /// <remarks>
    ///     Deliberately not validated here. The set of accepted names belongs to the local ffmpeg
    ///     build, not to this record, so a name this code has never heard of has to reach ffmpeg -
    ///     which rejects it immediately, loudly, and with its own wording (see the stderr tail on
    ///     <see cref="FfmpegEncodeResult.FfmpegOutput" />). A whitelist here would instead reject
    ///     presets a newer build supports.
    /// </remarks>
    public string? EncoderPreset { get; init; }

    /// <summary>
    ///     Output pixel format. Null uses the encoder's own default, which is <c>yuv420p</c> for the
    ///     H.264 encoders (players are far pickier about 4:4:4 than ffmpeg is) and unset for utvideo.
    /// </summary>
    public string? PixelFormat { get; init; }

    /// <summary>
    ///     Extra output arguments appended verbatim before the output path (power-user escape hatch,
    ///     no validation or escaping). Each element is one argv entry.
    /// </summary>
    public IReadOnlyList<string>? ExtraOutputArgs { get; init; }
}

/// <summary>
///     Geometry and pixel layout of a headerless raw frame stream
///     (<see cref="ImagePipeEncodeRequest.RawVideo" />).
/// </summary>
public record RawVideoInput
{
    /// <summary>
    ///     ffmpeg pixel format name of the stream (for the present hook: <c>rgba</c> - the observed
    ///     swapchain backbuffer layout, 4 bytes per pixel).
    /// </summary>
    public required string PixelFormat { get; init; }

    /// <summary>Frame width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public required int Height { get; init; }
}

/// <summary>
///     A running encoder fed by a stream of frame images. Disposing the session kills ffmpeg if it is
///     still running, so a caller that abandons a capture cannot leave a process behind.
/// </summary>
public interface IImagePipeEncodeSession : IAsyncDisposable
{
    /// <summary>
    ///     The encoder's standard input. Write whole frame images to it in order; close it to signal
    ///     end of stream. Writes block while the encoder is behind, which is the backpressure signal
    ///     the frame producer is expected to feel.
    /// </summary>
    Stream Input { get; }

    /// <summary>
    ///     Completes when ffmpeg exits, was killed, or failed to start. Never faults - failures are
    ///     reported as a failed <see cref="FfmpegEncodeResult" />.
    /// </summary>
    Task<FfmpegEncodeResult> Completion { get; }

    /// <summary>
    ///     The encoder that was actually selected, with <see cref="ImagePipeEncoder.Auto" /> already
    ///     resolved, so callers can record what produced the file.
    /// </summary>
    ImagePipeEncoder Encoder { get; }

    /// <summary>
    ///     The preset ffmpeg was actually given, with <see cref="ImagePipeEncodeRequest.EncoderPreset" />
    ///     null already turned into the encoder's default. Null when the encoder has no preset (utvideo)
    ///     or when ffmpeg never started. Reported alongside the encoder so a capture's speed-for-quality
    ///     trade is in the log and on the console rather than inferred from the settings file.
    /// </summary>
    string? Preset { get; }
}

/// <summary>
///     Result of an FFmpeg encode or mux operation.
/// </summary>
public record FfmpegEncodeResult
{
    /// <summary>
    ///     Whether the operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     Path to the output file.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    ///     Duration of the operation.
    /// </summary>
    public required TimeSpan ProcessingDuration { get; init; }

    /// <summary>
    ///     Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     FFmpeg standard error output (useful for debugging).
    /// </summary>
    public string? FfmpegOutput { get; init; }

    /// <summary>
    ///     Creates a successful result for <paramref name="outputPath" /> with the measured duration
    ///     and optional captured FFmpeg stderr for diagnostics.
    /// </summary>
    public static FfmpegEncodeResult Succeeded(string outputPath, TimeSpan duration, string? ffmpegOutput = null) =>
        new()
        {
            Success = true,
            OutputPath = outputPath,
            ProcessingDuration = duration,
            FfmpegOutput = ffmpegOutput
        };

    /// <summary>
    ///     Creates a failed result carrying the error message and optional captured FFmpeg stderr.
    /// </summary>
    public static FfmpegEncodeResult Failed(
        string outputPath,
        string error,
        string? ffmpegOutput = null,
        TimeSpan duration = default) =>
        new()
        {
            Success = false,
            OutputPath = outputPath,
            ProcessingDuration = duration,
            ErrorMessage = error,
            FfmpegOutput = ffmpegOutput
        };
}

/// <summary>
///     Result of probing the local ffmpeg build for its encoder list.
/// </summary>
public record FfmpegEncoderProbeResult
{
    /// <summary>
    ///     Whether the probe ran. A failed probe carries an empty <see cref="Encoders" /> set, which
    ///     is deliberately not the same thing as an ffmpeg build with no encoders.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     Encoder names as ffmpeg reports them (for example <c>libx264</c>, <c>h264_nvenc</c>).
    /// </summary>
    public required IReadOnlySet<string> Encoders { get; init; }

    /// <summary>
    ///     Error message if the probe failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Whether the local ffmpeg build offers the named encoder.
    /// </summary>
    public bool Has(string encoderName) => Encoders.Contains(encoderName);

    /// <summary>
    ///     Creates a successful probe result over the parsed encoder set.
    /// </summary>
    public static FfmpegEncoderProbeResult Succeeded(IReadOnlySet<string> encoders) =>
        new() { Success = true, Encoders = encoders };

    /// <summary>
    ///     Creates a failed probe result with an empty encoder set.
    /// </summary>
    public static FfmpegEncoderProbeResult Failed(string error) =>
        new() { Success = false, Encoders = new HashSet<string>(StringComparer.Ordinal), ErrorMessage = error };
}

/// <summary>
///     Result of an FFmpeg concatenation operation.
/// </summary>
public record FfmpegConcatResult
{
    /// <summary>
    ///     Whether the concatenation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    ///     Path to the concatenated output file.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    ///     Duration of the concatenation operation.
    /// </summary>
    public required TimeSpan ProcessingDuration { get; init; }

    /// <summary>
    ///     Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     FFmpeg standard error output (useful for debugging).
    /// </summary>
    public string? FfmpegOutput { get; init; }

    /// <summary>
    ///     Creates a successful concat result for <paramref name="outputPath" /> with the
    ///     measured processing duration and optional captured FFmpeg stderr for diagnostics.
    /// </summary>
    public static FfmpegConcatResult Succeeded(string outputPath, TimeSpan duration, string? ffmpegOutput = null) =>
        new()
        {
            Success = true,
            OutputPath = outputPath,
            ProcessingDuration = duration,
            FfmpegOutput = ffmpegOutput
        };

    /// <summary>
    ///     Creates a failed concat result carrying the error message and optional captured
    ///     FFmpeg stderr. <see cref="ProcessingDuration" /> is reported as zero.
    /// </summary>
    public static FfmpegConcatResult Failed(string outputPath, string error, string? ffmpegOutput = null) =>
        new()
        {
            Success = false,
            OutputPath = outputPath,
            ProcessingDuration = TimeSpan.Zero,
            ErrorMessage = error,
            FfmpegOutput = ffmpegOutput
        };
}

/// <summary>
///     Result of checking FFmpeg availability.
/// </summary>
public record FfmpegAvailabilityResult
{
    /// <summary>
    ///     Whether FFmpeg is available.
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    ///     FFmpeg version string if available.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    ///     Path to the FFmpeg executable.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    ///     Error message if FFmpeg is not available.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Creates a successful availability result.
    /// </summary>
    public static FfmpegAvailabilityResult Succeeded(string version, string executablePath) =>
        new() { IsAvailable = true, Version = version, ExecutablePath = executablePath };

    /// <summary>
    ///     Creates a failed availability result.
    /// </summary>
    public static FfmpegAvailabilityResult Failed(string error) =>
        new() { IsAvailable = false, ErrorMessage = error };
}
