#region

using System.Reflection;
using System.Security.Cryptography;
using CommandLine;
using CommandLine.Text;
using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Steam;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

/// <summary>
///     Diagnostic task that displays configuration and validates that required dependencies can be found.
///     Launches neither CS2 nor a capture backend; the one process it does run is ffmpeg, asked for its
///     version and its encoder list, because what a local ffmpeg build can do cannot be inferred from
///     the file system.
/// </summary>
public sealed class DoctorTask(
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder,
    IOptions<DoctorTask.Options> taskOptions,
    IOptions<Cs2VideoGeneratorOptions> csvgOptions,
    IOptions<ObsCaptureConfiguration> obsOptions,
    IOptions<InEngineCaptureConfiguration> inEngineOptions,
    IConfiguration configuration,
    CachedPathDiscoveryService pathDiscoveryService,
    IBackupManager backupManager,
    INativeAssetProvider nativeAssetProvider,
    IFfmpegService ffmpegService,
    [FromKeyedServices(CaptureProviderNames.InEngine)]
    ICaptureSessionFactory inEngineCaptureFactory) : CliTaskBase(applicationLifetime, exitCodeHolder)
{
    protected override async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        bool verbose = taskOptions.Value.Verbose;
        bool noCache = taskOptions.Value.NoCache;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new FigletText("CSVG Doctor").Color(Color.Blue));
        AnsiConsole.WriteLine();

        if (noCache)
        {
            pathDiscoveryService.InvalidateCache();
            AnsiConsole.MarkupLine("[dim](Cache invalidated - using fresh discovery)[/]");
            AnsiConsole.WriteLine();
        }

        List<CheckResult> results = new();

        results.AddRange(CheckSteamConfiguration(verbose));

        results.AddRange(CheckCs2Configuration(verbose));

        results.AddRange(await CheckVideoCaptureProviderConfigurationAsync(verbose, cancellationToken));

        results.AddRange(CheckPluginConfiguration(verbose));

        results.AddRange(CheckBackupConfiguration(verbose));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Diagnostic Results[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Status[/]").Centered().Width(8))
            .AddColumn(new TableColumn("[bold]Check[/]"))
            .AddColumn(new TableColumn("[bold]Details[/]"))
            .AddColumn(new TableColumn("[bold]Source[/]"));

        foreach (CheckResult result in results)
        {
            string statusMarkup = result.Status switch
            {
                CheckStatus.Pass => "[green]PASS[/]",
                CheckStatus.Fail => "[red]FAIL[/]",
                CheckStatus.Warn => "[yellow]WARN[/]",
                CheckStatus.Skip => "[dim]SKIP[/]",
                _ => "[dim]?[/]"
            };

            string detailMarkup = result.Status switch
            {
                CheckStatus.Pass => $"[green]{Markup.Escape(result.Detail)}[/]",
                CheckStatus.Fail => $"[red]{Markup.Escape(result.Detail)}[/]",
                CheckStatus.Warn => $"[yellow]{Markup.Escape(result.Detail)}[/]",
                _ => $"[dim]{Markup.Escape(result.Detail)}[/]"
            };

            string sourceMarkup = result.Source is not null
                ? $"[dim]{Markup.Escape(result.Source)}[/]"
                : "";

            table.AddRow(statusMarkup, Markup.Escape(result.Name), detailMarkup, sourceMarkup);
        }

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        int passCount = results.Count(r => r.Status == CheckStatus.Pass);
        int failCount = results.Count(r => r.Status == CheckStatus.Fail);
        int warnCount = results.Count(r => r.Status == CheckStatus.Warn);
        int skipCount = results.Count(r => r.Status == CheckStatus.Skip);

        bool allPassed = failCount == 0;

        if (allPassed)
        {
            string summaryText = $"[green bold]All checks passed![/]\n\n" +
                                 $"[dim]{passCount} passed";
            if (warnCount > 0)
            {
                summaryText += $", {warnCount} warnings";
            }

            if (skipCount > 0)
            {
                summaryText += $", {skipCount} skipped";
            }

            summaryText += "[/]";

            AnsiConsole.Write(new Panel(new Markup(summaryText))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("green"),
                Header = new PanelHeader("[green] Summary [/]"),
                Padding = new Padding(2, 1)
            });
        }
        else
        {
            string summaryText = $"[red bold]Some checks failed[/]\n\n" +
                                 $"[dim]{passCount} passed, {failCount} failed";
            if (warnCount > 0)
            {
                summaryText += $", {warnCount} warnings";
            }

            if (skipCount > 0)
            {
                summaryText += $", {skipCount} skipped";
            }

            summaryText += "\n\nReview the failures above and update your configuration.[/]";

            AnsiConsole.Write(new Panel(new Markup(summaryText))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("red"),
                Header = new PanelHeader("[red] Summary [/]"),
                Padding = new Padding(2, 1)
            });
        }

        AnsiConsole.WriteLine();
        await Task.CompletedTask;

        return allPassed ? ExitCodes.Success : ExitCodes.OperationFailure;
    }

    private List<CheckResult> CheckSteamConfiguration(bool verbose)
    {
        List<CheckResult> results = new();
        Cs2VideoGeneratorOptions options = csvgOptions.Value;

        AnsiConsole.Write(new Rule("[blue]Steam Configuration[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        if (verbose)
        {
            Table configTable = NewConfigTable();

            configTable.AddRow("SteamInstallationDirectory",
                FormatConfigValue(options.SteamInstallationDirectory),
                GetConfigurationSource("Cs2VideoGenerator:SteamInstallationDirectory"));

            AnsiConsole.Write(configTable);
            AnsiConsole.WriteLine();
        }

        // explicit config first, cached discovery second
        string? steamPath;
        string source;

        if (!string.IsNullOrWhiteSpace(options.SteamInstallationDirectory))
        {
            steamPath = options.SteamInstallationDirectory;
            source = GetConfigurationSource("Cs2VideoGenerator:SteamInstallationDirectory");
        }
        else
        {
            steamPath = pathDiscoveryService.GetSteamInstallationPath();
            source = "Auto-discovery";
        }

        AnsiConsole.MarkupLine($"[cyan]Source:[/] {source}");
        AnsiConsole.WriteLine();

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            results.Add(new CheckResult("Steam installation", CheckStatus.Fail,
                "Not found in common installation locations", source));
            AnsiConsole.WriteLine();
            return results;
        }

        if (!Directory.Exists(steamPath))
        {
            results.Add(new CheckResult("Steam installation", CheckStatus.Fail,
                $"Path configured but not found: {steamPath}", source));
            AnsiConsole.WriteLine();
            return results;
        }

        results.Add(new CheckResult("Steam installation", CheckStatus.Pass, steamPath, source));

        IReadOnlyList<SteamUserInfo> users = SteamUtils.GetLoggedInUsers(steamPath);

        if (users.Count == 0)
        {
            results.Add(new CheckResult("Steam users", CheckStatus.Warn, "No logged in users found"));
            AnsiConsole.WriteLine();
            return results;
        }

        SteamUserInfo? mostRecentUser = SteamUtils.GetMostRecentUser(steamPath);

        if (mostRecentUser != null)
        {
            AnsiConsole.MarkupLine(
                $"[cyan]Current User:[/] {Markup.Escape(mostRecentUser.PersonaName ?? mostRecentUser.AccountName)}");

            Table userTable = new Table()
                .Border(TableBorder.Simple)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[dim]Property[/]"))
                .AddColumn(new TableColumn("[dim]Value[/]"));

            userTable.AddRow("Display Name", Markup.Escape(mostRecentUser.PersonaName ?? "(not set)"));
            userTable.AddRow("Account Name", Markup.Escape(mostRecentUser.AccountName));
            userTable.AddRow("SteamID64", mostRecentUser.SteamId64.ToString());
            userTable.AddRow("SteamID", Markup.Escape(mostRecentUser.SteamId));
            userTable.AddRow("SteamID3", Markup.Escape(mostRecentUser.SteamId3));

            if (mostRecentUser.LastLoginTimestamp.HasValue)
            {
                userTable.AddRow("Last Login", mostRecentUser.LastLoginTimestamp.Value.LocalDateTime.ToString("g"));
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(userTable);

            results.Add(new CheckResult("Current Steam user", CheckStatus.Pass, mostRecentUser.ToString()));

            // backup/restore both need this directory
            string? cs2ConfigDir = SteamUtils.GetCs2UserConfigDirectory(mostRecentUser, steamPath);
            if (!string.IsNullOrWhiteSpace(cs2ConfigDir))
            {
                results.Add(new CheckResult("CS2 user config", CheckStatus.Pass, cs2ConfigDir));
            }
            else
            {
                results.Add(new CheckResult("CS2 user config", CheckStatus.Warn,
                    "Not found - user may not have launched CS2"));
            }
        }
        else
        {
            results.Add(new CheckResult("Current Steam user", CheckStatus.Warn,
                "Could not determine most recent user"));
        }

        if (verbose && users.Count > 1)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]All logged in users ({users.Count}):[/]");

            Table allUsersTable = new Table()
                .Border(TableBorder.Simple)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[dim]Display Name[/]"))
                .AddColumn(new TableColumn("[dim]Account[/]"))
                .AddColumn(new TableColumn("[dim]SteamID64[/]"))
                .AddColumn(new TableColumn("[dim]Last Login[/]"))
                .AddColumn(new TableColumn("[dim]Most Recent[/]"));

            foreach (SteamUserInfo user in users)
            {
                allUsersTable.AddRow(
                    Markup.Escape(user.PersonaName ?? "-"),
                    Markup.Escape(user.AccountName),
                    user.SteamId64.ToString(),
                    user.LastLoginTimestamp?.LocalDateTime.ToString("g") ?? "-",
                    user.IsMostRecent ? "[green]Yes[/]" : "No"
                );
            }

            AnsiConsole.Write(allUsersTable);
        }

        AnsiConsole.WriteLine();
        return results;
    }

    private List<CheckResult> CheckCs2Configuration(bool verbose)
    {
        List<CheckResult> results = new();
        Cs2VideoGeneratorOptions options = csvgOptions.Value;

        AnsiConsole.Write(new Rule("[blue]CS2 Configuration[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        if (verbose)
        {
            Table configTable = NewConfigTable();

            configTable.AddRow("Cs2RootInstallationDirectory",
                FormatConfigValue(options.Cs2RootInstallationDirectory),
                GetConfigurationSource("Cs2VideoGenerator:Cs2RootInstallationDirectory"));
            configTable.AddRow("MockMode",
                options.MockMode ? "[green]Enabled[/]" : "[dim]Disabled[/]",
                GetConfigurationSource("Cs2VideoGenerator:MockMode"));
            configTable.AddRow("ExternalMockServerPath",
                FormatConfigValue(options.ExternalMockServerPath),
                GetConfigurationSource("Cs2VideoGenerator:ExternalMockServerPath"));
            configTable.AddRow("ExternalPluginPath",
                FormatConfigValue(options.ExternalPluginPath),
                GetConfigurationSource("Cs2VideoGenerator:ExternalPluginPath"));

            AnsiConsole.Write(configTable);
            AnsiConsole.WriteLine();
        }

        if (options.IsMockModeEffective)
        {
            AnsiConsole.MarkupLine("[cyan]Mode:[/] Mock CS2 Instance");
            AnsiConsole.WriteLine();

            string? mockServerPath = options.ExternalMockServerPath;

            if (!string.IsNullOrWhiteSpace(mockServerPath) && File.Exists(mockServerPath))
            {
                results.Add(new CheckResult("Mock executable", CheckStatus.Pass, mockServerPath,
                    GetConfigurationSource("Cs2VideoGenerator:ExternalMockServerPath")));
            }
            else if (!string.IsNullOrWhiteSpace(mockServerPath))
            {
                results.Add(new CheckResult("Mock executable", CheckStatus.Fail,
                    $"Not found: {mockServerPath}",
                    GetConfigurationSource("Cs2VideoGenerator:ExternalMockServerPath")));
            }
            else
            {
                results.Add(new CheckResult("Mock executable", CheckStatus.Pass,
                    "Using bundled mock_server (will be resolved at runtime)", "Bundled"));
            }

            return results;
        }

        AnsiConsole.MarkupLine("[cyan]Mode:[/] Standard CS2 Installation");

        try
        {
            string? cs2Root;
            string source;

            if (!string.IsNullOrWhiteSpace(options.Cs2RootInstallationDirectory))
            {
                cs2Root = options.Cs2RootInstallationDirectory;
                source = GetConfigurationSource("Cs2VideoGenerator:Cs2RootInstallationDirectory");
            }
            else
            {
                cs2Root = pathDiscoveryService.GetCs2RootDirectory();
                source = "Auto-discovery";
            }

            AnsiConsole.MarkupLine($"[cyan]Source:[/] {source}");
            AnsiConsole.WriteLine();

            if (!string.IsNullOrWhiteSpace(cs2Root) && Directory.Exists(cs2Root))
            {
                results.Add(new CheckResult("CS2 root directory", CheckStatus.Pass, cs2Root, source));

                try
                {
                    Cs2DirectoryUtil dirUtil = new(options);
                    string cs2Executable = dirUtil.Cs2ExecutablePath;

                    if (File.Exists(cs2Executable))
                    {
                        results.Add(new CheckResult("CS2 executable", CheckStatus.Pass, cs2Executable));
                    }
                    else
                    {
                        results.Add(new CheckResult("CS2 executable", CheckStatus.Fail, $"Not found: {cs2Executable}"));
                    }

                    if (File.Exists(dirUtil.Cs2GameInfoFilePath))
                    {
                        results.Add(new CheckResult("gameinfo.gi", CheckStatus.Pass, dirUtil.Cs2GameInfoFilePath));
                    }
                    else
                    {
                        results.Add(new CheckResult("gameinfo.gi", CheckStatus.Fail,
                            $"Not found: {dirUtil.Cs2GameInfoFilePath}"));
                    }

                    // Report the installed CS2 build (steam.inf ClientVersion) - the same value the
                    // plugin reports in the version handshake at session start.
                    string? steamInfPath = SteamInfReader.FindSteamInfPath(cs2Root);
                    string? gameBuild = steamInfPath is not null
                        ? SteamInfReader.ReadClientVersion(steamInfPath)
                        : null;
                    if (gameBuild is not null)
                    {
                        results.Add(new CheckResult("CS2 game build", CheckStatus.Pass,
                            $"{gameBuild} (steam.inf ClientVersion)"));
                    }
                    else
                    {
                        results.Add(new CheckResult("CS2 game build", CheckStatus.Warn,
                            "steam.inf not found or unreadable - game build unknown (compatibility check will warn and proceed)"));
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new CheckResult("CS2 directory structure", CheckStatus.Fail, ex.Message));
                }
            }
            else
            {
                results.Add(new CheckResult("CS2 root directory", CheckStatus.Fail,
                    "Not found in common installation locations", source));
            }
        }
        catch (CounterStrikeDirectoryNotFoundException ex)
        {
            results.Add(new CheckResult("CS2 installation", CheckStatus.Fail, ex.Message));
        }
        catch (Exception ex)
        {
            results.Add(new CheckResult("CS2 configuration", CheckStatus.Fail, ex.Message));
        }

        AnsiConsole.WriteLine();
        return results;
    }

    /// <summary>
    ///     Reports the selected capture provider and runs that provider's own checks. One case per
    ///     provider: each backend answers different questions (OBS: where is the executable; InEngine:
    ///     is there an ffmpeg and somewhere to put six megabytes a frame; InEngineHooked: the same, minus
    ///     the frame files, plus Windows), and a name no provider
    ///     answers to is a failed check rather than a crash - though options validation rejects an
    ///     unknown name before the diagnostics run at all.
    /// </summary>
    private async Task<List<CheckResult>> CheckVideoCaptureProviderConfigurationAsync(
        bool verbose,
        CancellationToken cancellationToken)
    {
        List<CheckResult> results = new();
        Cs2VideoGeneratorOptions options = csvgOptions.Value;

        AnsiConsole.Write(new Rule("[blue]Video Capture Provider[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[cyan]Provider:[/] {options.VideoCaptureProvider}");

        switch (options.VideoCaptureProvider)
        {
            case CaptureProviderNames.Obs:
                results.AddRange(CheckObsCaptureConfiguration(verbose));
                break;

            case CaptureProviderNames.InEngine:
                results.AddRange(await CheckInEngineCaptureConfigurationAsync(verbose, hooked: false,
                    cancellationToken));
                break;

            case CaptureProviderNames.InEngineHooked:
                results.AddRange(await CheckInEngineCaptureConfigurationAsync(verbose, hooked: true,
                    cancellationToken));
                break;

            default:
                AnsiConsole.WriteLine();
                results.Add(new CheckResult("Video provider", CheckStatus.Fail,
                    $"Unknown provider: {options.VideoCaptureProvider} " +
                    $"(known providers: {CaptureProviderNames.Format()})"));
                break;
        }

        return results;
    }

    /// <summary>
    ///     The in-engine provider's own diagnostics. Leads with what has and has not been observed on
    ///     a machine running CS2, because that outranks every other row here: the checks below can
    ///     all pass on a provider whose remaining assumptions about <c>startmovie</c> are wrong.
    ///     <paramref name="hooked" /> runs the same checks for <c>InEngineHooked</c>, which shares the
    ///     <c>Capture:InEngine</c> configuration but takes frames from the plugin's present hook: the
    ///     frame-file throughput row does not apply, and the platform and the plugin's
    ///     <c>present-capture</c> capability do.
    /// </summary>
    private async Task<List<CheckResult>> CheckInEngineCaptureConfigurationAsync(
        bool verbose,
        bool hooked,
        CancellationToken cancellationToken)
    {
        List<CheckResult> results = new();
        InEngineCaptureConfiguration config = inEngineOptions.Value;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(string.Join(Environment.NewLine + Environment.NewLine,
                    [
                        Markup.Escape(InEngineCaptureSessionFactory.UnverifiedNotice),
                        ..InEngineCaptureSessionFactory.UnverifiedFacts.Select(fact => "- " + Markup.Escape(fact))
                    ]))
                .Header("[yellow]InEngine: partially verified against real CS2[/]")
                .BorderColor(Color.Yellow));

        results.Add(new CheckResult("InEngine validation status", CheckStatus.Warn,
            "Verified against CS2 build 2000879 (both frame formats + game-time wav audio work end to end). " +
            "Cvar restore values are still unconfirmed",
            "docs/in-engine-capture.md"));

        if (verbose)
        {
            AnsiConsole.WriteLine();
            Table configTable = NewConfigTable();
            configTable.AddRow("FrameFormat", config.FrameFormat.ToString(),
                GetConfigurationSource("Capture:InEngine:FrameFormat"));
            configTable.AddRow("JpegQuality", FormatConfigValue(config.JpegQuality?.ToString()),
                GetConfigurationSource("Capture:InEngine:JpegQuality"));
            configTable.AddRow("IntermediateEncoder", config.IntermediateEncoder.ToString(),
                GetConfigurationSource("Capture:InEngine:IntermediateEncoder"));
            configTable.AddRow("IntermediateQuality", FormatConfigValue(config.IntermediateQuality?.ToString()),
                GetConfigurationSource("Capture:InEngine:IntermediateQuality"));
            configTable.AddRow("IntermediateEncoderPreset", FormatConfigValue(config.IntermediateEncoderPreset),
                GetConfigurationSource("Capture:InEngine:IntermediateEncoderPreset"));
            configTable.AddRow("StagingDirectory", FormatConfigValue(config.StagingDirectory),
                GetConfigurationSource("Capture:InEngine:StagingDirectory"));
            configTable.AddRow("AudioDirectory", FormatConfigValue(config.AudioDirectory),
                GetConfigurationSource("Capture:InEngine:AudioDirectory"));
            configTable.AddRow("MissingAudioBehavior", config.MissingAudioBehavior.ToString(),
                GetConfigurationSource("Capture:InEngine:MissingAudioBehavior"));
            configTable.AddRow("KeepEngineAudio", config.KeepEngineAudio.ToString(),
                GetConfigurationSource("Capture:InEngine:KeepEngineAudio"));
            configTable.AddRow("StartTickClamp", config.StartTickClamp.ToString(),
                GetConfigurationSource("Capture:InEngine:StartTickClamp"));
            configTable.AddRow("EngineOutput:TgaFrameSizeBytes",
                FormatConfigValue(config.EngineOutput.TgaFrameSizeBytes?.ToString()),
                GetConfigurationSource("Capture:InEngine:EngineOutput:TgaFrameSizeBytes"));
            AnsiConsole.Write(configTable);
        }

        AnsiConsole.WriteLine();

        IReadOnlyList<string> configurationIssues = config.Validate();
        results.Add(configurationIssues.Count == 0
            ? new CheckResult("InEngine configuration", CheckStatus.Pass, "Settings are self-consistent",
                "Capture:InEngine")
            : new CheckResult("InEngine configuration", CheckStatus.Fail, string.Join(" ", configurationIssues),
                "Capture:InEngine"));

        // ffmpeg is not optional for this provider: the engine writes images, and nothing else in CSVG
        // turns images into a video.
        FfmpegAvailabilityResult ffmpeg = await ffmpegService.CheckAvailabilityAsync(cancellationToken);
        results.Add(ffmpeg.IsAvailable
            ? new CheckResult("ffmpeg", CheckStatus.Pass, ffmpeg.Version ?? "available",
                ffmpeg.ExecutablePath ?? "PATH")
            : new CheckResult("ffmpeg", CheckStatus.Fail,
                $"Required by the InEngine provider and not found: {ffmpeg.ErrorMessage}", "PATH"));

        results.AddRange(await CheckInEngineDirectoriesAsync(config, cancellationToken));
        results.Add(await CheckInEngineEncoderAsync(config, ffmpeg.IsAvailable, cancellationToken));

        if (hooked)
        {
            results.Add(OperatingSystem.IsWindows()
                ? new CheckResult("InEngineHooked platform", CheckStatus.Pass, "Windows", "OS")
                : new CheckResult("InEngineHooked platform", CheckStatus.Fail,
                    "Windows only: frames come from the plugin's DXGI present hook. Use InEngine on this OS",
                    "OS"));
        }
        else
        {
            results.Add(CheckInEngineFrameThroughput(config));
        }

        // The provider's engine brackets are tick-scheduled; its cvar setup and its emergency stop are
        // not. Neither capability can be checked without a plugin connected, so this is a statement of
        // the requirement rather than a test of it. Both are required: a clip fails naming whichever is
        // missing.
        results.Add(new CheckResult("InEngine plugin capabilities", CheckStatus.Skip,
            $"Needs a plugin advertising '{CsvgCapabilities.TickScheduledCommands}' (recording brackets) and " +
            $"'{CsvgCapabilities.ConsoleExec}' (the cvar setup before recording, and stopping a failed clip's " +
            "recording)" +
            (hooked ? $", and '{CsvgCapabilities.PresentCapture}' (the frame stream)" : "") +
            "; checked at session start", "Plugin handshake"));

        return results;
    }

    /// <summary>
    ///     Where the frames and the wav are expected, and whether there is room for them. The staging
    ///     directory comes from the factory's own availability check, so the doctor and a real capture
    ///     resolve it the same way - including on a machine with no CS2, where resolution fails and is
    ///     reported rather than thrown.
    /// </summary>
    private async Task<List<CheckResult>> CheckInEngineDirectoriesAsync(
        InEngineCaptureConfiguration config,
        CancellationToken cancellationToken)
    {
        List<CheckResult> results = new();

        CaptureAvailabilityResult availability =
            await inEngineCaptureFactory.CheckAvailabilityAsync(cancellationToken);
        Dictionary<string, object> diagnostics = availability.DiagnosticInfo ?? new Dictionary<string, object>();

        string stagingSource = string.IsNullOrWhiteSpace(config.StagingDirectory)
            ? "CS2 installation"
            : GetConfigurationSource("Capture:InEngine:StagingDirectory");

        if (diagnostics.TryGetValue("stagingDirectory", out object? stagingValue) &&
            stagingValue is string staging)
        {
            results.Add(Directory.Exists(staging)
                ? new CheckResult("InEngine staging directory", CheckStatus.Pass, staging, stagingSource)
                : new CheckResult("InEngine staging directory", CheckStatus.Fail,
                    $"Could not be created: {staging}", stagingSource));

            results.Add(CheckFreeSpace(staging, config));
        }
        else
        {
            results.Add(new CheckResult("InEngine staging directory", CheckStatus.Fail,
                "Could not be resolved - set \"Capture:InEngine:StagingDirectory\" when CS2 is not installed " +
                "where CSVG can find it", stagingSource));
        }

        string audioSource = string.IsNullOrWhiteSpace(config.AudioDirectory)
            ? "CS2 installation"
            : GetConfigurationSource("Capture:InEngine:AudioDirectory");

        if (diagnostics.TryGetValue("audioDirectory", out object? audioValue) && audioValue is string audio)
        {
            results.Add(Directory.Exists(audio)
                ? new CheckResult("InEngine movie audio directory", CheckStatus.Pass, audio, audioSource)
                : new CheckResult("InEngine movie audio directory", CheckStatus.Warn,
                    $"Does not exist yet: {audio} (the engine creates it on the first recording)", audioSource));
        }
        else
        {
            results.Add(new CheckResult("InEngine movie audio directory", CheckStatus.Warn,
                "Could not be resolved - clips that ask for audio will take the MissingAudioBehavior path",
                audioSource));
        }

        return results;
    }

    /// <summary>
    ///     Whether the staging volume has room for the render. There is no clip length here to size it
    ///     against, so the check reports what a minute of capture would take at the configured format
    ///     and flags a volume that could not hold it.
    /// </summary>
    private static CheckResult CheckFreeSpace(string staging, InEngineCaptureConfiguration config)
    {
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(staging)) ?? staging);
            long free = drive.AvailableFreeSpace;

            long perMinute = ReferenceBytesPerSecond(config) * 60;
            string detail =
                $"{FormatFileSize(free)} free; about {FormatFileSize(perMinute)} per minute of capture at " +
                $"{config.FrameFormat} 1080p60";

            return free < perMinute
                ? new CheckResult("InEngine staging free space", CheckStatus.Warn,
                    detail + " - not enough for a one-minute clip", drive.Name)
                : new CheckResult("InEngine staging free space", CheckStatus.Pass, detail, drive.Name);
        }
        catch (Exception ex)
        {
            return new CheckResult("InEngine staging free space", CheckStatus.Skip,
                $"Could not be measured: {ex.Message}", staging);
        }
    }

    /// <summary>
    ///     Which encoder the intermediate encode would actually use. <c>Auto</c> falling back to
    ///     software H.264 is worth a warning: at a fixed timestep the render waits on the encoder, so
    ///     the fallback does not slow the capture down a little, it decides how long it takes.
    /// </summary>
    private async Task<CheckResult> CheckInEngineEncoderAsync(
        InEngineCaptureConfiguration config,
        bool ffmpegAvailable,
        CancellationToken cancellationToken)
    {
        const string name = "InEngine intermediate encoder";
        string source = GetConfigurationSource("Capture:InEngine:IntermediateEncoder");

        if (!ffmpegAvailable)
        {
            return new CheckResult(name, CheckStatus.Skip, "ffmpeg is not available to probe", source);
        }

        FfmpegEncoderProbeResult probe = await ffmpegService.ProbeEncodersAsync(cancellationToken);
        if (!probe.Success)
        {
            return new CheckResult(name, CheckStatus.Warn,
                $"Could not probe this ffmpeg build's encoders: {probe.ErrorMessage}", source);
        }

        if (config.IntermediateEncoder != ImagePipeEncoder.Auto)
        {
            string requested = FfmpegService.EncoderName(config.IntermediateEncoder);
            return probe.Has(requested)
                ? new CheckResult(name, CheckStatus.Pass,
                    requested + DescribePreset(config, config.IntermediateEncoder), source)
                : new CheckResult(name, CheckStatus.Fail,
                    $"'{requested}' was configured but this ffmpeg build does not have it", source);
        }

        string nvenc = FfmpegService.EncoderName(ImagePipeEncoder.Nvenc);
        string x264 = FfmpegService.EncoderName(ImagePipeEncoder.X264);

        return probe.Has(nvenc)
            ? new CheckResult(name, CheckStatus.Pass,
                $"Auto resolves to {nvenc}{DescribePreset(config, ImagePipeEncoder.Nvenc)}", source)
            : new CheckResult(name, CheckStatus.Warn,
                $"Auto falls back to {x264}{DescribePreset(config, ImagePipeEncoder.X264)} - no hardware H.264 " +
                "in this ffmpeg build. Offline capture waits on the encoder, so expect the render to run at " +
                "software-encode speed", source);
    }

    /// <summary>
    ///     The preset the encode would actually run at, spelled out because it is a speed-for-quality
    ///     trade the user is entitled to see before a long render rather than after one. Resolved
    ///     through <see cref="FfmpegService.ResolvePreset" /> so the doctor cannot report a default the
    ///     encoder no longer uses; blank for an encoder that has no preset at all.
    /// </summary>
    private static string DescribePreset(InEngineCaptureConfiguration config, ImagePipeEncoder encoder) =>
        FfmpegService.ResolvePreset(encoder, config.IntermediateEncoderPreset) is { } preset
            ? $" (preset {preset}{(string.IsNullOrWhiteSpace(config.IntermediateEncoderPreset) ? ", default" : "")})"
            : "";

    /// <summary>
    ///     What the chosen frame format asks of the disk. Targa is lossless and enormous - 1080p60 is
    ///     roughly 500 MB/s, sustained, which a mechanical disk cannot do and a SATA SSD cannot either.
    /// </summary>
    private CheckResult CheckInEngineFrameThroughput(InEngineCaptureConfiguration config)
    {
        const string name = "InEngine frame throughput";
        string source = GetConfigurationSource("Capture:InEngine:FrameFormat");
        long perSecond = ReferenceBytesPerSecond(config);

        if (config.FrameFormat != InEngineFrameFormat.Tga)
        {
            return new CheckResult(name, CheckStatus.Pass,
                $"About {FormatFileSize(perSecond)}/s at 1080p60 (JPEG frames, estimated) - any SSD keeps up",
                source);
        }

        long frameBytes = config.EngineOutput.TgaFrameSizeBytes
                          ?? InEngineCaptureConfiguration.UncompressedTgaFrameSize(1920, 1080);

        return new CheckResult(name, CheckStatus.Warn,
            $"Targa writes {FormatFileSize(frameBytes)} per frame, about {FormatFileSize(perSecond)}/s at 60 " +
            "fps, sustained for the whole render. Use an NVMe drive (or a tmpfs on Linux) for the staging " +
            "directory, or switch to FrameFormat Jpg", source);
    }

    /// <summary>
    ///     Bytes per second the staging directory has to absorb at a reference 1080p60, for the disk
    ///     advice. The JPEG figure is an estimate - the engine's compression is not ours to predict -
    ///     and is marked as such wherever it is shown.
    /// </summary>
    private static long ReferenceBytesPerSecond(InEngineCaptureConfiguration config)
    {
        const int referenceFrameRate = 60;
        const long estimatedJpegFrameBytes = 400 * 1024;

        if (config.FrameFormat != InEngineFrameFormat.Tga)
        {
            return estimatedJpegFrameBytes * referenceFrameRate;
        }

        long frameBytes = config.EngineOutput.TgaFrameSizeBytes
                          ?? InEngineCaptureConfiguration.UncompressedTgaFrameSize(1920, 1080);
        return frameBytes * referenceFrameRate;
    }

    /// <summary>
    ///     The OBS provider's own diagnostics: its bound <c>Capture:OBS</c> configuration (verbose
    ///     only) and whether the OBS executable can be found where it says, or by discovery.
    /// </summary>
    private List<CheckResult> CheckObsCaptureConfiguration(bool verbose)
    {
        List<CheckResult> results = new();
        ObsCaptureConfiguration obsConfig = obsOptions.Value;

        if (verbose)
        {
            AnsiConsole.WriteLine();
            Table configTable = NewConfigTable();

            configTable.AddRow("ExecutablePath (override)",
                FormatConfigValue(obsConfig.ExecutablePath),
                GetConfigurationSource("Capture:OBS:ExecutablePath"));
            configTable.AddRow("Host", obsConfig.Host,
                GetConfigurationSource("Capture:OBS:Host"));
            configTable.AddRow("Port", obsConfig.Port.ToString(),
                GetConfigurationSource("Capture:OBS:Port"));
            configTable.AddRow("ProfileName", FormatConfigValue(obsConfig.ProfileName),
                GetConfigurationSource("Capture:OBS:ProfileName"));
            configTable.AddRow("SceneCollectionName", FormatConfigValue(obsConfig.SceneCollectionName),
                GetConfigurationSource("Capture:OBS:SceneCollectionName"));
            configTable.AddRow("AutoStart", obsConfig.AutoStart.ToString(),
                GetConfigurationSource("Capture:OBS:AutoStart"));
            configTable.AddRow("StartMinimized", obsConfig.StartMinimized.ToString(),
                GetConfigurationSource("Capture:OBS:StartMinimized"));

            AnsiConsole.Write(configTable);
        }

        AnsiConsole.WriteLine();

        string? obsPath;
        string source;

        if (!string.IsNullOrWhiteSpace(obsConfig.ExecutablePath))
        {
            obsPath = obsConfig.ExecutablePath;
            source = GetConfigurationSource("Capture:OBS:ExecutablePath");
        }
        else
        {
            obsPath = pathDiscoveryService.GetObsExecutablePath();
            source = "Auto-discovery";
        }

        AnsiConsole.MarkupLine($"[cyan]Source:[/] {source}");
        AnsiConsole.WriteLine();

        if (!string.IsNullOrWhiteSpace(obsPath) && File.Exists(obsPath))
        {
            results.Add(new CheckResult("OBS executable", CheckStatus.Pass, obsPath, source));
        }
        else if (!string.IsNullOrWhiteSpace(obsPath))
        {
            results.Add(new CheckResult("OBS executable", CheckStatus.Fail,
                $"Path configured but not found: {obsPath}", source));
        }
        else
        {
            results.Add(new CheckResult("OBS executable", CheckStatus.Fail,
                "Not found in common installation locations", source));
        }

        return results;
    }

    private List<CheckResult> CheckPluginConfiguration(bool verbose)
    {
        List<CheckResult> results = new();
        Cs2VideoGeneratorOptions options = csvgOptions.Value;

        AnsiConsole.Write(new Rule("[blue]Plugin Configuration[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        // Report the CSVG package version - the bundled plugin embeds the same repo version and
        // reports it in the session handshake.
        results.Add(new CheckResult("CSVG version", CheckStatus.Pass, GetCsvgVersion(), "Assembly"));

        if (options.IsMockModeEffective)
        {
            results.Add(new CheckResult("Plugin management", CheckStatus.Skip, "Disabled in mock mode"));
            return results;
        }

        if (verbose && !string.IsNullOrWhiteSpace(options.ExternalPluginPath))
        {
            AnsiConsole.MarkupLine($"[cyan]ExternalPluginPath:[/] {options.ExternalPluginPath}");
        }

        AnsiConsole.WriteLine();

        string pluginSource = !string.IsNullOrWhiteSpace(options.ExternalPluginPath)
            ? GetConfigurationSource("Cs2VideoGenerator:ExternalPluginPath")
            : "Bundled";

        if (!string.IsNullOrWhiteSpace(options.ExternalPluginPath))
        {
            if (File.Exists(options.ExternalPluginPath))
            {
                results.Add(new CheckResult("Plugin source (external)", CheckStatus.Pass,
                    options.ExternalPluginPath, pluginSource));
            }
            else if (Directory.Exists(options.ExternalPluginPath))
            {
                string expectedDll = Path.Combine(options.ExternalPluginPath, "server.dll");
                string expectedSo = Path.Combine(options.ExternalPluginPath, "server.so");

                if (File.Exists(expectedDll))
                {
                    results.Add(new CheckResult("Plugin source (server.dll)", CheckStatus.Pass,
                        expectedDll, pluginSource));
                }
                else if (File.Exists(expectedSo))
                {
                    results.Add(new CheckResult("Plugin source (server.so)", CheckStatus.Pass,
                        expectedSo, pluginSource));
                }
                else
                {
                    results.Add(new CheckResult("Plugin source", CheckStatus.Fail,
                        "Directory exists but no server.dll or server.so found", pluginSource));
                }
            }
            else
            {
                results.Add(new CheckResult("Plugin source", CheckStatus.Fail,
                    $"Not found: {options.ExternalPluginPath}", pluginSource));
            }
        }
        else
        {
            // Using bundled plugin - resolve it the same way the session start does and report
            // its presence and content hash so support can match a report to a shipped binary.
            string? bundledPlugin = nativeAssetProvider.ServerPluginPath;
            if (bundledPlugin is not null)
            {
                results.Add(new CheckResult("Plugin source (bundled)", CheckStatus.Pass,
                    $"{bundledPlugin}{DescribePluginFileHash(bundledPlugin)}", pluginSource));
            }
            else
            {
                results.Add(new CheckResult("Plugin source (bundled)", CheckStatus.Fail,
                    "Bundled server plugin not found for this platform (runtimes/<rid>/native)", pluginSource));
            }
        }

        return results;
    }

    /// <summary>Informational version of the Core assembly, not the CLI's.</summary>
    private static string GetCsvgVersion()
    {
        Assembly coreAssembly = typeof(Cs2VideoGeneratorOptions).Assembly;
        return coreAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? coreAssembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    /// <summary>Empty string when the file cannot be read; this detail is best-effort.</summary>
    private static string DescribePluginFileHash(string pluginPath)
    {
        try
        {
            using FileStream stream = File.Open(pluginPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return $" (SHA-256 {hash[..16]}...)";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private List<CheckResult> CheckBackupConfiguration(bool verbose)
    {
        List<CheckResult> results = new();

        AnsiConsole.Write(new Rule("[blue]Backup Configuration[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        string centralBackupDir = backupManager.CentralBackupDirectory;

        AnsiConsole.MarkupLine($"[cyan]Central Backup Directory:[/] {Markup.Escape(centralBackupDir)}");
        AnsiConsole.WriteLine();

        if (!Directory.Exists(centralBackupDir))
        {
            results.Add(new CheckResult("Central backup directory", CheckStatus.Warn,
                "Not yet created (will be created on first backup)"));
            return results;
        }

        results.Add(new CheckResult("Central backup directory", CheckStatus.Pass, centralBackupDir));

        IReadOnlyDictionary<string, BackupIndexEntry>? backupIndex = backupManager.GetBackupIndex();
        if (backupIndex.Count == 0)
        {
            backupIndex = null;
        }

        List<string?> backupEntries = Directory.GetFileSystemEntries(centralBackupDir)
            .Where(e => Path.GetFileName(e) != "backup-index.json")
            .Select(Path.GetFileName)
            .ToList();

        int indexedCount = backupIndex?.Count ?? 0;
        int physicalCount = backupEntries.Count;

        if (indexedCount == 0 && physicalCount == 0)
        {
            AnsiConsole.MarkupLine("[dim]No backups stored[/]");
            results.Add(new CheckResult("Backup status", CheckStatus.Pass, "No backups (clean state)"));
        }
        else
        {
            results.Add(new CheckResult("Indexed backups", CheckStatus.Pass, $"{indexedCount} entries"));

            if (backupIndex != null)
            {
                List<string> orphanedEntries = backupIndex.Keys
                    .Where(k => !backupEntries.Contains(k))
                    .ToList();

                List<string?> unindexedEntries = backupEntries
                    .Where(e => !backupIndex.ContainsKey(e!))
                    .ToList();

                if (orphanedEntries.Count > 0)
                {
                    results.Add(new CheckResult("Orphaned index entries", CheckStatus.Warn,
                        $"{orphanedEntries.Count} entries in index but missing on disk"));
                }

                if (unindexedEntries.Count > 0)
                {
                    results.Add(new CheckResult("Unindexed backups", CheckStatus.Warn,
                        $"{unindexedEntries.Count} backups on disk but not in index"));
                }
            }

            if (verbose && backupIndex != null && backupIndex.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Indexed backups:[/]");

                Table backupTable = new Table()
                    .Border(TableBorder.Simple)
                    .BorderColor(Color.Grey)
                    .AddColumn(new TableColumn("[dim]Backup Name[/]"))
                    .AddColumn(new TableColumn("[dim]Original Path[/]"))
                    .AddColumn(new TableColumn("[dim]Backed Up[/]"))
                    .AddColumn(new TableColumn("[dim]Status[/]"));

                foreach ((string backupName, BackupIndexEntry entry) in backupIndex.OrderByDescending(e =>
                             e.Value.BackedUpAt))
                {
                    string backupPath = Path.Combine(centralBackupDir, backupName);
                    bool exists = File.Exists(backupPath) || Directory.Exists(backupPath);
                    string status = exists ? "[green]OK[/]" : "[red]Missing[/]";

                    string displayPath = entry.OriginalPath.Length > 60
                        ? "..." + entry.OriginalPath[^57..]
                        : entry.OriginalPath;

                    backupTable.AddRow(
                        Markup.Escape(backupName),
                        Markup.Escape(displayPath),
                        entry.BackedUpAt.LocalDateTime.ToString("g"),
                        status
                    );
                }

                AnsiConsole.Write(backupTable);
            }

            if (verbose)
            {
                AnsiConsole.WriteLine();
                try
                {
                    List<string> allFiles = Directory.GetFiles(centralBackupDir, "*", SearchOption.AllDirectories)
                        .Where(f => Path.GetFileName(f) != "backup-index.json")
                        .ToList();

                    long totalSize = allFiles.Sum(f => new FileInfo(f).Length);
                    string sizeDisplay = FormatFileSize(totalSize);

                    Table statsTable = new Table()
                        .Border(TableBorder.Simple)
                        .BorderColor(Color.Grey)
                        .AddColumn(new TableColumn("[dim]Storage Statistics[/]"))
                        .AddColumn(new TableColumn("[dim]Value[/]"));

                    statsTable.AddRow("Total files", allFiles.Count.ToString());
                    statsTable.AddRow("Total size", sizeDisplay);

                    if (allFiles.Count > 0)
                    {
                        DateTime oldestFile = allFiles.Min(f => new FileInfo(f).LastWriteTime);
                        DateTime newestFile = allFiles.Max(f => new FileInfo(f).LastWriteTime);
                        statsTable.AddRow("Oldest backup", oldestFile.ToString("g"));
                        statsTable.AddRow("Newest backup", newestFile.ToString("g"));
                    }

                    AnsiConsole.Write(statsTable);
                }
                catch
                {
                    // Ignore errors reading backup stats
                }
            }
        }

        AnsiConsole.WriteLine();
        return results;
    }

    private string GetConfigurationSource(string configurationKey)
    {
        if (configuration is not IConfigurationRoot configRoot)
            return "Unknown";

        // reverse order: the last registered provider wins in .NET configuration
        foreach (IConfigurationProvider provider in configRoot.Providers.Reverse())
        {
            if (provider.TryGet(configurationKey, out _))
            {
                return GetFriendlyProviderName(provider);
            }
        }

        return "Default";
    }

    private static string GetFriendlyProviderName(IConfigurationProvider provider)
    {
        string typeName = provider.GetType().Name;

        if (typeName.Contains("CommandLine"))
            return "Command Line";

        if (typeName.Contains("EnvironmentVariables"))
            return "Environment Variable";

        if (typeName.Contains("Memory"))
            return "In-Memory";

        // File-based providers (JSON, XML, INI) expose a Source.Path property
        object? source = provider.GetType().GetProperty("Source")?.GetValue(provider);
        if (source?.GetType().GetProperty("Path")?.GetValue(source) is string filePath &&
            !string.IsNullOrEmpty(filePath))
            return Path.GetFileName(filePath);

        return typeName;
    }

    private static Table NewConfigTable() =>
        new Table()
            .Border(TableBorder.Simple)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[dim]Setting[/]"))
            .AddColumn(new TableColumn("[dim]Value[/]"))
            .AddColumn(new TableColumn("[dim]Source[/]"));

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private static string FormatConfigValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "[dim](not set)[/]" : value;

    [Verb("doctor", HelpText = "Diagnose configuration and validate that required dependencies can be found")]
    public sealed class Options : Cs2ProcessOptions, IRegisterTaskAndOptions
    {
        [Usage(ApplicationAlias = "csvg")]
        public static IEnumerable<Example> Examples =>
        [
            new("Run diagnostics", new Options()),
            new("Show detailed configuration", new Options { Verbose = true }),
            new("Force fresh path discovery", new Options { NoCache = true }),
            new("Check mock mode configuration", new Options { MockMode = true })
        ];

        [Option('v', "verbose", Default = false, HelpText = "Show detailed configuration values")]
        public bool Verbose { get; set; }

        [Option("no-cache", Default = false, HelpText = "Skip cached paths and force fresh discovery")]
        public bool NoCache { get; set; }

        public void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder)
        {
            hostBuilder.Services.Configure<Options>(opts =>
            {
                opts.Verbose = Verbose;
                opts.NoCache = NoCache;
                // Common options from base class
                opts.MockMode = MockMode;
                opts.ExternalMockServerPath = ExternalMockServerPath;
                opts.ExternalPluginPath = ExternalPluginPath;
            });

            hostBuilder.Services.AddHostedService<DoctorTask>();
        }
    }

    private enum CheckStatus
    {
        Pass,
        Fail,
        Warn,
        Skip
    }

    private record CheckResult(string Name, CheckStatus Status, string Detail, string? Source = null);
}
