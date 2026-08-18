#region

using System.Diagnostics;
using System.Globalization;
using CommandLine;
using CommandLine.Text;
using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Progress;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Cli.Utils;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Microsoft.Extensions.Options;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

public sealed partial class WatchTask(
    ILogger<WatchTask> logger,
    ActivitySource activitySource,
    CsvgVideoSession videoSession,
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder,
    IOptions<WatchTask.Options> options,
    IOptions<Cs2VideoGeneratorOptions> generatorOptions,
    IOptions<InEngineCaptureConfiguration> inEngineConfiguration) : CliTaskBase(applicationLifetime, exitCodeHolder)
{
    /// <summary>
    ///     Ticks played when no --end-tick is given. The plugin protocol has no demo-length
    ///     query or play-to-end command, so a fixed default range is used instead.
    /// </summary>
    internal const int DefaultTickRangeLength = 10000;

    protected override async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = activitySource.StartActivity(nameof(WatchTask));
        activity?.SetTag("cli.demo", options.Value.Demo);
        activity?.SetTag("cli.mockMode", options.Value.MockMode);
        activity?.SetTag("cli.mockServerPath", options.Value.ExternalMockServerPath);
        activity?.SetTag("cli.noCapture", options.Value.NoCapture);

        WriteTaskHeader("CS2 Demo Playback");

        string demoPath = options.Value.Demo;
        if (!Path.IsPathRooted(demoPath))
        {
            // relative paths resolve against the working directory
            demoPath = Path.GetFullPath(demoPath);
        }

        if (!File.Exists(demoPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Demo file not found: {Markup.Escape(options.Value.Demo)}");
            if (demoPath != options.Value.Demo)
            {
                AnsiConsole.MarkupLine($"[dim]  Resolved path: {Markup.Escape(demoPath)}[/]");
            }

            return ExitCodes.UsageError;
        }

        options.Value.Demo = demoPath;

        bool capture = !options.Value.NoCapture;

        // Determine tick range (numeric constraints are validated at the parse layer)
        int startTick = options.Value.StartTick;
        int endTick = options.Value.EndTick > 0
            ? options.Value.EndTick
            : startTick + DefaultTickRangeLength;

        DisplayConfiguration(capture, startTick, endTick);

        int exitCode;

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Starting CS2 session...", async ctx =>
                {
                    await videoSession.StartAsync(
                        new EngineSessionOptions
                        {
                            Width = options.Value.Width,
                            Height = options.Value.Height,
                            Fullscreen = options.Value.Fullscreen
                        },
                        initializeCapture: capture,
                        cancellationToken);
                });

            AnsiConsole.MarkupLine("[green]✓[/] CS2 session started");
            LogSessionReady(logger, videoSession.Engine.SessionId ?? "unknown");

            // --game-events is applied by Core at session start (it is a Cs2VideoGenerator option);
            // this only surfaces the resulting stream on the console. Wired unconditionally: with
            // telemetry off the event never fires.
            videoSession.GameEventReceived += (_, telemetry) =>
            {
                AnsiConsole.MarkupLine(
                    $"[grey]game-event[/] [cyan]{Markup.Escape(telemetry.EventName)}[/] " +
                    $"{Markup.Escape(telemetry.JsonPayload)}");
                return Task.CompletedTask;
            };

            await AnsiConsole.Status()
                .StartAsync("Loading demo...",
                    async ctx => { await videoSession.LoadDemoAsync(options.Value.Demo, cancellationToken: cancellationToken); });

            AnsiConsole.MarkupLine($"[green]✓[/] Demo loaded: {Markup.Escape(Path.GetFileName(options.Value.Demo))}");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[cyan]Playing ticks {startTick} - {endTick}[/]");

            exitCode = capture
                ? await PlayWithCaptureAsync(startTick, endTick, cancellationToken)
                : await PlayWithoutCaptureAsync(startTick, endTick, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Playback cancelled[/]");
            exitCode = ExitCodes.OperationFailure;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {Markup.Escape(ex.Message)}");
            logger.LogError(ex, "WatchTask timed out");
            exitCode = ExitCodes.OperationFailure;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            logger.LogError(ex, "WatchTask failed");
            exitCode = ExitCodes.OperationFailure;
        }
        finally
        {
            await AnsiConsole.Status()
                .StartAsync("Stopping CS2 session...",
                    async _ => { await videoSession.StopAsync(CancellationToken.None); });
            AnsiConsole.MarkupLine("[green]✓[/] Session stopped");
        }

        return exitCode;
    }

    /// <summary>
    ///     Plays the tick range while recording it, through the single-clip Core pathway
    ///     (<see cref="CsvgVideoSession.CaptureClipAsync" />). Same per-clip recipe a compilation's
    ///     clips go through.
    /// </summary>
    private async Task<int> PlayWithCaptureAsync(int startTick, int endTick, CancellationToken cancellationToken)
    {
        // Directory: --output-dir / Cs2VideoGenerator:OutputDirectory (relative resolves against the
        // working directory), falling back to the working directory itself. Name: an explicit
        // --output-name verbatim, otherwise the descriptive default that encodes the capture
        // configuration - so a directory of ad-hoc recordings stays reviewable.
        string outputDirectory = generatorOptions.Value.OutputDirectory is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Environment.CurrentDirectory;

        // Frame rate: --fps wins, else the configured default (Cs2VideoGenerator:DefaultCaptureFrameRate,
        // 30 out of the box - see the option docs for why). Speed and its normalized companion are
        // per-run intents, straight off the flags.
        int frameRate = options.Value.Fps ?? generatorOptions.Value.DefaultCaptureFrameRate;
        double playbackSpeed = options.Value.Speed ?? 1.0;
        bool normalize = options.Value.Normalize;

        string providerName = generatorOptions.Value.VideoCaptureProvider;
        string outputName = options.Value.OutputName is { Length: > 0 } explicitName
            ? explicitName
            : CaptureOutputNames.Describe(
                options.Value.Demo, startTick, endTick, providerName,
                options.Value.Width, options.Value.Height, frameRate,
                inEngineConfiguration.Value, DateTime.Now);

        CaptureMetadata metadata = new()
        {
            OutputDirectory = outputDirectory,
            OutputFileName = outputName,
            Width = options.Value.Width,
            Height = options.Value.Height,
            FrameRate = frameRate,
            PlaybackSpeed = playbackSpeed,
            ProduceNormalizedCopy = normalize
        };

        CaptureResult captureResult = await videoSession.CaptureClipAsync(
            new ClipCaptureContext
            {
                StartTick = startTick,
                EndTick = endTick,
                Metadata = metadata
            },
            cancellationToken);

        AnsiConsole.WriteLine();

        if (!captureResult.Success)
        {
            AnsiConsole.MarkupLine(
                $"[red]Capture failed:[/] {Markup.Escape(captureResult.ErrorMessage ?? "Unknown error")}");
            return ExitCodes.OperationFailure;
        }

        AnsiConsole.MarkupLine("[green]✓[/] Playback completed");
        AnsiConsole.MarkupLine($"[cyan]Output:[/] {Markup.Escape(captureResult.OutputFilePath)}");

        // Side artifacts: a normalized real-time copy of a speed clip, a kept engine wav.
        foreach (string extra in captureResult.AdditionalOutputs)
        {
            AnsiConsole.MarkupLine($"[cyan]Also:[/] {Markup.Escape(extra)}");
        }

        // Only the offline provider measures its own render, so this line is present for InEngine and
        // absent for OBS - where wall clock and clip length are the same number and saying so twice
        // would be noise.
        if (CaptureStatisticsFormatter.Describe(captureResult.Statistics) is { } summary)
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(summary)}[/]");
        }

        return ExitCodes.Success;
    }

    private async Task<int> PlayWithoutCaptureAsync(int startTick, int endTick, CancellationToken cancellationToken)
    {
        DemoPlaybackResult playbackResult = await videoSession.PlayTickRangeAsync(
            startTick, endTick, cancellationToken: cancellationToken);

        AnsiConsole.WriteLine();

        if (!playbackResult.Success)
        {
            AnsiConsole.MarkupLine(
                $"[red]Playback failed:[/] {Markup.Escape(playbackResult.ErrorMessage ?? "Unknown error")}");
            return ExitCodes.OperationFailure;
        }

        AnsiConsole.MarkupLine("[green]✓[/] Playback completed");
        return ExitCodes.Success;
    }

    private void DisplayConfiguration(bool capture, int startTick, int endTick)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Demo", Markup.Escape(Path.GetFileName(options.Value.Demo)));
        table.AddRow("Resolution", $"{options.Value.Width}x{options.Value.Height}");
        table.AddRow("Fullscreen", options.Value.Fullscreen ? "Yes" : "No");
        table.AddRow("Capture", capture ? "Enabled" : "Disabled (--no-capture)");

        if (capture)
        {
            string outputDirectory = generatorOptions.Value.OutputDirectory is { Length: > 0 } configured
                ? Path.GetFullPath(configured)
                : Environment.CurrentDirectory;
            table.AddRow("Output directory", Markup.Escape(outputDirectory));

            int fps = options.Value.Fps ?? generatorOptions.Value.DefaultCaptureFrameRate;
            table.AddRow("Frame rate", $"{fps} fps");

            double speed = options.Value.Speed ?? 1.0;
            if (Math.Abs(speed - 1.0) > 0.0001)
            {
                string sense = speed < 1 ? "slow motion" : "fast-forward";
                string norm = options.Value.Normalize ? " (+ normalized copy)" : "";
                table.AddRow("Playback speed", $"{speed.ToString(CultureInfo.InvariantCulture)}x {sense}{norm}");
            }
        }

        string tickRange = options.Value.EndTick > 0
            ? $"{startTick} - {endTick}"
            : $"{startTick} - {endTick} (default: {DefaultTickRangeLength} ticks)";
        table.AddRow("Tick Range", tickRange);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    [LoggerMessage(LogLevel.Information, "Session ready: {SessionId}")]
    static partial void LogSessionReady(ILogger<WatchTask> logger, string SessionId);

    [Verb("watch", HelpText = "Launch CS2 and play a demo file, recording it by default")]
    public sealed class Options : CommonOptions, IRegisterTaskAndOptions
    {
        [Usage(ApplicationAlias = "csvg")]
        public static IEnumerable<Example> Examples =>
        [
            new("Play and record a demo file", new Options { Demo = "match.dem" }),
            new("Play without recording", new Options { Demo = "match.dem", NoCapture = true }),
            new("Play specific tick range", new Options { Demo = "match.dem", StartTick = 5000, EndTick = 15000 }),
            new("Play in 4K fullscreen",
                new Options
                {
                    Demo = "match.dem",
                    Width = 3840,
                    Height = 2160,
                    Fullscreen = true
                })
        ];

        // Task-specific options only - mock options inherited from CommonOptions

        [Option('d', "demo", Required = true, HelpText = "Path to the .dem file to play")]
        public required string Demo { get; set; }

        [Option('s', "start-tick", Default = 0, HelpText = "Demo tick to start playback from")]
        public int StartTick { get; set; }

        [Option('e', "end-tick", Default = 0,
            HelpText = "Demo tick to stop playback at (0 = play 10000 ticks from --start-tick)")]
        public int EndTick { get; set; }

        [Option('w', "width", Default = 1920, HelpText = "Game window width in pixels")]
        public int Width { get; set; }

        [Option('h', "height", Default = 1080, HelpText = "Game window height in pixels")]
        public int Height { get; set; }

        [Option('f', "fullscreen", Default = false, HelpText = "Launch in fullscreen mode")]
        public bool Fullscreen { get; set; }

        [Option("no-capture", Default = false,
            HelpText = "Disable video capture (watch only, no recording)")]
        public bool NoCapture { get; set; }

        /// <summary>
        ///     Output frame rate. When omitted, the configured default
        ///     (<c>Cs2VideoGenerator:DefaultCaptureFrameRate</c>, 30 out of the box) applies.
        /// </summary>
        [Option("fps", Required = false,
            HelpText = "Output frame rate (default: 30; pass 60 for the smoothest result). For the " +
                       "offline providers a lower rate renders fewer frames and captures faster.")]
        [ConfigurationOverride(ConfigurationPath = DefaultCaptureFrameRateConfigurationPath)]
        public int? Fps { get; set; }

        public const string DefaultCaptureFrameRateConfigurationPath =
            "Cs2VideoGenerator:DefaultCaptureFrameRate";

        /// <summary>
        ///     Playback speed of the clip: 1 (default) real time, 0.5 = 2x slow motion, 2 = 2x
        ///     fast-forward. Offline providers only; every slow-motion frame is really rendered.
        /// </summary>
        [Option("speed", Required = false,
            HelpText = "Playback speed: 1 = real time (default), 0.5 = 2x slow motion, 2 = 2x fast-forward " +
                       "(offline providers only)")]
        public double? Speed { get; set; }

        /// <summary>
        ///     With <c>--speed</c>, also write a real-time copy next to the clip
        ///     (<c>&lt;name&gt;.normalized.&lt;container&gt;</c>) - retimed losslessly with the
        ///     engine's original audio. Ignored at speed 1.
        /// </summary>
        [Option("normalize", Default = false,
            HelpText = "With --speed, also emit a real-time copy of the clip (lossless retime, original audio)")]
        public bool Normalize { get; set; }

        /// <summary>
        ///     Where the finished clip is written. Flows through configuration
        ///     (<c>Cs2VideoGenerator:OutputDirectory</c>) so the flag, an appsettings entry and the
        ///     bundled default ("captures") stack in the usual precedence.
        /// </summary>
        [Option("output-dir", Required = false,
            HelpText = "Directory to write the clip into (default: the configured " +
                       "Cs2VideoGenerator:OutputDirectory, out of the box 'captures/')")]
        [ConfigurationOverride(ConfigurationPath = OutputDirectoryConfigurationPath)]
        public string? OutputDir { get; set; }

        public const string OutputDirectoryConfigurationPath = "Cs2VideoGenerator:OutputDirectory";

        /// <summary>
        ///     Exact output file name (without extension), replacing the descriptive default that
        ///     encodes the capture configuration and a timestamp.
        /// </summary>
        [Option("output-name", Required = false,
            HelpText = "Output file name without extension (default: a descriptive name carrying the " +
                       "provider, resolution, fps, capture settings and a timestamp)")]
        public string? OutputName { get; set; }

        /// <inheritdoc />
        public override IReadOnlyList<string> Validate()
        {
            List<string> issues = [.. base.Validate()];

            if (StartTick < 0)
            {
                issues.Add("--start-tick must be zero or greater.");
            }

            if (EndTick != 0 && EndTick <= StartTick)
            {
                issues.Add("--end-tick must be greater than --start-tick.");
            }

            if (Width <= 0)
            {
                issues.Add("--width must be greater than zero.");
            }

            if (Height <= 0)
            {
                issues.Add("--height must be greater than zero.");
            }

            if (Fps is <= 0)
            {
                issues.Add("--fps must be greater than zero.");
            }

            if (Speed is <= 0)
            {
                issues.Add("--speed must be greater than zero (1 = real time, 0.5 = 2x slow motion).");
            }

            if (Normalize && (Speed is null || Math.Abs(Speed.Value - 1.0) <= 0.0001))
            {
                issues.Add("--normalize only applies with --speed other than 1; there is nothing to normalize.");
            }

            return issues;
        }

        public void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder)
        {
            // task-specific options only; the common ones flow through
            // ConfigurationOverride → IConfiguration
            hostBuilder.Services
                .Configure<Options>(opts =>
                {
                    opts.Demo = Demo;
                    opts.StartTick = StartTick;
                    opts.EndTick = EndTick;
                    opts.Width = Width;
                    opts.Height = Height;
                    opts.Fullscreen = Fullscreen;
                    opts.NoCapture = NoCapture;
                    opts.OutputDir = OutputDir;
                    opts.OutputName = OutputName;
                    opts.Fps = Fps;
                    opts.Speed = Speed;
                    opts.Normalize = Normalize;
                    opts.MockMode = MockMode;
                    opts.ExternalMockServerPath = ExternalMockServerPath;
                    opts.ExternalPluginPath = ExternalPluginPath;
                })
                .AddHostedService<WatchTask>();
        }
    }
}
