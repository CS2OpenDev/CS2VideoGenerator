#region

using System.Globalization;
using Cs2VideoGenerator.Core.Services;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Image format the engine is asked to dump frames in.
/// </summary>
public enum InEngineFrameFormat
{
    /// <summary>
    ///     JPEG. Self-delimiting, so ffmpeg can split the pipe on its own, and roughly a fiftieth of
    ///     the disk bandwidth Targa needs. Lossy, and the compression happens inside CS2 where its
    ///     quality is at best a cvar away.
    /// </summary>
    Jpg,

    /// <summary>
    ///     Targa. Lossless, and enormous: 1080p is 8.3 MB per frame (32-bit, observed), about
    ///     500 MB/s at 60 frames per second. Needs
    ///     <see cref="InEngineEngineOutput.TgaFrameSizeBytes" /> because ffmpeg has no Targa parser
    ///     and can only split the pipe at a fixed record length.
    /// </summary>
    Tga
}

/// <summary>
///     Where a clip's frames come from.
/// </summary>
public enum InEngineFrameSource
{
    /// <summary>
    ///     <c>startmovie</c> writes numbered frame files (tga/jpg) into the staging directory and
    ///     the consumer streams them into ffmpeg. Works everywhere, costs a synchronous GPU readback
    ///     per frame (~8 ms at 1080p, observed).
    /// </summary>
    EngineFiles,

    /// <summary>
    ///     <c>startmovie &lt;name&gt; wav</c> runs the engine's audio-only recording mode (fixed
    ///     timestep, game-time wav, no frame files, observed on build 2000879) while the CSVG
    ///     plugin's DXGI present hook copies each backbuffer through a delayed-map staging ring and
    ///     streams raw RGBA over a named pipe. Same clock, same audio, no readback stall. Windows
    ///     only; requires a plugin advertising <see cref="CsvgCapabilities.PresentCapture" />.
    /// </summary>
    PresentHook
}

/// <summary>
///     What to do when the engine did not leave a wav behind for a clip that asked for audio.
/// </summary>
public enum MissingAudioBehavior
{
    /// <summary>
    ///     Fail the clip. The default: a clip that asked for audio and has none does not meet the
    ///     requested spec, and silently shipping a mute video hides that until someone watches it.
    ///     Audio is confirmed working on build 2000879 (the wav token writes
    ///     csgo/movie/&lt;timestamp&gt;/&lt;name&gt;.wav), so a missing wav is a real problem.
    /// </summary>
    Fail,

    /// <summary>
    ///     Keep the video, without an audio track, and report a warning on the result.
    /// </summary>
    WarnAndContinue
}

/// <summary>
///     The shape of what CS2's <c>startmovie</c> leaves on disk.
/// </summary>
/// <remarks>
///     <para>
///         Everything in this section is a value rather than something the provider infers. The
///         defaults reflect what CS2 build 2000879 was observed doing (frames, numbering, format
///         tokens, Targa mode, audio placement); the one property still marked unverified is the
///         focus-sleep restore value. Each property carries what is known and what goes wrong when
///         the value is wrong.
///     </para>
///     <para>
///         Correcting reality is then an edit to configuration, not a change to the provider, the
///         frame consumer or the audio pipeline.
///     </para>
/// </remarks>
public record InEngineEngineOutput
{
    /// <summary>
    ///     Text prepended to the movie name in the <c>startmovie</c> argument, for the case where the
    ///     engine interprets the name as a path relative to somewhere other than the directory the
    ///     frames are expected in. Empty means the movie name is passed as-is.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): no prefix needed. "startmovie csvg_000 jpg" wrote
    // to game/csgo/csvg/movie/<timestamp>/, the movie root under the first writable gameinfo search
    // path, which the CSVG gameinfo patch makes the plugin directory. The knob stays for engine
    // changes; see FramesInTimestampedSubdirectory for the <timestamp> part.
    public string MovieNamePrefix { get; init; } = "";

    /// <summary>
    ///     Digits in the zero-padded frame number the engine writes.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): 8 digits, appended directly to the movie name with
    // no separator (engine2.dll's own pattern string is "%s%08llu.png"; the live files were
    // csvg_00000000000.jpg, csvg_00000000001.jpg, ...). Wrong value = the glob matches nothing and
    // the clip fails with no frames.
    public int FrameNumberDigits { get; init; } = 8;

