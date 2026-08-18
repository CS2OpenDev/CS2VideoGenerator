#region

using System.Text.Json;
using CommandLine;
using CommandLine.Text;
using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Progress;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using Microsoft.Extensions.Options;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

public sealed class GenerateTask(
    ILogger<GenerateTask> logger,
    CsvgVideoSession videoSession,
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder,
    IOptions<GenerateTask.Options> options) : CliTaskBase(applicationLifetime, exitCodeHolder)
{
    protected override async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        WriteTaskHeader("CS2 Video Generation");

        Cs2Compilation? compilation = await LoadCompilationAsync();
        if (compilation is null)
        {
            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> validationIssues = compilation.Validate();
        if (validationIssues.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Compilation validation failed:[/]");
            foreach (string issue in validationIssues)
            {
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(issue)}");
            }

            return ExitCodes.UsageError;
        }

        DisplayCompilationSummary(compilation);

        int exitCode;

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Starting CS2 session...", async ctx =>
                {
                    await videoSession.StartAsync(
                        new EngineSessionOptions
                        {
                            Width = compilation.Settings.Width,
                            Height = compilation.Settings.Height,
                            Fullscreen = compilation.Settings.Fullscreen
                        },
                        initializeCapture: true,
                        cancellationToken);
                });

            AnsiConsole.MarkupLine("[green]✓[/] CS2 session started");
            AnsiConsole.WriteLine();

            // live per-clip progress
            AnsiConsole.MarkupLine($"[cyan]Capturing {compilation.Clips.Count} clip(s)...[/]");
            AnsiConsole.WriteLine();

            Cs2CompilationResult result = await GenerationProgressReporter.CaptureWithProgressAsync(
                AnsiConsole.Console, videoSession, compilation, cancellationToken);

            AnsiConsole.WriteLine();
            DisplayResults(result);

            exitCode = result.Success ? ExitCodes.Success : ExitCodes.OperationFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Generation cancelled[/]");
            exitCode = ExitCodes.OperationFailure;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Generation failed:[/] {Markup.Escape(ex.Message)}");
            logger.LogError(ex, "Generation failed");
            exitCode = ExitCodes.OperationFailure;
        }
        finally
        {
            await AnsiConsole.Status()
                .StartAsync("Stopping CS2 session...",
                    async _ => { await videoSession.StopAsync(CancellationToken.None); });
            AnsiConsole.MarkupLine("[green]✓[/] CS2 session stopped");
        }

        return exitCode;
    }

    private async Task<Cs2Compilation?> LoadCompilationAsync()
    {
        Options opts = options.Value;

        if (string.IsNullOrWhiteSpace(opts.CompilationFile) && string.IsNullOrWhiteSpace(opts.CompilationJson))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No compilation definition provided.");
            AnsiConsole.MarkupLine(
                "Use [cyan]--file[/] to specify a JSON file or [cyan]--compilation[/] to provide JSON directly.");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Example:[/]");
            AnsiConsole.MarkupLine("  csvg generate --file compilation.json");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(opts.CompilationFile) && !string.IsNullOrWhiteSpace(opts.CompilationJson))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Both --file and --compilation provided. Please use only one.");
            return null;
        }

        string json;

        if (!string.IsNullOrWhiteSpace(opts.CompilationFile))
        {
            if (!File.Exists(opts.CompilationFile))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Compilation file not found: {Markup.Escape(opts.CompilationFile)}");
                return null;
            }

            AnsiConsole.MarkupLine($"[cyan]Loading compilation from:[/] {Markup.Escape(opts.CompilationFile)}");
            json = await File.ReadAllTextAsync(opts.CompilationFile);
        }
        else
        {
            json = opts.CompilationJson!;
        }

        try
        {
            Cs2Compilation? compilation = JsonSerializer.Deserialize<Cs2Compilation>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (compilation is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse compilation definition - result was null");
                return null;
            }

            return compilation;
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid JSON in compilation definition");
            if (ex.LineNumber.HasValue)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]  Line {ex.LineNumber + 1}, Column {ex.BytePositionInLine + 1}[/]");
            }

            if (ex.Path is not null)
            {
                AnsiConsole.MarkupLine($"[dim]  Path: {Markup.Escape(ex.Path)}[/]");
            }

            AnsiConsole.MarkupLine($"[dim]  {Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }

    private static void DisplayCompilationSummary(Cs2Compilation compilation)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Compilation Summary[/]")
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Output Directory", Markup.Escape(compilation.Settings.OutputDirectory));
        table.AddRow("Base Filename", Markup.Escape(compilation.Settings.BaseFileName));
        table.AddRow("Format", compilation.Settings.ContainerFormat);
        table.AddRow("Frame Rate", $"{compilation.Settings.FrameRate} fps");

        if (compilation.Settings.Width.HasValue && compilation.Settings.Height.HasValue)
        {
            table.AddRow("Resolution", $"{compilation.Settings.Width}x{compilation.Settings.Height}");
        }

        table.AddRow("Fullscreen", compilation.Settings.Fullscreen ? "Yes" : "No");
        table.AddRow("Clips", compilation.Clips.Count.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        Table clipsTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Clips[/]")
            .AddColumn("#")
            .AddColumn("Player")
            .AddColumn("Ticks")
            .AddColumn("Demo");

        for (int i = 0; i < compilation.Clips.Count; i++)
        {
            Cs2CompilationClip clip = compilation.Clips[i];
            string demoName = Path.GetFileName(clip.DemoFilePath);
            clipsTable.AddRow(
                i.ToString(),
                Markup.Escape(clip.PlayerNameToSpectate),
                $"{clip.StartTick} - {clip.EndTick}",
                Markup.Escape(demoName.Length > 40 ? "..." + demoName[^37..] : demoName)
            );
        }

        AnsiConsole.Write(clipsTable);
        AnsiConsole.WriteLine();
    }

    private static void DisplayResults(Cs2CompilationResult result)
    {
        if (result.Success)
        {
            string successMessage = $"[green]Successfully captured {result.CompletedClips}/{result.TotalClips} clip(s)[/]";

            if (result.ConcatenationResult is { Success: true })
            {
                successMessage +=
                    $"\n[cyan]Output:[/] {Markup.Escape(Path.GetFileName(result.ConcatenatedOutputPath ?? ""))}";
            }

            AnsiConsole.Write(
                new Panel(successMessage)
                {
                    Border = BoxBorder.Rounded, BorderStyle = Style.Parse("green")
                });
        }
        else
        {
            string errorMessage =
                $"[red]Compilation failed: {Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]\n[yellow]Completed: {result.CompletedClips}/{result.TotalClips} clips[/]";

            AnsiConsole.Write(
                new Panel(errorMessage)
                {
                    Border = BoxBorder.Rounded, BorderStyle = Style.Parse("red")
                });
        }

        AnsiConsole.WriteLine();

        if (result.ClipResults.Count > 0)
        {
            Table resultsTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .Title("[bold]Clip Results[/]")
                .AddColumn("#")
                .AddColumn("Status")
                .AddColumn("Output");

            foreach (Cs2ClipResult clipResult in result.ClipResults)
            {
                string status = clipResult.Success ? "[green]✓[/]" : "[red]✗[/]";
                string output = clipResult.Success
                    ? Markup.Escape(Path.GetFileName(clipResult.OutputFilePath))
                    : Markup.Escape(clipResult.ErrorMessage ?? "Failed");

                resultsTable.AddRow(
                    clipResult.ClipIndex.ToString(),
                    status,
                    output
                );
            }

            AnsiConsole.Write(resultsTable);
        }

        if (result.ConcatenationResult != null)
        {
            AnsiConsole.WriteLine();

            if (result.ConcatenationResult.Success)
            {
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] Concatenated output: [cyan]{Markup.Escape(result.ConcatenatedOutputPath ?? "")}[/]");
                AnsiConsole.MarkupLine(
                    $"[dim]  Processing time: {result.ConcatenationResult.ProcessingDuration.TotalSeconds:F1}s[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]![/] Concatenation failed: {Markup.Escape(result.ConcatenationResult.ErrorMessage ?? "Unknown error")}");
                AnsiConsole.MarkupLine("[dim]  Individual clip files are still available[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    [Verb("generate", HelpText = "Generate video clips from CS2 demo files based on a compilation definition")]
    public sealed class Options : CommonOptions, IRegisterTaskAndOptions
    {
        [Usage(ApplicationAlias = "csvg")]
        public static IEnumerable<Example> Examples =>
        [
            new("Generate from a compilation file", new Options { CompilationFile = "compilation.json" }),
            new("Generate in mock mode",
                new Options { CompilationFile = "compilation.json", MockMode = true })
        ];

        // Task-specific options only - mock options inherited from CommonOptions

        [Option('f', "file", Required = false, HelpText = "Path to a JSON file containing the compilation definition")]
        public string? CompilationFile { get; set; }

        [Option('c', "compilation", Required = false,
            HelpText = "JSON string containing the compilation definition (alternative to --file)")]
        public string? CompilationJson { get; set; }

        public void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder)
        {
            // Common options flow through ConfigurationOverride → IConfiguration
            hostBuilder.Services.Configure<Options>(opts =>
            {
                opts.CompilationFile = CompilationFile;
                opts.CompilationJson = CompilationJson;
                opts.MockMode = MockMode;
                opts.ExternalMockServerPath = ExternalMockServerPath;
                opts.ExternalPluginPath = ExternalPluginPath;
            });

            hostBuilder.Services.AddHostedService<GenerateTask>();
        }
    }
}
