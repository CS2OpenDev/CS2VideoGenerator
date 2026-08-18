#region

using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Services;

#endregion

namespace Cs2VideoGenerator.Core.Tests.TestSupport;

/// <summary>
///     A stand-in for <see cref="IFfmpegService" /> that behaves like the real one from the outside -
///     the image pipe accepts a stream of frames and writes them to a file, the mux produces an output
///     file - without spawning anything. The frames land where they can be read back, so a test can
///     assert what actually reached the encoder rather than only that something did.
/// </summary>
public sealed class FakeFfmpegService : IFfmpegService
{
    /// <summary>Every request passed to <see cref="StartImagePipeEncodeAsync" />, in order.</summary>
    public List<ImagePipeEncodeRequest> EncodeRequests { get; } = [];

    /// <summary>The encode sessions handed out so far, in order.</summary>
    public List<FakeImagePipeEncodeSession> EncodeSessions { get; } = [];

    /// <summary>Every mux call, in order.</summary>
    public List<MuxCall> MuxCalls { get; } = [];

    /// <summary>The encoder names <see cref="ProbeEncodersAsync" /> reports.</summary>
    public HashSet<string> Encoders { get; } = new(StringComparer.Ordinal) { "libx264" };

    /// <summary>Whether the encoder probe succeeds.</summary>
    public bool ProbeSucceeds { get; set; } = true;

    /// <summary>Whether <see cref="CheckAvailabilityAsync" /> reports ffmpeg as present.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>When set, every mux fails with this message.</summary>
    public string? MuxFailure { get; set; }

    /// <summary>When set, the image pipe comes back already failed with this message.</summary>
    public string? EncodeStartFailure { get; set; }

    /// <summary>The encoder the fake reports having resolved to.</summary>
    public ImagePipeEncoder ResolvedEncoder { get; set; } = ImagePipeEncoder.X264;

    /// <summary>The last encode session handed out, or null when none has been.</summary>
    public FakeImagePipeEncodeSession? LastEncode => EncodeSessions.Count > 0 ? EncodeSessions[^1] : null;

    /// <inheritdoc />
    public Task<FfmpegConcatResult> ConcatenateAsync(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        Cs2EncodingSettings? encoding = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A capture session never concatenates.");

    /// <inheritdoc />
    public Task<FfmpegAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAvailable
            ? FfmpegAvailabilityResult.Succeeded("fake-7.1", "/fake/ffmpeg")
            : FfmpegAvailabilityResult.Failed("ffmpeg was not found on PATH"));

    /// <inheritdoc />
    public Task<IImagePipeEncodeSession> StartImagePipeEncodeAsync(
        ImagePipeEncodeRequest request,
        CancellationToken cancellationToken = default)
    {
        EncodeRequests.Add(request);

        // Resolved the way the real service resolves it, so a test can see the preset a clip's
        // configuration actually produced rather than the one it asked for.
        FakeImagePipeEncodeSession session = new(request.OutputPath, ResolvedEncoder,
            FfmpegService.ResolvePreset(ResolvedEncoder, request.EncoderPreset), EncodeStartFailure);
        EncodeSessions.Add(session);
        return Task.FromResult<IImagePipeEncodeSession>(session);
    }

    /// <inheritdoc />
    public Task<FfmpegEncodeResult> MuxAsync(
        string videoPath,
        string? audioPath,
        string outputPath,
        string audioCodec = "aac",
        int? audioBitrateKbps = null,
        double? audioTempo = null,
        CancellationToken cancellationToken = default)
    {
        MuxCalls.Add(new MuxCall(videoPath, audioPath, outputPath, audioCodec, audioBitrateKbps, audioTempo));

        if (MuxFailure != null)
        {
            return Task.FromResult(FfmpegEncodeResult.Failed(outputPath, MuxFailure));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(videoPath, outputPath, true);

        return Task.FromResult(FfmpegEncodeResult.Succeeded(outputPath, TimeSpan.FromMilliseconds(1)));
    }

    /// <inheritdoc />
    public Task<FfmpegEncoderProbeResult> ProbeEncodersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProbeSucceeds
            ? FfmpegEncoderProbeResult.Succeeded(Encoders)
            : FfmpegEncoderProbeResult.Failed("ffmpeg could not be run"));

