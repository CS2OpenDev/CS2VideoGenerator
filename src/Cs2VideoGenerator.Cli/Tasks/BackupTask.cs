#region

using CommandLine;
using CommandLine.Text;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Options;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

public sealed class BackupTask(
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder,
    IOptions<BackupTask.Options> taskOptions,
    IOptions<Cs2VideoGeneratorOptions> csvgOptions,
    Cs2DirectoryUtil directoryUtil,
    IBackupManager backupManager) : CliTaskBase(applicationLifetime, exitCodeHolder)
{
    protected override async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Options opts = taskOptions.Value;

        WriteTaskHeader("CS2 Backup");

        if (csvgOptions.Value.IsMockModeEffective)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Backup is not available in mock mode — there are no real game files to back up.[/]");
            return ExitCodes.Success;
        }

        if (opts.GameFilesOnly && opts.UserSettingsOnly)
        {
            AnsiConsole.MarkupLine("[red]Cannot specify both --game-files and --user-settings[/]");
            return ExitCodes.UsageError;
        }

        List<(string Path, string Description)> pathsToBackup = new();

        if (!opts.UserSettingsOnly)
        {
            foreach (Cs2GameFileChanges item in directoryUtil.PathsThatGetModified.Where(f => f.ShouldBeBackedUp))
            {
                if (File.Exists(item.Path) || Directory.Exists(item.Path))
                {
                    pathsToBackup.Add((item.Path, "Game file"));
                }
            }
        }

        if (!opts.GameFilesOnly)
        {
            if (directoryUtil.CanBackupUserSettings)
            {
                string? userConfigDir = directoryUtil.UserConfigDirectory;
                if (!string.IsNullOrWhiteSpace(userConfigDir))
                {
                    pathsToBackup.Add((userConfigDir, "User settings"));
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]User settings directory not found - skipping[/]");
            }
        }

        if (pathsToBackup.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No paths found to backup[/]");
            return ExitCodes.OperationFailure;
        }

        AnsiConsole.MarkupLine(
            $"[cyan]Central backup directory:[/] {Markup.Escape(backupManager.CentralBackupDirectory)}");
        AnsiConsole.WriteLine();

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Type[/]"))
            .AddColumn(new TableColumn("[bold]Path[/]"))
            .AddColumn(new TableColumn("[bold]Status[/]"));

        foreach ((string path, string description) in pathsToBackup)
        {
            bool exists = File.Exists(path) || Directory.Exists(path);
            string status = exists ? "[green]Found[/]" : "[red]Missing[/]";
            table.AddRow(description, Markup.Escape(path), status);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (opts.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run - no files were backed up[/]");
            return ExitCodes.Success;
        }

        (int successCount, int failCount) = await BackupConsoleUi.RunItemsWithStatusAsync(
            "Backing up files...",
            pathsToBackup,
            item => $"Backing up {item.Description}...",
            item => Path.GetFileName(item.Path),
            item => backupManager.BackupPath(item.Path),
            successLabel: "Backed up",
            failureLabel: "backup");

        AnsiConsole.WriteLine();
        BackupConsoleUi.DisplaySummary(successCount, failCount, "backed up", "item");

        return failCount == 0 ? ExitCodes.Success : ExitCodes.OperationFailure;
    }

    [Verb("backup", HelpText = "Backup CS2 game files and user settings")]
    public sealed class Options : IRegisterTaskAndOptions
    {
        [Usage(ApplicationAlias = "csvg")]
        public static IEnumerable<Example> Examples =>
        [
            new("Backup all files", new Options()),
            new("Preview what would be backed up", new Options { DryRun = true }),
            new("Backup only game files", new Options { GameFilesOnly = true }),
            new("Backup only user settings", new Options { UserSettingsOnly = true })
        ];

        [Option("dry-run", Default = false, HelpText = "Show what would be backed up without actually backing up")]
        public bool DryRun { get; set; }

        [Option("game-files", Default = false, HelpText = "Only backup game files (gameinfo.gi, etc.)")]
        public bool GameFilesOnly { get; set; }

        [Option("user-settings", Default = false, HelpText = "Only backup user settings (cfg directory)")]
        public bool UserSettingsOnly { get; set; }

        public void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder)
        {
            hostBuilder.Services.Configure<Options>(opts =>
            {
                opts.DryRun = DryRun;
                opts.GameFilesOnly = GameFilesOnly;
                opts.UserSettingsOnly = UserSettingsOnly;
            });

            hostBuilder.Services.AddHostedService<BackupTask>();
        }
    }
}
