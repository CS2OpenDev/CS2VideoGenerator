#region

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     What a wav's RIFF header says about it.
/// </summary>
internal sealed record WavHeader
{
    /// <summary>Channel count from the <c>fmt </c> chunk.</summary>
    public required int Channels { get; init; }

    /// <summary>Sample rate in hertz.</summary>
    public required int SampleRate { get; init; }

    public required int BitsPerSample { get; init; }

    /// <summary>Bytes per second of audio, which is what turns a data size into a duration.</summary>
    public required long AverageBytesPerSecond { get; init; }

    /// <summary>Size of the <c>data</c> chunk as the header declares it.</summary>
    public required long DeclaredDataBytes { get; init; }

    /// <summary>Bytes of the <c>data</c> chunk that are actually present in the file.</summary>
    public required long AvailableDataBytes { get; init; }

    /// <summary>
    ///     Whether the header describes a finished file. An engine writing a wav as it goes cannot
    ///     know the sample count until the end, so it leaves a placeholder in the size fields and
    ///     corrects them when it closes the file: a declared size of zero, or one larger than the
    ///     bytes actually there, means the correction has not happened yet. That is a different state
    ///     from "a very short recording", and reading it as a duration would produce a number wrong
    ///     enough to look like a real defect.
    /// </summary>
    public bool IsFinalized => DeclaredDataBytes > 0 && DeclaredDataBytes <= AvailableDataBytes;

    /// <summary>
    ///     Length of the audio, from the declared data size. Meaningless until
    ///     <see cref="IsFinalized" />.
    /// </summary>
    public TimeSpan Duration => AverageBytesPerSecond > 0
        ? TimeSpan.FromSeconds(DeclaredDataBytes / (double)AverageBytesPerSecond)
        : TimeSpan.Zero;
}

/// <summary>
///     How a wav's length compares with the length the video is supposed to be.
/// </summary>
internal enum AudioDurationVerdict
{
    /// <summary>Within tolerance.</summary>
    Matches,

    /// <summary>The wav is shorter than the video by more than the tolerance.</summary>
    TooShort,

    /// <summary>The wav is longer than the video by more than the tolerance.</summary>
    TooLong
}

/// <summary>
///     The outcome of comparing a wav against the video's game-time length.
/// </summary>
/// <param name="Verdict">Whether the lengths agree.</param>
/// <param name="WavSeconds">The wav's length in seconds.</param>
/// <param name="ExpectedSeconds">The video's length in seconds, frames divided by frame rate.</param>
/// <param name="RelativeError">Signed relative difference, positive when the wav is longer.</param>
internal readonly record struct AudioDurationCheck(
    AudioDurationVerdict Verdict,
    double WavSeconds,
    double ExpectedSeconds,
    double RelativeError);

/// <summary>
///     Finds the wav CS2 leaves behind for a movie, waits for the engine to finish writing its
///     header, and compares its length against the length the frames say the clip should be.
/// </summary>
/// <remarks>
///     Separate from the clip because all three steps are decisions about a file on disk, and each one
///     is a place a wrong assumption about the engine shows up: where the file is, when it is
///     finished, and whether its length means what it should.
/// </remarks>
internal sealed partial class InEngineAudioLocator
{
    private const int MinimumRiffHeaderBytes = 12;
    private const int ChunkHeaderBytes = 8;

    /// <summary>
    ///     Cap on how far into a file the chunk walk will look for <c>data</c>, so a file that is not
    ///     really a wav cannot turn into an unbounded read.
    /// </summary>
    private const int MaxChunksScanned = 64;

    private readonly ILogger _logger;