    /// <inheritdoc />
    public Task<FfmpegEncodeResult> RemuxRetimedAsync(
        string videoPath,
        double timestampScale,
        string? audioPath,
        string outputPath,
        string audioCodec = "aac",
        int? audioBitrateKbps = null,
        CancellationToken cancellationToken = default)
    {
        RetimeCalls.Add(new RetimeCall(videoPath, timestampScale, audioPath, outputPath, audioCodec));

        if (RetimeFailure != null)
        {
            return Task.FromResult(FfmpegEncodeResult.Failed(outputPath, RetimeFailure));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(videoPath, outputPath, true);

        return Task.FromResult(FfmpegEncodeResult.Succeeded(outputPath, TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>When set, <see cref="RemuxRetimedAsync" /> fails with this message.</summary>
    public string? RetimeFailure { get; set; }

    /// <summary>Every call to <see cref="RemuxRetimedAsync" />, in order.</summary>
    public List<RetimeCall> RetimeCalls { get; } = [];

    /// <summary>One call to <see cref="RemuxRetimedAsync" />.</summary>
    public sealed record RetimeCall(
        string VideoPath,
        double TimestampScale,
        string? AudioPath,
        string OutputPath,
        string AudioCodec);

    /// <summary>One call to <see cref="MuxAsync" />.</summary>
    public sealed record MuxCall(
        string VideoPath,
        string? AudioPath,
        string OutputPath,
        string AudioCodec,
        int? AudioBitrateKbps,
        double? AudioTempo = null);
}

/// <summary>
///     An image-pipe session that writes whatever is piped into it to a file and completes when the
///     pipe is closed - the same contract the real one has, and the same signal (a closed stdin) that
///     ends a real encode.
/// </summary>
public sealed class FakeImagePipeEncodeSession : IImagePipeEncodeSession
{
    private readonly TaskCompletionSource<FfmpegEncodeResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly string _outputPath;
    private int _disposed;
    private byte[] _piped = [];

    internal FakeImagePipeEncodeSession(
        string outputPath,
        ImagePipeEncoder encoder,
        string? preset,
        string? startFailure)
    {
        _outputPath = outputPath;
        Encoder = encoder;
        Preset = preset;

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (startFailure != null)
        {
            Input = Stream.Null;
            _completion.TrySetResult(FfmpegEncodeResult.Failed(outputPath, startFailure));
            return;
        }

        FileStream file = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        Input = new CallbackOnCloseStream(file, () =>
        {
            // Snapshot before completing: the clip deletes its intermediate once the encode is done,
            // so reading the file afterwards would say the encoder received nothing.
            _piped = File.Exists(outputPath) ? File.ReadAllBytes(outputPath) : [];
            _completion.TrySetResult(FfmpegEncodeResult.Succeeded(outputPath, TimeSpan.FromMilliseconds(1)));
        });
    }

    /// <summary>The bytes piped in, captured when the pipe was closed.</summary>
    public byte[] WrittenBytes => _piped;

    /// <summary>Whether the session has been disposed, which for a real one means ffmpeg is gone.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public Stream Input { get; }

    /// <inheritdoc />
    public Task<FfmpegEncodeResult> Completion => _completion.Task;

    /// <inheritdoc />
    public ImagePipeEncoder Encoder { get; }

    /// <inheritdoc />
    public string? Preset { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Input.Dispose();
            _completion.TrySetResult(FfmpegEncodeResult.Failed(_outputPath, "The encode session was disposed"));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Makes the encoder die while the clip is still recording, the way a real ffmpeg that ran out
    ///     of disk would.
    /// </summary>
    public void FailNow(string error) =>
        _completion.TrySetResult(FfmpegEncodeResult.Failed(_outputPath, error));
}

/// <summary>
///     Wraps a stream and runs a callback once, when it is closed. The frame consumer closes the sink
///     to signal end of sequence, and this is how that signal becomes a completed encode.
/// </summary>
internal sealed class CallbackOnCloseStream(Stream inner, Action onClose) : Stream
{
    private int _closed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            inner.Dispose();
            onClose();
        }

        base.Dispose(disposing);
    }
}
