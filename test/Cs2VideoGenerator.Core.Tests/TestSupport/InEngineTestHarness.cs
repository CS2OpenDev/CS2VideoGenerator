#region

using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Core.Tests.TestSupport;

/// <summary>
///     Stands an in-engine capture session up over temporary directories and a fake ffmpeg, so a test
///     can get straight to the clip. Owns the directories and tears them down on dispose.
/// </summary>
public sealed class InEngineTestHarness : IAsyncDisposable
{
    private readonly TempDir _audio = new("csvg-inengine-audio-");
    private readonly TempDir _output = new("csvg-inengine-output-");
    private readonly TempDir _staging = new("csvg-inengine-staging-");

    private InEngineTestHarness(InEngineCaptureConfiguration configuration, FakeFfmpegService ffmpeg,
        InEngineFrameSource frameSource)
    {
        Configuration = configuration;
        Ffmpeg = ffmpeg;
        Engine = new FakeEngineConsole();

        Session = new InEngineCaptureSession(
            configuration,
            new CaptureSessionContext { Engine = Engine },
            ffmpeg,
            () => _staging.Path,
            () => _audio.Path,
            frameSource: frameSource,
            providerName: frameSource == InEngineFrameSource.PresentHook
                ? InEngineHookedCaptureSessionFactory.ProviderKey
                : null);
    }

    /// <summary>Where the engine's frames are expected.</summary>
    public string StagingDirectory => _staging.Path;

    /// <summary>Where the engine's movie audio is expected.</summary>
    public string AudioDirectory => _audio.Path;

    /// <summary>Where finished clips are written.</summary>
    public string OutputDirectory => _output.Path;

    /// <summary>The configuration the session was built with.</summary>
    public InEngineCaptureConfiguration Configuration { get; }

    /// <summary>The fake ffmpeg the session encodes and muxes through.</summary>
    public FakeFfmpegService Ffmpeg { get; }

    /// <summary>The engine view the session was given.</summary>
    public FakeEngineConsole Engine { get; }

    /// <summary>The session under test.</summary>
    public InEngineCaptureSession Session { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        _staging.Dispose();
        _audio.Dispose();
        _output.Dispose();
    }

    /// <summary>
    ///     Builds a harness and initializes its session. The staging and audio directories are supplied
    ///     as the session's defaults rather than as configuration, which is also what proves the lazy
    ///     resolution seam is the one being used.
    /// </summary>
    public static async Task<InEngineTestHarness> CreateAsync(
        InEngineCaptureConfiguration? configuration = null,
        Action<FakeFfmpegService>? configureFfmpeg = null,
        InEngineFrameSource frameSource = InEngineFrameSource.EngineFiles)
    {
        FakeFfmpegService ffmpeg = new();
        configureFfmpeg?.Invoke(ffmpeg);

        // Jpg explicitly, not the production default (Tga): these tests exercise pipeline
        // behavior, and jpg's self-delimiting frames keep them independent of record-length
        // arithmetic. Production defaults are pinned by their own tests.
        InEngineTestHarness harness = new(
            configuration ?? new InEngineCaptureConfiguration
            {
                FrameConsumerPollMs = 20,
                FrameFormat = InEngineFrameFormat.Jpg
            },
            ffmpeg,
            frameSource);

        await harness.Session.InitializeAsync();
        return harness;
    }

    /// <summary>
    ///     A clip context writing into this harness's output directory.
    /// </summary>
    public ClipCaptureContext Clip(
        int startTick = 1000,
        int endTick = 1640,
        int clipIndex = 0,
        int frameRate = 60,
        bool captureAudio = true,
        int? width = null,
        int? height = null) =>
        new()
        {
            StartTick = startTick,
            EndTick = endTick,
            ClipIndex = clipIndex,
            Metadata = new CaptureMetadata
            {
                OutputFileName = $"clip_{clipIndex:D3}",
                OutputDirectory = _output.Path,
                FrameRate = frameRate,
                CaptureAudio = captureAudio,
                Width = width,
                Height = height
            }
        };

    /// <summary>
    ///     Writes <paramref name="count" /> frame files of <paramref name="frameBytes" /> bytes each
    ///     into the staging directory, numbered the way the configuration says the engine numbers them.
    /// </summary>
    public void WriteFrames(string movieName, int count, int frameBytes = 64, int? startNumber = null)
    {
        int first = startNumber ?? Configuration.EngineOutput.FirstFrameNumber;

        for (int i = 0; i < count; i++)
        {
            WriteFrame(movieName, first + i, frameBytes);
        }
    }

    /// <summary>
    ///     The timestamp-shaped subdirectory frames are written into when the configuration says the
    ///     engine uses one. A fixed name rather than the clock so tests are reproducible; the shape
    ///     is what matters, and it is the shape CS2 was observed creating.
    /// </summary>
    public const string TimestampedSubdirectoryName = "2026_07_30_15_44_29";

    /// <summary>
    ///     Writes one frame file, laid out the way the engine lays frames out: inside a
    ///     timestamp-named subdirectory of the staging directory when
    ///     <see cref="InEngineEngineOutput.FramesInTimestampedSubdirectory" /> says so (the observed
    ///     behavior, and the default), flat in the staging directory otherwise. Its contents are the
    ///     frame number repeated, so a test can tell which frames reached the encoder and in what
    ///     order.
    /// </summary>
    public void WriteFrame(string movieName, int frameNumber, int frameBytes = 64)
    {
        string name = movieName +
                      frameNumber.ToString("D" + Configuration.EngineOutput.FrameNumberDigits) +
                      "." + Configuration.FrameExtension();

        string directory = Configuration.EngineOutput.FramesInTimestampedSubdirectory
            ? Path.Combine(_staging.Path, TimestampedSubdirectoryName)
            : _staging.Path;
        Directory.CreateDirectory(directory);

        byte[] contents = new byte[frameBytes];
        Array.Fill(contents, (byte)(frameNumber % 251));
        File.WriteAllBytes(Path.Combine(directory, name), contents);
    }

    /// <summary>
    ///     Writes a wav of the requested length into the audio directory, optionally in a subdirectory
    ///     (the engine is believed to use a timestamped one).
    /// </summary>
    public string WriteWav(string movieName, double seconds, string? subdirectory = null, bool finalized = true)
    {
        string directory = subdirectory is null ? _audio.Path : Path.Combine(_audio.Path, subdirectory);
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, movieName + ".wav");
        WavFixture.Write(path, seconds, finalized);
        return path;
    }
}
