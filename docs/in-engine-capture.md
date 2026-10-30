# In-engine capture (`InEngine`)

> **Validated against real CS2** (build 2000879): both frame formats produce working 1080p60 video
> **with audio** out of the box - `startmovie`'s output location, naming, jpg and tga modes, jpeg
> quality, tick-exact frame pacing and the game-time wav are all observed facts. The cvar restore
> values are the main remaining assumptions. `InEngine` is the default provider. The
> [last section](#unverified-against-real-cs2) lists exactly what is and is not confirmed. For the
> faster hooked variant, see [The hooked variant](#the-hooked-variant-inenginehooked).

## What it does

OBS screen-records CS2 in real time: the recording is as long as the playback took, and a frame the
machine could not render in time is a frame that is not in the file.

`InEngine` renders *offline*. `host_framerate <fps>` makes the engine advance game time by exactly
`1/fps` per rendered frame regardless of how long the frame took to draw, and `startmovie` makes it
dump one image per rendered frame. The result is frame-exact — no dropped frames, no timing jitter,
the same output whether the machine renders at 200 fps or at 2. It also works with the window
unfocused or offscreen.

The cost is data. At 1080p the engine writes 8.3 MB per Targa frame (32-bit, observed), so a 60 fps
capture asks the disk for roughly 500 MB/s, sustained, for the whole render. CSVG never lets the dump
accumulate: a frame is streamed into ffmpeg as soon as the engine has moved on to the next one, and
unlinked immediately. The staging directory holds the backlog and nothing more.

```
CS2 (host_framerate 60, startmovie)   ->  csvg_000########.jpg in movie/<timestamp>/ under staging
FrameSequenceConsumer                 ->  discovers the timestamped directory, pipes frame N once
                                          N+1 exists, then deletes N
ffmpeg -f image2pipe                  ->  intermediate .mkv
ffmpeg mux (-c:v copy)                ->  clip_000.mp4 (silent - see Audio)
```

## Selecting it

```json
{
  "Cs2VideoGenerator": { "VideoCaptureProvider": "InEngine" }
}
```

or `--capture-provider InEngine` on `generate` / `watch`. Names are matched ignoring case.

The provider needs a plugin advertising both `tick-scheduled-commands` (how `startmovie` and the
teardown reach the engine at their ticks) and `console-exec` (how the cvars reach it before recording
starts, and how a failed clip's recording is stopped). A plugin missing either fails the clip, naming
the one that is missing. Recording without `host_framerate` would produce a real-time capture with
the wrong frame count and no error at all, which is worse than not recording.

Run `csvg doctor` after selecting it. The InEngine branch checks ffmpeg, the staging directory, its
free space, which encoder will actually be used, and the disk bandwidth the chosen frame format asks
for. It leads with the unverified status.

## Frame rate and duration

The frame rate comes from the clip, not from this configuration: `CaptureMetadata.FrameRate`
(`settings.frameRate` in a compilation JSON) is what `host_framerate` is set to and what ffmpeg is
told the pipe's rate is. That is the difference the `HonorsFrameRate` trait describes. For OBS the
same value is advisory, because the real frame rate comes from the OBS profile.

`CaptureResult.Duration` is therefore **game time**: frames divided by frame rate. A ten-second clip
reports ten seconds whether the render took two seconds or two minutes. Wall clock and the rates
derived from it live in `CaptureResult.Statistics`:

| Statistic | Meaning |
|---|---|
| `gameSeconds` | Frames divided by frame rate — the same number as `Duration` |
| `wallClockSeconds` | How long the render actually took, measured from the first consumed frame — the seek-and-arm preamble before it is `setupSeconds` |
| `setupSeconds` | Wall clock between capture arm and the first frame — the demo seek, mostly |
| `captureFps` | Frames produced per second of wall clock |
| `realtimeFactor` | `gameSeconds / wallClockSeconds`; below 1 means the render was slower than real time |
| `framesConsumed` / `frameBytes` | What actually reached the encoder |
| `encoder` / `encoderPreset` | What actually encoded the frames — `Auto` resolved and the default preset filled in |
| `movieName`, `frameFormat`, `frameRate`, `recordTick`, `endTick`, `audioMuxed` | What was asked of the engine |

`DroppedFrames` counts gaps in the engine's numbering. It should always be zero. `startmovie` does
not skip numbers, so a non-zero count is a regression signal, not a capacity signal. Backpressure
needs no mechanism: writing to a busy encoder blocks, the poller falls behind, and frames pile up in
staging. That pile is the buffer, and its size is reported as `stagingBacklog` in progress updates.

## Engine brackets

One immediate batch and two tick-scheduled ones, all issued by `ClipCaptureCoordinator` before
playback starts:

| When | Commands |
|---|---|
| Immediately, before playback | `host_framerate <fps>`, `engine_no_focus_sleep 0` |
| Tick `max(clamp, start)` | `startmovie csvg_<index> [jpg] [wav] [jpeg_quality <q>]` |
| Tick `end` | `endmovie`, `host_framerate 0`, `engine_no_focus_sleep <default>` |

Format, audio and jpeg quality all travel on the `startmovie` line because they are startmovie
arguments, not cvars. The engine's own usage string is
`startmovie testmovie jpg wav jpeg_qality 75 framerate 30`. The `wav` token appears exactly when the
clip asks for audio; without it no wav is written. The format token is mandatory even for Targa: a
token-less `startmovie` is the engine's AVI/video path and writes no image files at all.

Three details worth knowing:

- **The cvars are not tick-scheduled**, and that is deliberate. They only have to be in place before
  `startmovie`, not at any particular moment, and the plugin fires batches on the half-open interval
  `(last observed tick, current tick]` — anything armed at or below the last observation is stranded
  for good. A compilation reloads the demo only when the demo *path* changes, so the second clip from
  one demo starts from wherever the first one left the playhead; a setup tick ahead of the recording
  tick was behind that playhead for any two clips less than a second apart. `startmovie` keeps its
  tick because it genuinely needs one.
- The cvar restores ride the end batch rather than being scheduled after it. Playback pauses at the
  end tick, so a restore armed for a later tick would wait for a tick that never arrives.
- The abort commands are the end batch. A clip that dies mid-playback has the same problem in a
  worse form: its `endmovie` is armed at a tick nothing will play, and the engine keeps writing
  frames. The coordinator runs the abort commands through the immediate console (`console-exec`) the
  moment the clip fails — the same channel the setup went out on.

The engine swallows commands scheduled in the first ticks of playback (CSDM issue 1343), so
`StartTickClamp` (default 96) is the earliest tick a batch is scheduled at. A clip starting before it
records from the clamp and says so in the result's warnings.

## Capture speed

Measured on a 32-core / NVMe / NVIDIA machine against a real demo (CS2 build 2000879, 2000-tick
range, TGA, NVENC): **70 fps at 1080p (1.16x realtime), 90 fps at 720p (1.50x)**. The frame loop
carries a ~9 ms fixed cost per frame (sim step plus `startmovie`'s synchronous GPU readback plus
present), which is why the encoder preset (p1 vs p7: identical), `fps_max 0`, `mat_vsync 0`, the
low-latency sleeps, `host_timescale 0` and `demo_timescale` all measure as no-ops.

Only two levers actually work. Frames scale linearly with the clip's `frameRate`, so a 30 fps
capture takes half the wall clock of a 60 fps one. Resolution is sublinear (the fixed cost stays)
but real.

`demo_timescale` deserves its own warning: it is inert against game time under `host_framerate`
(the demo always advances 64 ticks per game second), so raising `host_framerate` alongside it just
oversamples into slow motion. A "timescale 2" experiment produced exactly 2x the frames and 2x
slow-motion video, with the audio duration guard flagging the half-length wav. There is no faster
playback to be had from pacing cvars; the fixed per-frame cost is the whole story, and it is the
same for every `startmovie`-based recorder (CSDM's non-HLAE CS2 path uses the identical mechanism).
HLAE outruns them by replacing the capture path itself (asynchronous GPU capture), not by pacing.

## The hooked variant (`InEngineHooked`)

The file path above pays a ~8 ms synchronous GPU readback per frame inside `startmovie` - the cost
that caps it at ~1.15x realtime at 1080p60. `InEngineHooked` removes it:

- The engine records in **audio-only mode** (`startmovie <name> wav nocodec`, observed): the same
  fixed timestep and game-time wav, zero frame files.
- The CSVG plugin's **DXGI present hook** copies each presented backbuffer into a small staging-
  texture ring and maps each copy two presents late, so the GPU-to-CPU transfer overlaps rendering
  instead of stalling it (the delayed map measures ~0.001 ms). Frames stream to the host over a
  named pipe (`\\.\pipe\csvg-frames`) as raw RGBA and go straight into ffmpeg's `rawvideo` input.
- Presents map 1:1 onto movie frames (measured: 302 presents for 302 frames, content-identical at
  61 dB PSNR against the Targa dump), so the output is exactly as frame-accurate as the file path.

Measured on the reference machine, benchmark clip, 1080p60: **2.70x realtime (162 fps)** against the
file path's 1.15x. Validated end to end on CS2 build 2000879 (1873 frames, audio in sync within 9 ms).

```json
{ "Cs2VideoGenerator": { "VideoCaptureProvider": "InEngineHooked" } }
```

or `--capture-provider InEngineHooked`. It shares the entire `Capture:InEngine` configuration
section - staging, audio, encoder, timing - and differs only in where pixels come from.

Requirements and behavior to know about:

- **Windows only** (the frame source is a DXGI swapchain hook; a Vulkan analog for Linux is a
  design, not code), and requires a plugin build advertising the `present-capture` capability -
  older plugins fail the clip before playback with a message saying so.
- The clip must carry a resolution (the CLI's `--width`/`--height` defaults do): raw frames have no
  header, so ffmpeg is told the geometry up front, and the plugin's stream header is checked
  against it.
- The capture is one frame (1/fps) shorter than the wav at the clip's tail, because the final movie
  frame's present lands after `endmovie`. That is inside the audio-duration tolerance and invisible
  in practice.
- Backpressure never drops frames. If the encoder falls behind, the pipe fills and the engine's
  present blocks; the render slows down instead.
- If nothing is listening on the frame pipe (any other provider, or a user-typed `startmovie`),
  the plugin's hook does nothing at all; the feature is inert unless this provider armed it.
- Coexists with the Steam overlay, which also hooks Present: capture ran with
  `GameOverlayRenderer64.dll` loaded (two capture windows plus full session teardown) with no
  instability.

## Slow motion and fast-forward

Offline capture produces **real** slow motion: every slowed frame is a frame the engine actually
rendered, not an interpolation or a duplicate. Set `CaptureMetadata.PlaybackSpeed` (the compilation
JSON's per-clip `playbackSpeed`, or `watch --speed`): `0.5` is 2× slow motion, `0.25` is 4×, `2` is
2× fast-forward. The engine renders at `FrameRate / speed` (so a 60 fps clip at `0.5` renders 120
frames per game-second) and encodes at `FrameRate`; the game-time audio is stretched by the speed
factor with a pitch-preserving `atempo` chain so the tracks stay in step. This is **not**
`demo_timescale`, which is measurably inert for offline capture.

Ask for a real-time copy alongside a speed clip with `produceNormalizedCopy` (`watch --normalize`).
It is made losslessly at capture time: the video retimed by a timestamp rewrite (stream copy, no
re-encode) and the engine's original game-time wav muxed on untouched, so its audio has zero
tempo-processing passes. Renormalizing a speed clip after the fact instead would run the audio
through two `atempo` passes; keeping the original wav avoids that entirely (`KeepEngineAudio` exposes
it directly). Speed clips and their normalized copies both appear on `CaptureResult.AdditionalOutputs`.

## Frame formats and disk bandwidth

`Tga` is the default: lossless, and on the machines measured also *faster* than jpg, because the
engine's CPU-side JPEG encoder costs more than Targa's disk bandwidth on an NVMe (24 fps vs 55-70 fps
at 1080p). The record length ffmpeg needs is computed from the clip's width and height when
`EngineOutput:TgaFrameSizeBytes` is not set; the per-frame record check still fails the clip loudly
on any disagreement. `Jpg` is the low-bandwidth alternative: lossy, quality set on the startmovie
line, a fiftieth of the disk throughput, for machines whose storage cannot keep up.

`Tga` is lossless and enormous, and it carries a hazard worth stating plainly: **ffmpeg has no Targa
parser.** Fed a bare stream of Targa frames, `image2pipe` cannot find where one ends and the next
begins, and produces nothing at all ("Output file does not contain any stream"). The only thing that
makes a Targa pipe work is telling ffmpeg the exact byte length of one frame, which is what
`EngineOutput:TgaFrameSizeBytes` is. That holds only while every frame is the same size — that is,
while the frames are uncompressed. Run-length-encoded frames vary in size, at which point a fixed
record length is *silently* wrong: the encoder keeps splitting at the wrong offset and writes a video
of noise that decodes without complaint.

Set `TgaFrameSizeBytes` explicitly when the engine's output disagrees with the computed value; the
per-frame check is what turns that disagreement into a loud failure instead of noise. The engine
writes uncompressed 32-bit Targa (observed: a live 1080p frame was exactly 8,294,418 bytes), so the
value is `18 + width × height × 4`:

| Resolution | Bytes per Targa frame | At 30 fps | At 60 fps | At 120 fps |
|---|---|---|---|---|
| 1280×720 | 3.7 MB | 111 MB/s | 221 MB/s | 442 MB/s |
| 1920×1080 | 8.3 MB | 249 MB/s | 498 MB/s | 995 MB/s |
| 2560×1440 | 14.7 MB | 442 MB/s | 885 MB/s | 1.8 GB/s |
| 3840×2160 | 33.2 MB | 995 MB/s | 2.0 GB/s | 4.0 GB/s |

A mechanical disk cannot sustain any of these. A SATA SSD manages 1080p30 and struggles above it.
NVMe is the realistic floor for 1080p60, and on Linux a tmpfs staging directory removes the disk from
the question entirely (at the cost of holding the backlog in RAM). If none of that is available, use
`Jpg`.

Every frame is checked against the configured record length as it is piped, and the first frame that
disagrees fails the clip with both sizes in the message. That check is the only reason a wrong record
length is loud rather than silent, so do not remove it. The failure also vetoes the clip immediately
rather than waiting for the end tick: the frame consumer closes the pipe on its way out, so ffmpeg
sees a clean end of stream and exits successfully, and the encoder's own exit code would never report
the problem.

## Encoder quality vs speed

The frames go into an ffmpeg `image2pipe` encode, and the file it writes is what the wav is muxed onto
with `-c:v copy`. For a compilation that file is an intermediate, re-encoded at concatenation. **For a
single clip (`csvg watch`, or any run that produces one video) the intermediate encode is the final
quality.** Nothing re-encodes after it, so `IntermediateQuality` and `IntermediateEncoderPreset` are
the quality settings of the delivered video.

Spending encoder time here is unusually cheap, because the render and the encode are not in lockstep.
Writing to a busy encoder blocks, the frame poller falls behind, and frames pile up in staging. That
pile *is* the buffer. A slower preset therefore cannot drop a frame or change the output's length; it
can only make the capture take longer in wall clock. That is why the defaults sit where they do:

| Encoder | Default preset | Why |
|---|---|---|
| `h264_nvenc` | `p7` (+ `-tune hq`) | The slow end of NVENC's scale. At the near-lossless default `-cq 16` the render, not the fixed-function encoder, is what dominates wall clock, so `p7` costs little and the quality is worth having. No `-multipass`: its passes exist to spend a bitrate budget well, and `-cq` is a quality target with no budget to spend |
| `libx264` | `veryfast` | Not `ultrafast` — its quality loss at a given CRF is real, because it drops the analysis CRF assumes when picking quantizers. Not slower than `veryfast` either: software H.264 is where an offline render actually waits, and anything past this can fall behind a fast render by hours on a long compilation |
| `utvideo` | *(none)* | Lossless; it has no such dial |

Override with `Capture:InEngine:IntermediateEncoderPreset` when the trade should go the other way: a
handful of hero clips on x264 are worth a `slow`, an overnight batch on a busy machine may want
`ultrafast` back. The name is passed to ffmpeg unvalidated. The accepted set belongs to the local
ffmpeg build, so a name CSVG has never heard of has to reach ffmpeg, which rejects it immediately and
by name. The encode fails, and the failure carries ffmpeg's own stderr into the log and onto the
result, because an exit code alone does not distinguish a refused option from an undecodable frame.

`csvg watch` prints what actually ran once the capture succeeds:

```
Rendered 564 frames in 8.3 s wall (68 fps, 1.13x realtime), encoder h264_nvenc p7
```

The encoder and preset there are the resolved ones (`Auto` already decided, the default preset already
filled in), not what the settings file happened to say.

## Audio

CS2 writes the movie's audio as a wav when (and only when) the `wav` token was on the `startmovie`
line. It lands under a **different root than the frames**: `csgo/movie/<timestamp>/<name>.wav`
under the game files directory, while frames go under the csvg search path (both observed on build
2000879). The wav is game-time length (9.358 s for a 9.367 s clip), so the CSGO-era wall-clock bug
is not present on current builds. After `endmovie`:

1. The wav is located by glob under the audio directory, searched recursively (it is expected in the
   movie's timestamped subdirectory), newest match wins. One movie name per clip
   keeps clips from finding each other's audio, and anything under this clip's name is swept when the
   clip arms. A candidate last written before the clip started is rejected outright: a clip whose
   recording produced no wav would otherwise inherit the previous run's — right name, plausible
   length, wrong take, and nothing for the duration guard to catch.
2. CSVG waits for the engine to **finalize the RIFF header**. An engine streaming a wav cannot know
   the sample count until the end, so it writes a placeholder and corrects the chunk sizes when it
   closes the file. A header whose `data` chunk declares zero bytes, or more bytes than the file
   contains, is unfinished, which is a different state from "a very short recording". Reading it as a
   duration would produce a number wrong enough to look like a real defect. Two consecutive agreeing
   reads are required.
3. The wav's length is compared against the video's game-time length. More than
   `AudioDurationTolerancePercent` (default 2%) apart is reported.
4. The wav is muxed on with `-c:v copy`, and deleted once it has been.

**The duration guard** exists because CSGO shipped for three years, roughly 2017 to 2020, writing
movie audio at *wall-clock* length while the video was movie-length. The result plays perfectly and
drifts further out of sync the longer it runs. Worst possible failure mode: nothing about the file
looks wrong. If frames were dropped the warning blames the dropped frames instead, because then the
video really is short of the game time it covers and that is the defect to fix first.

`CaptureAudio: false` skips the mux and the guard but still *looks* for the wav, to delete it: the
engine is expected to write one whether the clip wanted it or not, and nothing else would clean it
up. Whether it always does (`jpg` mode in particular) is one of the open questions, so finding
nothing here is not an error.

A clip that asked for audio and has none takes the `MissingAudioBehavior` path. `Fail` is the
default and fails the clip: output that does not meet the requested spec should not pass silently.
`WarnAndContinue` keeps the video without an audio track and puts a warning on the result.

## Configuration reference

Section `Capture:InEngine`.

| Setting | Default | Meaning |
|---|---|---|
| `FrameFormat` | `Tga` | `Jpg` or `Tga`. See above before choosing `Tga` |
| `JpegQuality` | `90` | JPEG quality passed on the `startmovie` line for `Jpg`; `null` leaves the engine's own default (50) |
| `IntermediateEncoder` | `Auto` | `Auto`, `Nvenc`, `X264`, `Utvideo`. `Auto` probes the local ffmpeg and prefers `h264_nvenc`. `Utvideo` is lossless and needs an output container that can carry it — the audio mux copies the video stream, and MP4 cannot, so `Utvideo` into an `mp4`/`mov` output is rejected before the render rather than after it |
| `IntermediateQuality` | `16` | `-cq` for NVENC, `-crf` for x264. Near-lossless — see *Encoder quality vs speed* above |
| `IntermediateEncoderPreset` | *(unset)* | The speed-for-quality dial: `p1`–`p7` for NVENC, `ultrafast`–`placebo` for x264, ignored by `Utvideo`. Unset means the encoder's default (`p7` / `veryfast`). Not validated here — ffmpeg rejects a name it does not know, by name, and the failure carries its stderr |
| `IntermediateContainer` | `mkv` | Container for the video the frame pipe writes, before audio is muxed on |
| `StagingDirectory` | `<cs2>/game/csgo/csvg/movie` | Where the engine's frames are expected. Resolved lazily — a host that never captures never needs a CS2 installation |
| `AudioDirectory` | `<cs2>/game/csgo/movie` | Where the engine's movie audio is expected — a different root than the frames (observed). Resolved lazily |
| `DeleteFramesAfterPipe` | `true` | Effectively mandatory for a real capture; turn it off only to inspect what the engine actually wrote |
| `KeepStagingOnFailure` | `false` | Keep the evidence after a failed clip |
| `MissingAudioBehavior` | `Fail` | `Fail` or `WarnAndContinue` — audio works on current builds, so a missing wav is a real problem |
| `AdditionalSetupCommands` | `[]` | Extra console commands issued with the per-clip setup, after `host_framerate` and `engine_no_focus_sleep`. Nothing is restored afterwards |
| `KeepEngineAudio` | `false` | Keep the engine's original game-time wav next to the output (`<name>.engine-audio.wav`, reported on `CaptureResult.AdditionalOutputs`) instead of deleting it after muxing — the zero-processing-pass source for external post-processing |
| `FramePipeName` | `csvg-frames` | `InEngineHooked` only: the named pipe the plugin streams frames over, without the `\\.\pipe\` prefix. Must match the plugin build |
| `HookedDrainTimeoutMs` | `15000` | `InEngineHooked` only: how long to wait after playback ends for the plugin's end-of-stream record |
| `FrameConsumerPollMs` | `100` | How often the staging directory is scanned. Also bounds the tail drain, which is a fixed number of polls |
| `StartTickClamp` | `96` | Earliest tick a command batch is scheduled at |
| `AudioDurationTolerancePercent` | `2.0` | How far the wav may be from the video's game-time length before it is reported |
| `AudioFinalizationTimeoutMs` | `15000` | How long to wait for the engine to correct the wav's header |
| `AudioFinalizationPollMs` | `200` | How often the header is re-read while waiting |
| `AudioCodec` | `aac` | Codec for the muxed audio track |

There is deliberately **no frame-rate setting** in this section: it arrives per clip on
`CaptureMetadata.FrameRate` (the compilation JSON's `frameRate`, or the CLI's `--fps`), because it is
the property of the requested output that this backend can actually honor. The CLI `watch` verb
defaults it to 30 fps (`Cs2VideoGenerator:DefaultCaptureFrameRate`); the library and compilation
defaults stay 60. The file path also has no drain-timeout setting: the drain's completion test is structural
(a frame is finished once a higher-numbered one exists, and the last one once it has settled), and a
knob that bound nothing would only look like one that did.

### `Capture:InEngine:EngineOutput`

Everything in this subsection describes what CS2 leaves on disk. The defaults reflect what
**CS2 build 2000879 was observed doing**: `startmovie csvg_000 jpg` recorded a tick-exact sequence
into `movie/2026_07_30_15_44_29/csvg_00000000000.jpg` onward. Rows marked *(observed)* are
measured on that build; the rest are still assumptions. It is a subsection rather
than a set of scattered settings so that correcting reality on a future build is an edit to one block.

| Setting | Default | Status | What a wrong value does |
|---|---|---|---|
| `MovieNamePrefix` | `""` | *(observed)* no prefix needed; output goes under the first writable gameinfo search path, which the CSVG patch makes the plugin directory | Frames land where the consumer never looks; the clip fails with no frames |
| `FramesInTimestampedSubdirectory` | `true` | *(observed)* one `yyyy_MM_dd_HH_mm_ss` directory per `startmovie`; the engine's own format string is `movie/%04d_%02d_%02d_%02d_%02d_%02d/%s` | Off, on a build that uses them: no frames found. On, on a build that does not: harmless — the staging root is always scanned too |
| `FrameNumberDigits` | `8` | *(observed)* 8 digits, appended with no separator (`%s%08llu`) | The glob matches nothing; the clip fails with no frames |
| `FirstFrameNumber` | `0` | *(observed)* numbering starts at 0 | Survivable — the consumer adopts the first number it sees and logs the disagreement |
| `TgaFrameSizeBytes` | *(unset)* | *(observed)* uncompressed 32-bit; computed from the clip resolution when unset | Silently wrong output if the frames were ever compressed — which is why every frame is checked and the first mismatch fails the clip |
| `TgaFormatToken` | `tga` | *(observed)* mandatory; a token-less `startmovie` writes no image files | No frames appear |
| `JpgFormatToken` | `jpg` | *(observed)* works; engine default quality is 50 | `startmovie` is rejected; no frames appear |
| `AudioToken` | `wav` | *(observed)* works — writes `csgo/movie/<timestamp>/<name>.wav` at game-time length; without the token no wav is written | Blank: no clip ever gets audio |
| `AudioOnlyTokens` | `wav nocodec` | *(observed)* `InEngineHooked` only: game-time wav, zero frame files | The engine dumps frames the hooked provider does not read, or records no wav |
| `JpegQualityArgument` | `jpeg_quality` | *(observed)* argument on the startmovie line, not a cvar; the correct spelling is accepted (`quality 90` echoed back) despite the usage text's typo `jpeg_qality` | The engine ignores an unknown token and uses default quality (50) |
| `FocusSleepRestoreValue` | `1` | Still unverified: the engine's own `engine_no_focus_sleep` default | The user's CS2 is left with a different throttle than it started with |
| `AudioFileSearchPattern` | `{0}*.wav` | *(observed)* the wav is named exactly `<name>.wav`, which this matches | The wav is not found; the clip takes the `MissingAudioBehavior` path |
| `AudioSearchRecursive` | `true` | Covers the timestamped subdirectory | As above |

## Limitations

- **No pause or resume.** Offline capture has no meaningful pause: pausing under `host_framerate`
  keeps rendering the same moment over and over. `CaptureTraits.SupportsPauseResume` is false and the
  operations are not on the clip type at all.
- **One clip at a time.** The engine records one movie at a time, so a second concurrent clip would
  have its frames folded into the first one's sequence. A second `BeginClipAsync` is rejected.
- Clips before tick 96 record from tick 96 (see `StartTickClamp`).
- The render waits on the encoder. At a fixed timestep there is no frame to drop, so a slow encoder
  does not degrade the output, it extends the render. `Auto` falling back to `libx264` is therefore
  worth knowing about, which is why doctor warns about it.
- `demo_timescale` has no effect on game time under `host_framerate` (see *Capture speed*). Use
  `playbackSpeed` for slow motion or fast-forward.

## Unverified against real CS2

Not yet confirmed on build 2000879: the engine's own `engine_no_focus_sleep` default (it is what
gets restored), and whether the tick-96 command clamp still applies to current builds. `csvg doctor` reports the same list at runtime, and each item has
an "unverified" comment at its definition.

Mock mode deliberately does **not** simulate `startmovie`. A mock built to match these assumptions
would make a green mock end-to-end test prove self-consistency rather than fidelity to CS2.

## See also

- [`docs/mock-cs2-mode.md`](mock-cs2-mode.md) — mock mode, which does not yet simulate `startmovie`
- [`README.md`](../README.md#configuration) — configuration and the provider table