    /// <summary>
    ///     Creates a locator. A null logger is supported and falls back to a no-op logger.
    /// </summary>
    public InEngineAudioLocator(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <summary>
    ///     Finds the wav a movie produced: the most recently written file matching the configured
    ///     pattern, searched recursively when configured to be. Returns null when the directory does
    ///     not exist or nothing matches.
    /// </summary>
    /// <remarks>
    ///     Newest wins because the search is by movie name and a repeat of the same clip index would
    ///     otherwise be ambiguous. One name per clip is what keeps that from being the common case.
    /// </remarks>
    /// <param name="audioDirectory">Directory the engine writes movie audio into.</param>
    /// <param name="movieName">The movie name, substituted into the configured glob.</param>
    /// <param name="layout">The engine-output settings holding the glob and the recursion flag.</param>
    /// <param name="writtenAfter">
    ///     Rejects anything last written before this instant. A wav older than the clip that is looking
    ///     for it belongs to some earlier run, and muxing it on would produce a file that is wrong in
    ///     the way nobody notices - the audio of a different take, at plausibly the right length.
    ///     Matters because whether the engine writes a wav at all in jpg mode is unconfirmed: a clip
    ///     that got none would otherwise silently inherit the previous one's.
    /// </param>
    public string? FindWav(
        string audioDirectory,
        string movieName,
        InEngineEngineOutput layout,
        DateTime? writtenAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(movieName);
        ArgumentNullException.ThrowIfNull(layout);

        if (!Directory.Exists(audioDirectory))
        {
            LogAudioDirectoryMissing(audioDirectory);
            return null;
        }

        string pattern = string.Format(CultureInfo.InvariantCulture, layout.AudioFileSearchPattern, movieName);
        SearchOption option = layout.AudioSearchRecursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(audioDirectory, pattern, option))
            {
                DateTime written = File.GetLastWriteTimeUtc(candidate);

                if (writtenAfter.HasValue && written < writtenAfter.Value)
                {
                    LogAudioTooOld(candidate, written, writtenAfter.Value);
                    continue;
                }

                if (newest is null || written > newestWrite)
                {
                    newest = candidate;
                    newestWrite = written;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogAudioSearchFailed(audioDirectory, pattern, ex.Message);
            return null;
        }

        if (newest is null)
        {
            LogAudioNotFound(audioDirectory, pattern);
        }

        return newest;
    }

    /// <summary>
    ///     Waits for the engine to correct the wav's chunk sizes after <c>endmovie</c>, then returns
    ///     the finished header. Returns the last header read when the wait runs out, so the caller can
    ///     say what it saw rather than only that it gave up; a null result means the file could not be
    ///     parsed as a wav at all.
    /// </summary>
    /// <remarks>
    ///     Two consecutive reads have to agree before the header counts as finished: the engine writes
    ///     the corrected sizes into a file it has just reopened, and a read that lands mid-write would
    ///     otherwise be taken at face value.
    /// </remarks>
    public async Task<WavHeader?> WaitForFinalizedHeaderAsync(
        string wavPath,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);

        DateTime deadline = DateTime.UtcNow + timeout;
        WavHeader? last = null;
        long stableAt = -1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WavHeader? header = TryReadHeader(wavPath);
            if (header != null)
            {
                last = header;

                if (header.IsFinalized)
                {
                    if (stableAt == header.DeclaredDataBytes)
                    {
                        LogAudioFinalized(wavPath, header.DeclaredDataBytes, header.Duration.TotalSeconds);
                        return header;
                    }

                    stableAt = header.DeclaredDataBytes;
                }
                else
                {
                    stableAt = -1;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                LogAudioNotFinalized(wavPath, timeout.TotalSeconds, last?.DeclaredDataBytes ?? -1);
                return last;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads a wav's header, or returns null when the file is missing, locked, or not a RIFF wav.
    ///     Walks the chunk list rather than assuming <c>data</c> follows <c>fmt </c>, because a file
    ///     may carry <c>LIST</c> or <c>fact</c> chunks between them, and pads odd chunk sizes to even
    ///     as the format requires.
    /// </summary>
    public static WavHeader? TryReadHeader(string wavPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);

        try
        {
            using FileStream stream = new(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadHeader(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Compares a wav's length against the game-time length the frames imply, and reports how far
    ///     apart they are. Pure, so the tolerance behaviour is testable without a file.
    /// </summary>
    public static AudioDurationCheck CheckDuration(
        TimeSpan wavDuration,
        double expectedSeconds,
        double tolerancePercent)
    {
        double wavSeconds = wavDuration.TotalSeconds;

        if (expectedSeconds <= 0)
        {
            return new AudioDurationCheck(AudioDurationVerdict.Matches, wavSeconds, expectedSeconds, 0);
        }

        double relativeError = (wavSeconds - expectedSeconds) / expectedSeconds;
        double tolerance = tolerancePercent / 100.0;

        AudioDurationVerdict verdict = Math.Abs(relativeError) <= tolerance
            ? AudioDurationVerdict.Matches
            : relativeError < 0
                ? AudioDurationVerdict.TooShort
                : AudioDurationVerdict.TooLong;

        return new AudioDurationCheck(verdict, wavSeconds, expectedSeconds, relativeError);
    }

    private static WavHeader? ReadHeader(FileStream stream)
    {
        long fileLength = stream.Length;
        if (fileLength < MinimumRiffHeaderBytes)
        {
            return null;
        }

        Span<byte> riff = stackalloc byte[MinimumRiffHeaderBytes];
        if (!TryFill(stream, riff))
        {
            return null;
        }

        if (!Matches(riff[..4], "RIFF") || !Matches(riff[8..12], "WAVE"))
        {
            return null;
        }

        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        long averageBytesPerSecond = 0;
        long declaredDataBytes = -1;
        long availableDataBytes = 0;

        long position = MinimumRiffHeaderBytes;
        Span<byte> chunkHeader = stackalloc byte[ChunkHeaderBytes];

        for (int scanned = 0; scanned < MaxChunksScanned; scanned++)
        {
            if (position + ChunkHeaderBytes > fileLength)
            {
                break;
            }

            stream.Position = position;
            if (!TryFill(stream, chunkHeader))
            {
                break;
            }

            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            long chunkStart = position + ChunkHeaderBytes;

            if (Matches(chunkHeader[..4], "fmt "))
            {
                if (!TryReadFormat(stream, chunkStart, chunkSize,
                        out channels, out sampleRate, out bitsPerSample, out averageBytesPerSecond))
                {
                    return null;
                }
            }
            else if (Matches(chunkHeader[..4], "data"))
            {
                declaredDataBytes = chunkSize;
                availableDataBytes = Math.Max(0, fileLength - chunkStart);
                break;
            }

            // Chunks are padded to an even length, and the pad byte is not counted in the size.
            long advance = chunkSize + (chunkSize % 2);
            if (advance <= 0)
            {
                break;
            }

            position = chunkStart + advance;
        }

        if (declaredDataBytes < 0 || sampleRate <= 0)
        {
            return null;
        }

        if (averageBytesPerSecond <= 0)
        {
            averageBytesPerSecond = (long)sampleRate * channels * bitsPerSample / 8;
        }

        return new WavHeader
        {
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample,
            AverageBytesPerSecond = averageBytesPerSecond,
            DeclaredDataBytes = declaredDataBytes,
            AvailableDataBytes = availableDataBytes
        };
    }

    private static bool TryReadFormat(
        FileStream stream,
        long chunkStart,
        uint chunkSize,
        out int channels,
        out int sampleRate,
        out int bitsPerSample,
        out long averageBytesPerSecond)
    {
        channels = 0;
        sampleRate = 0;
        bitsPerSample = 0;
        averageBytesPerSecond = 0;

        // wFormatTag, nChannels, nSamplesPerSec, nAvgBytesPerSec, nBlockAlign, wBitsPerSample.
        const int minimumFormatBytes = 16;
        if (chunkSize < minimumFormatBytes)
        {
            return false;
        }

        Span<byte> format = stackalloc byte[minimumFormatBytes];
        stream.Position = chunkStart;
        if (!TryFill(stream, format))
        {
            return false;
        }

        channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        averageBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        return true;
    }

    private static bool TryFill(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer[read..]);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, string ascii) =>
        Encoding.ASCII.GetString(bytes).Equals(ascii, StringComparison.Ordinal);

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Movie audio directory does not exist: {Directory}")]
    partial void LogAudioDirectoryMissing(string directory);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "No movie audio matching '{Pattern}' under {Directory}")]
    partial void LogAudioNotFound(string directory, string pattern);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Could not search {Directory} for movie audio matching '{Pattern}': {Error}")]
    partial void LogAudioSearchFailed(string directory, string pattern, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message =
            "Ignoring {Path} as movie audio: it was last written at {Written:O}, before this clip started " +
            "recording at {Threshold:O}, so it belongs to an earlier run")]
    partial void LogAudioTooOld(string path, DateTime written, DateTime threshold);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "Movie audio finalized: {Path} ({DataBytes} bytes, {Seconds:F3} s)")]
    partial void LogAudioFinalized(string path, long dataBytes, double seconds);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message =
            "The engine did not finish writing {Path}'s RIFF header within {Seconds:F1} s (declared data size " +
            "{DataBytes}); its reported length may be a placeholder rather than the real one")]
    partial void LogAudioNotFinalized(string path, double seconds, long dataBytes);

    #endregion
}