    /// <summary>
    ///     The number the engine gives the first frame of a movie.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): numbering starts at 0. A different start is
    // survivable regardless: the consumer adopts the first number it actually sees and logs the
    // disagreement rather than counting a run of dropped frames.
    public int FirstFrameNumber { get; init; }

    /// <summary>
    ///     Whether the engine writes each movie's files into a timestamped subdirectory of the
    ///     staging directory (<c>movie/2026_07_30_15_44_29/csvg_000...</c>) rather than into the
    ///     staging directory itself. When set, the frame consumer scans those subdirectories as well
    ///     as the staging root, so either layout is consumed.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): the engine creates one
    // <yyyy_MM_dd_HH_mm_ss>-named subdirectory per startmovie (its format string is
    // "movie/%04d_%02d_%02d_%02d_%02d_%02d/%s") and writes every frame there. This is the value
    // whose old default (frames expected flat in the staging root) made every real-CS2 capture fail
    // with zero frames consumed.
    public bool FramesInTimestampedSubdirectory { get; init; } = true;

    /// <summary>
    ///     Size of one uncompressed Targa frame in bytes, which is what lets ffmpeg split a Targa pipe
    ///     into frames at all. Required when <see cref="InEngineCaptureConfiguration.FrameFormat" />
    ///     is <see cref="InEngineFrameFormat.Tga" />; ignored otherwise.
    /// </summary>
    /// <remarks>
    ///     Deliberately not derived from <see cref="CaptureMetadata.Width" /> and
    ///     <see cref="CaptureMetadata.Height" />: those describe the output that was asked for and are
    ///     optional, while this has to be the byte count the engine's renderer actually writes.
    ///     <see cref="InEngineCaptureConfiguration.UncompressedTgaFrameSize" /> computes the value for
    ///     a known resolution.
    /// </remarks>
    // Observed on CS2 build 2000879 (2026-07-30): startmovie writes uncompressed 32-bit Targa.
    // Every live frame was byte-identical at 8,294,418 bytes for 1920x1080 (18 + w x h x 4), so a
    // fixed record length is valid. The value stays mandatory and the frame consumer still checks
    // every frame against it, because run-length encoding on a future build would make frames vary
    // and silently corrupt the pipe - the per-frame check is what turns that into a loud failure.
    public int? TgaFrameSizeBytes { get; init; }

    /// <summary>
    ///     The format token passed as <c>startmovie &lt;name&gt; &lt;token&gt;</c> for Targa output.
    ///     Bare <c>startmovie &lt;name&gt;</c> is not Targa mode: observed on build 2000879, a
    ///     token-less startmovie reported "Started recording movie: ...: framerate 60.000000" with
    ///     no format word and wrote no image files at all (the engine's default appears to be its
    ///     AVI/video path), so the token is mandatory for a frame dump.
    /// </summary>
    public string TgaFormatToken { get; init; } = "tga";

    /// <summary>
    ///     The format token passed as <c>startmovie &lt;name&gt; &lt;token&gt;</c> for JPEG output.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): the token exists and works - the console replied
    // "Started recording movie: ...: jpg framerate 60.000000 quality 50". The engine's full token
    // list (engine2.dll strings): tga png jpg jpeg jpeg_quality wav avi avisound viewport nocodec
    // height width quality framerate quit. Wrong value = startmovie is rejected and no frames appear.
    public string JpgFormatToken { get; init; } = "jpg";

    /// <summary>
    ///     The token that asks <c>startmovie</c> to record audio alongside the frames. Appended when
    ///     the clip's metadata asks for audio; blank suppresses it, which means no clip gets a wav.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): audio is opt-in and works. A startmovie without
    // the token produced no wav anywhere, and with it the engine wrote
    // csgo/movie/<timestamp>/<name>.wav at game-time length (9.358 s for a 9.367 s clip). Note the
    // root differs from the frames' (see DefaultAudioSubdirectory).
    public string AudioToken { get; init; } = "wav";

    /// <summary>
    ///     The startmovie tokens that put the engine in its audio-only recording mode, used by
    ///     <see cref="InEngineFrameSource.PresentHook" />: fixed-timestep pacing and the game-time
    ///     wav with no frame files, leaving the presents free for the plugin's hook to capture.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): "startmovie <name> wav nocodec" wrote a
    // full-length game-time wav and zero frame files at 5.4x realtime; "wav viewport" behaved
    // identically, so `wav` as the leading token is the mode selector. This exact combination is
    // the tested one - a bare "wav" is expected to behave the same but was not probed. The wav is
    // always recorded in this mode (it is the pacing anchor); a no-audio clip's wav is discarded
    // after the fact.
    public string AudioOnlyTokens { get; init; } = "wav nocodec";

    /// <summary>
    ///     The <c>startmovie</c> argument that sets JPEG quality, passed as
    ///     <c>jpeg_quality &lt;value&gt;</c> on the startmovie line when
    ///     <see cref="InEngineCaptureConfiguration.JpegQuality" /> is set. Blank suppresses it even
    ///     when a quality is configured.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): quality is a startmovie argument, not a cvar; the
    // engine's default is 50 ("...: jpg framerate 60.000000 quality 50"). The parser's token table
    // spells it jpeg_quality; the usage example carries Valve's typo "jpeg_qality", which has not
    // been tested and is assumed to be example-text only.
    public string JpegQualityArgument { get; init; } = "jpeg_quality";

    /// <summary>
    ///     Value <c>engine_no_focus_sleep</c> is restored to at the end of a clip. Offline rendering
    ///     sets it to 0 so an unfocused window still renders.
    /// </summary>
    // Unverified. Confirm the engine's own default. Wrong value leaves the user's CS2 with a
    // different throttle than it started with - harmless to the capture, visible afterwards.
    public int FocusSleepRestoreValue { get; init; } = 1;

    /// <summary>
    ///     Glob matching the wav the engine writes for a movie, with <c>{0}</c> standing in for the
    ///     movie name.
    /// </summary>
    // Observed on CS2 build 2000879: the wav is named exactly <name>.wav and lands in a timestamped
    // subdirectory of the audio directory (which is why the search is recursive). Wrong value = the
    // wav is not found and the clip takes the MissingAudioBehavior path.
    public string AudioFileSearchPattern { get; init; } = "{0}*.wav";

    /// <summary>
    ///     Whether the wav search descends into subdirectories of the audio directory.
    /// </summary>
    // Observed on CS2 build 2000879: the engine writes into a timestamped subdirectory.
    public bool AudioSearchRecursive { get; init; } = true;
}

/// <summary>
///     Settings for the in-engine capture provider, bound from <c>Capture:InEngine</c>.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not derived from <see cref="CaptureConfiguration" />: this backend launches no
///         executable and connects to nothing, so the executable path, auto-start and connection
///         timeout on the base record would all be dead surface.
///     </para>
///     <para>
///         The frame rate is deliberately absent. It arrives per clip on
///         <see cref="CaptureMetadata.FrameRate" />, because it is the property of the requested output
///         that this backend, unlike a screen recorder, can actually honor.
///     </para>
/// </remarks>
public record InEngineCaptureConfiguration
{
    /// <summary>
    ///     Bytes of header on an uncompressed Targa frame.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): the plain 18-byte header, no image ID, no color
    // map, no Targa 2.0 footer - the arithmetic 18 + width x height x 4 matched a live frame
    // exactly. The frame consumer's per-frame check still guards a future change.
    public const int TgaHeaderBytes = 18;

    /// <summary>
    ///     Bits per pixel of an uncompressed Targa frame.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): 32-bit - a live 1920x1080 frame was exactly
    // 8,294,418 bytes = 18 + 1920 x 1080 x 4, which also confirms the plain 18-byte header. Every
    // observed frame was byte-identical in size, so the output is uncompressed and a fixed record
    // length is valid.
    public const int TgaBitsPerPixel = 32;

    /// <summary>
    ///     The ffmpeg pixel format of the frames the plugin's present hook streams.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): the swapchain backbuffer is
    // DXGI_FORMAT_R8G8B8A8_UNORM - RGBA, not the BGRA Targa uses - non-multisampled, at native
    // window resolution, post-HUD. The plugin sends its rows pitch-stripped, 4 bytes per pixel.
    public const string PresentHookPixelFormat = "rgba";

    /// <summary>
    ///     Format of the movie name handed to <c>startmovie</c>, by clip index. One name per clip
    ///     keeps a clip's frames and its wav from being confused with the previous clip's.
    /// </summary>
    public const string MovieNameFormat = "csvg_{0:D3}";

    /// <summary>
    ///     Directory under the CS2 plugin directory that frames are expected in, when
    ///     <see cref="StagingDirectory" /> does not override it.
    /// </summary>
    public const string DefaultStagingSubdirectory = "movie";

    /// <summary>
    ///     Directory under the CS2 game files directory that movie audio is expected in, when
    ///     <see cref="AudioDirectory" /> does not override it.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): audio and frames use different roots. Frames go
    // under the csvg search path (csvg/movie/<timestamp>/); the wav lands at
    // csgo/movie/<timestamp>/<name>.wav - the game files directory, which is what this default
    // resolves against. AudioSearchRecursive covers the timestamped subdirectory.
    public const string DefaultAudioSubdirectory = "movie";

    /// <summary>
    ///     Image format the engine dumps frames in.
    /// </summary>
    /// <remarks>
    ///     Targa by default: lossless, and on the machines measured it also renders faster than jpg
    ///     (the engine's CPU-side JPEG encoder costs more than Targa's disk bandwidth on an NVMe).
    ///     The record length ffmpeg needs is computed from the clip's width and height when
    ///     <see cref="InEngineEngineOutput.TgaFrameSizeBytes" /> is not set. The 32-bit
    ///     uncompressed arithmetic is an observed fact, and the per-frame record check turns any
    ///     future disagreement into a loud failure. Jpg trades quality for about a fiftieth of the
    ///     disk bandwidth.
    /// </remarks>
    public InEngineFrameFormat FrameFormat { get; init; } = InEngineFrameFormat.Tga;

    /// <summary>
    ///     JPEG quality passed on the <c>startmovie</c> line, or null for the engine's own default.
    ///     Only used for <see cref="InEngineFrameFormat.Jpg" />, and only when
    ///     <see cref="InEngineEngineOutput.JpegQualityArgument" /> is non-blank.
    /// </summary>
    /// <remarks>
    ///     Defaults to 90 rather than null because the engine's own default is 50 (observed), which
    ///     is visibly lossy for an intermediate that gets re-encoded at least once more. Set null to
    ///     take whatever the engine does.
    /// </remarks>
    public int? JpegQuality { get; init; } = 90;

    /// <summary>
    ///     Encoder the frame pipe hands frames to. <see cref="ImagePipeEncoder.Auto" /> picks the best
    ///     one the local ffmpeg build actually offers.
    /// </summary>
    public ImagePipeEncoder IntermediateEncoder { get; init; } = ImagePipeEncoder.Auto;

    /// <summary>
    ///     Quality for the intermediate encode (<c>-cq</c> for NVENC, <c>-crf</c> for x264), or null
    ///     for the encoder's default. Near-lossless by default: this file is an intermediate for a
    ///     compilation, and for a single-clip capture it is the delivered video, because nothing
    ///     re-encodes after it.
    /// </summary>
    public int? IntermediateQuality { get; init; } = 16;

    /// <summary>
    ///     Preset for the intermediate encode - the speed-for-quality dial - or null for the encoder's
    ///     default (<c>p7</c> for NVENC, <c>veryfast</c> for x264). NVENC takes <c>p1</c>-<c>p7</c>,
    ///     x264 takes <c>ultrafast</c>-<c>placebo</c>; utvideo is lossless and ignores it.
    /// </summary>
    /// <remarks>
    ///     Worth spending wall clock on, because there is wall clock to spend: the render and the
    ///     encoder are decoupled by the frame pile in staging, so a slower preset never drops a frame
    ///     or changes the output's length - it only makes the capture take longer. The name is not
    ///     validated here; ffmpeg rejects one it does not know, immediately and by name.
    /// </remarks>
    public string? IntermediateEncoderPreset { get; init; }

    /// <summary>
    ///     Container for the intermediate video the frame pipe writes, before audio is muxed on.
    /// </summary>
    public string IntermediateContainer { get; init; } = "mkv";

    /// <summary>
    ///     Where the engine's frames are expected to appear, or null to use
    ///     <c>&lt;cs2&gt;/game/csgo/csvg/movie</c>.
    /// </summary>
    /// <remarks>
    ///     Resolved lazily, when a session is initialized rather than when the provider is registered:
    ///     working out the CS2 directory throws on a machine with no CS2 installed, and a library host
    ///     that never captures anything must not be made to care.
    /// </remarks>
    public string? StagingDirectory { get; init; }

    /// <summary>
    ///     Where the engine's wav files are expected to appear, or null to use
    ///     <c>&lt;cs2&gt;/game/csgo/movie</c>, a different root than the frames (observed): the wav
    ///     lands at <c>csgo/movie/&lt;timestamp&gt;/&lt;name&gt;.wav</c> while frames go under the
    ///     csvg search path. Resolved lazily, like <see cref="StagingDirectory" />.
    /// </summary>
    public string? AudioDirectory { get; init; }

    /// <summary>
    ///     Whether each frame is deleted once it has been piped to the encoder. On by default and
    ///     effectively mandatory for a real capture - an offline render at Targa sizes fills a disk in
    ///     minutes - but worth turning off to inspect what the engine actually wrote.
    /// </summary>
    public bool DeleteFramesAfterPipe { get; init; } = true;

    /// <summary>
    ///     Whether the staging directory is left alone after a failed clip, instead of being swept.
    ///     Off by default; on, it keeps the evidence.
    /// </summary>
    public bool KeepStagingOnFailure { get; init; }

    /// <summary>
    ///     What to do when a clip that asked for audio has no wav.
    /// </summary>
    public MissingAudioBehavior MissingAudioBehavior { get; init; } = MissingAudioBehavior.Fail;

    // A PlaybackTimescale knob was tried here and removed after measurement (2026-07-30, CS2
    // build 2000879). Neither timescale cvar can speed up a host_framerate capture:
    //   - demo_timescale is inert against game time: the demo always advances 64 ticks per game
    //     second, so pairing "demo_timescale 2" with a doubled host_framerate just oversampled a
    //     31.25 s range into 3749 frames of 2x slow motion (the audio duration guard flagged the
    //     -50% wav exactly as designed);
    //   - host_timescale 0 (the CSGO-era unpacing trick CSDM still sends on its CSGO path) changed
    //     nothing on CS2: 69 fps against a 70 fps baseline.
    // The frame loop is bound by a ~9 ms fixed per-frame cost (sim + startmovie's synchronous GPU
    // readback + present; 720p only raised 70 fps to 90), so the honest speed levers are the
    // clip's frame rate (frames scale linearly) and resolution - not playback pacing. Use
    // AdditionalSetupCommands for future pacing experiments.

    /// <summary>
    ///     Extra console commands issued with the per-clip setup, after the built-in ones
    ///     (<c>host_framerate</c>, <c>engine_no_focus_sleep</c>). For render-speed experiments and
    ///     site-specific tweaks (e.g. <c>fps_max 0</c>) without a code change - the same escape
    ///     hatch CSDM's per-sequence cfg provides. Nothing is restored afterwards: commands that
    ///     need undoing must be undone by the caller's own configuration or accepted as sticky for
    ///     the CS2 session, which ends with the capture run anyway.
    /// </summary>
    public IReadOnlyList<string> AdditionalSetupCommands { get; init; } = [];

    /// <summary>
    ///     When set, a clip's engine wav is not deleted after muxing: it is moved next to the
    ///     finished clip as <c>&lt;name&gt;.engine-audio.wav</c> and reported on
    ///     <see cref="CaptureResult.AdditionalOutputs" />. The wav is the clip's original
    ///     game-time audio with zero processing passes: the right source for any external
    ///     post-processing, which is why a caller might want it kept (re-processing
    ///     already-stretched audio measurably degrades it).
    /// </summary>
    public bool KeepEngineAudio { get; init; }

    /// <summary>
    ///     Name of the named pipe the plugin streams present-hook frames over (without the
    ///     <c>\\.\pipe\</c> prefix). Must match the constant compiled into the plugin; a knob so a
    ///     future plugin build can move it without a Core release.
    /// </summary>
    /// <remarks>
    ///     One pipe name per machine, like the gRPC port: concurrent CSVG instances are not
    ///     supported until dynamic port negotiation lands, and this rides the same limitation.
    /// </remarks>
    public string FramePipeName { get; init; } = "csvg-frames";

    /// <summary>
    ///     How long <see cref="InEngineFrameSource.PresentHook" /> waits after playback ends for the
    ///     plugin's end-of-stream record before failing the clip, in milliseconds. The plugin drains
    ///     its staging ring and writes the trailer when <c>endmovie</c> executes, so a healthy clip
    ///     finishes this wait in milliseconds.
    /// </summary>
    public int HookedDrainTimeoutMs { get; init; } = 15000;

    /// <summary>
    ///     How often the staging directory is scanned for finished frames, in milliseconds.
    /// </summary>
    /// <remarks>
    ///     Also sets the tail drain's outer bound, which is a fixed number of polls. There is no
    ///     separate drain-timeout knob because the drain's real completion test is structural - a
    ///     frame is finished once a higher-numbered one exists, and the last one once it has settled -
    ///     rather than a timeout, and a knob that bound nothing would only look like one that did.
    /// </remarks>
    public int FrameConsumerPollMs { get; init; } = 100;

    /// <summary>
    ///     Earliest demo tick a command batch may be scheduled at.
    /// </summary>
    /// <remarks>
    ///     The engine swallows commands scheduled in the first ticks of playback, so a clip starting
    ///     before this tick records from here instead, and says so in the result's warnings.
    /// </remarks>
    // 96 comes from CSDM issue 1343 and has not been re-confirmed on a current CS2 build.
    // Too low = the recording brackets never fire for clips near the start of a demo.
    public int StartTickClamp { get; init; } = 96;

    /// <summary>
    ///     How far the wav's length may differ from the video's game-time length, as a percentage,
    ///     before the clip reports it.
    /// </summary>
    /// <remarks>
    ///     The guard exists because CSGO shipped for three years (2017-2020) writing movie audio at
    ///     wall-clock length instead of movie length, which produces a file that plays perfectly and
    ///     drifts further out of sync the longer it runs. Two percent is tight enough to catch that
    ///     and loose enough for rounding at the tail.
    /// </remarks>
    public double AudioDurationTolerancePercent { get; init; } = 2.0;

    /// <summary>
    ///     How long to wait, in milliseconds, for the engine to finish writing the wav's RIFF header
    ///     after <c>endmovie</c>.
    /// </summary>
    /// <remarks>
    ///     The engine streams samples while recording and only knows the real count at the end, so it
    ///     reopens the file afterwards to correct the chunk sizes. A wav read before that has a
    ///     placeholder length, which would read as a wildly wrong duration rather than as an unfinished
    ///     file.
    /// </remarks>
    public int AudioFinalizationTimeoutMs { get; init; } = 15000;

    /// <summary>
    ///     How often the wav's header is re-read while waiting for the engine to finalize it, in
    ///     milliseconds.
    /// </summary>
    public int AudioFinalizationPollMs { get; init; } = 200;

    /// <summary>
    ///     Codec for the audio track when the wav is muxed onto the video.
    /// </summary>
    public string AudioCodec { get; init; } = "aac";

    /// <summary>
    ///     The unverified shape of the engine's output. See <see cref="InEngineEngineOutput" />.
    /// </summary>
    public InEngineEngineOutput EngineOutput { get; init; } = new();

    /// <summary>
    ///     Bytes in one uncompressed <see cref="TgaBitsPerPixel" />-bit Targa frame of the given size.
    ///     Quoted in diagnostics and in the error a Targa capture fails with when
    ///     <see cref="InEngineEngineOutput.TgaFrameSizeBytes" /> is not configured, so the value that
    ///     has to be filled in is the one being suggested.
    /// </summary>
    public static long UncompressedTgaFrameSize(int width, int height) =>
        TgaHeaderBytes + ((long)width * height * TgaBitsPerPixel / 8);

    /// <summary>
    ///     The movie name for a clip, which is also the prefix of every file the engine writes for it.
    /// </summary>
    public static string MovieName(int clipIndex) =>
        string.Format(CultureInfo.InvariantCulture, MovieNameFormat, clipIndex);

    /// <summary>
    ///     Problems that make this configuration unusable, as messages fit to show a user. Empty means
    ///     the settings are self-consistent; it says nothing about whether CS2 or ffmpeg are present.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> issues = [];

        if (EngineOutput.TgaFrameSizeBytes is <= 0)
        {
            issues.Add("Capture:InEngine:EngineOutput:TgaFrameSizeBytes must be greater than zero.");
        }

        if (EngineOutput.FrameNumberDigits < 1)
        {
            issues.Add("Capture:InEngine:EngineOutput:FrameNumberDigits must be at least 1.");
        }

        if (FrameConsumerPollMs <= 0)
        {
            issues.Add("Capture:InEngine:FrameConsumerPollMs must be greater than zero.");
        }

        if (StartTickClamp < 0)
        {
            issues.Add("Capture:InEngine:StartTickClamp cannot be negative.");
        }

        if (AudioDurationTolerancePercent <= 0)
        {
            issues.Add("Capture:InEngine:AudioDurationTolerancePercent must be greater than zero.");
        }

        if (AudioFinalizationPollMs <= 0)
        {
            issues.Add("Capture:InEngine:AudioFinalizationPollMs must be greater than zero.");
        }

        if (AudioFinalizationTimeoutMs < 0)
        {
            issues.Add("Capture:InEngine:AudioFinalizationTimeoutMs cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(IntermediateContainer))
        {
            issues.Add("Capture:InEngine:IntermediateContainer cannot be blank.");
        }

        if (FrameFormat == InEngineFrameFormat.Jpg && string.IsNullOrWhiteSpace(EngineOutput.JpgFormatToken))
        {
            issues.Add(
                "Capture:InEngine:FrameFormat is Jpg, but Capture:InEngine:EngineOutput:JpgFormatToken is " +
                "blank, so there is no format argument to pass to startmovie.");
        }

        if (!string.IsNullOrWhiteSpace(EngineOutput.AudioFileSearchPattern) &&
            !EngineOutput.AudioFileSearchPattern.Contains("{0}", StringComparison.Ordinal))
        {
            issues.Add(
                "Capture:InEngine:EngineOutput:AudioFileSearchPattern must contain '{0}', which stands in " +
                "for the movie name - without it every clip finds every other clip's audio.");
        }

        if (string.IsNullOrWhiteSpace(EngineOutput.AudioFileSearchPattern))
        {
            issues.Add("Capture:InEngine:EngineOutput:AudioFileSearchPattern cannot be blank.");
        }

        if (string.IsNullOrWhiteSpace(FramePipeName))
        {
            issues.Add("Capture:InEngine:FramePipeName cannot be blank.");
        }

        if (HookedDrainTimeoutMs <= 0)
        {
            issues.Add("Capture:InEngine:HookedDrainTimeoutMs must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(EngineOutput.AudioOnlyTokens))
        {
            issues.Add(
                "Capture:InEngine:EngineOutput:AudioOnlyTokens cannot be blank - it is the startmovie " +
                "argument list that puts the engine in its audio-only recording mode for hooked capture.");
        }

        return issues;
    }

    /// <summary>
    ///     The frame format as the image pipe names its decoder.
    /// </summary>
    public ImagePipeSourceFormat SourceFormat() => FrameFormat switch
    {
        InEngineFrameFormat.Tga => ImagePipeSourceFormat.Targa,
        _ => ImagePipeSourceFormat.Mjpeg
    };

    /// <summary>
    ///     File extension, without the dot, of the frames the engine writes in this format.
    /// </summary>
    // Observed on CS2 build 2000879 (2026-07-30): jpg mode writes .jpg. Targa's extension is still
    // assumed. Wrong value = the glob matches nothing and the clip fails with no frames.
    public string FrameExtension() => FrameFormat switch
    {
        InEngineFrameFormat.Tga => "tga",
        _ => "jpg"
    };
}
