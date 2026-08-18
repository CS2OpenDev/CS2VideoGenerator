#region

using CommandLine;
using CommandLine.Text;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Options;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

public sealed class RestoreTask(
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder,
    IOptions<RestoreTask.Options> taskOptions,
    IOptions<Cs2VideoGeneratorOptions> csvgOptions,
    Cs2DirectoryUtil directoryUtil,
    IBackupManager backupManager) : CliTaskBase(applicationLifetime, exitCodeHolder)
{
    protected override async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Options opts = taskOptions.Value;

        WriteTaskHeader("CS2 Restore");

        if (csvgOptions.Value.IsMockModeEffective)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Restore is not available in mock mode — there are no real game files to restore.[/]");
            return ExitCodes.Success;
        }

        return opts.UseAdjacentBackups
            ? await RestoreFromAdjacentBackupsAsync(opts, cancellationToken)
            : await RestoreFromCentralBackupsAsync(opts, cancellationToken);
    }

    private async Task<int> RestoreFromCentralBackupsAsync(Options opts, CancellationToken cancellationToken)
    {
        string centralBackupDir = backupManager.CentralBackupDirectory;

        AnsiConsole.MarkupLine($"[cyan]Central backup directory:[/] {Markup.Escape(centralBackupDir)}");
        AnsiConsole.WriteLine();

        if (!Directory.Exists(centralBackupDir))
        {
            AnsiConsole.MarkupLine("[yellow]No central backup directory found[/]");
            return ExitCodes.Success;
        }

        IReadOnlyDictionary<string, BackupIndexEntry> backupIndex = backupManager.GetBackupIndex();

        if (backupIndex.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No backups found in index[/]");
            return ExitCodes.Success;
        }

        List<(string Name, BackupIndexEntry Entry, bool Exists)> availableBackups =
            BackupConsoleUi.BuildBackupList(backupIndex, centralBackupDir);
        BackupConsoleUi.DisplayBackupTable(availableBackups);

        if (opts.ListOnly)
        {
            return ExitCodes.Success;
        }

        List<(string Name, BackupIndexEntry Entry)> backupsToRestore = new();

        if (!string.IsNullOrWhiteSpace(opts.BackupName))
        {
            if (backupIndex.TryGetValue(opts.BackupName, out BackupIndexEntry? entry))
            {
                backupsToRestore.Add((opts.BackupName, entry));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Backup '{Markup.Escape(opts.BackupName)}' not found in index[/]");
                return ExitCodes.UsageError;
            }
        }
        else if (opts.RestoreAll)
        {
            backupsToRestore.AddRange(availableBackups.Where(b => b.Exists).Select(b => (b.Name, b.Entry)));
        }
        else
        {
            List<(string Name, BackupIndexEntry Entry, bool Exists)> candidates =
                availableBackups.Where(b => b.Exists).ToList();

            if (candidates.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No available backups to restore[/]");
                return ExitCodes.Success;
            }

            List<(string Name, BackupIndexEntry Entry)> selected =
                BackupConsoleUi.PromptBackupSelection(backupIndex, candidates, "Select backups to restore:");

            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No backups selected[/]");
                return ExitCodes.Success;
            }

            backupsToRestore.AddRange(selected);
        }

        if (backupsToRestore.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No backups to restore[/]");
            return ExitCodes.Success;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Will restore {backupsToRestore.Count} backup(s):[/]");
        foreach ((string name, BackupIndexEntry entry) in backupsToRestore)
        {
            AnsiConsole.MarkupLine($"  • {Markup.Escape(name)} → {Markup.Escape(entry.OriginalPath)}");
        }

        AnsiConsole.WriteLine();

        if (opts.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run - no files were restored[/]");
            return ExitCodes.Success;
        }

        if (!AnsiConsole.Confirm("Proceed with restore?", false))
        {
            AnsiConsole.MarkupLine("[dim]Restore cancelled[/]");
            return ExitCodes.Success;
        }

        (int successCount, int failCount) = await BackupConsoleUi.RunItemsWithStatusAsync(
            "Restoring files...",
            backupsToRestore,
            item => $"Restoring {Path.GetFileName(item.Entry.OriginalPath)}...",
            item => Path.GetFileName(item.Entry.OriginalPath),
            item => backupManager.RestorePath(item.Entry.OriginalPath),
            successLabel: "Restored",
            failureLabel: "restore");

        AnsiConsole.WriteLine();
        BackupConsoleUi.DisplaySummary(successCount, failCount, "restored", "item");

        return failCount == 0 ? ExitCodes.Success : ExitCodes.OperationFailure;
    }

    private async Task<int> RestoreFromAdjacentBackupsAsync(Options opts, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[cyan]Scanning for adjacent backups (.backup files)...[/]");
        AnsiConsole.WriteLine();

        List<(string BackupPath, string OriginalPath, string Description)> adjacentBackups = new();

        foreach (Cs2GameFileChanges item in directoryUtil.PathsThatGetModified.Where(f => f.ShouldBeBackedUp))
        {
            string backupPath = item.Path + ".backup";
            if (File.Exists(backupPath) || Directory.Exists(backupPath))
            {
                adjacentBackups.Add((backupPath, item.Path, "Game file"));
            }
        }

        if (directoryUtil.CanBackupUserSettings)
        {
            string? userConfigDir = directoryUtil.UserConfigDirectory;
            if (!string.IsNullOrWhiteSpace(userConfigDir))
            {
                string backupPath = userConfigDir + ".backup";
                if (Directory.Exists(backupPath))
                {
                    adjacentBackups.Add((backupPath, userConfigDir, "User settings"));
                }
            }
        }

        if (adjacentBackups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No adjacent backups found[/]");
            return ExitCodes.Success;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Type[/]"))
            .AddColumn(new TableColumn("[bold]Backup Path[/]"))
            .AddColumn(new TableColumn("[bold]Original Path[/]"));

        foreach ((string backupPath, string originalPath, string description) in adjacentBackups)
        {
            table.AddRow(
                description,
                Markup.Escape(backupPath),
                Markup.Escape(originalPath)
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (opts.ListOnly)
        {
            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[cyan]Will restore {adjacentBackups.Count} backup(s)[/]");
        AnsiConsole.WriteLine();

        if (opts.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run - no files were restored[/]");
            return ExitCodes.Success;
        }

        if (!AnsiConsole.Confirm("Proceed with restore?", false))
        {
            AnsiConsole.MarkupLine("[dim]Restore cancelled[/]");
            return ExitCodes.Success;
        }

        (int successCount, int failCount) = await BackupConsoleUi.RunItemsWithStatusAsync(
            "Restoring files...",
            adjacentBackups,
            item => $"Restoring {item.Description}...",
            item => Path.GetFileName(item.OriginalPath),
            item => backupManager.RestorePath(item.OriginalPath),
            successLabel: "Restored",
            failureLabel: "restore");

        AnsiConsole.WriteLine();
        BackupConsoleUi.DisplaySummary(successCount, failCount, "restored", "item");

        return failCount == 0 ? ExitCodes.Success : ExitCodes.OperationFailure;
    }

    [Verb("restore", HelpText = "Restore CS2 game files and user settings from backups")]
    public sealed class Options : IRegisterTaskAndOptions
    {
        [Usage(ApplicationAlias = "csvg")]
        public static IEnumerable<Example> Examples =>
        [
            new("List available backups", new Options { ListOnly = true }),
            new("Preview what would be restored", new Options { DryRun = true, RestoreAll = true }),
            new("Restore all backups", new Options { RestoreAll = true }),
            new("Restore a specific backup", new Options { BackupName = "gameinfo.gi_abc123" }),
            new("Restore from adjacent .backup files", new Options { UseAdjacentBackups = true })
        ];

        [Option("dry-run", Default = false, HelpText = "Show what would be restored without actually restoring")]
        public bool DryRun { get; set; }

        [Option("list", Default = false, HelpText = "List available backups without restoring")]
        public bool ListOnly { get; set; }

        [Option("all", Default = false, HelpText = "Restore all available backups")]
        public bool RestoreAll { get; set; }

        [Option("adjacent", Default = false,
            HelpText = "Restore from adjacent backups (.backup files) instead of central backups")]
        public bool UseAdjacentBackups { get; set; }

        [Value(0, MetaName = "backup-name",
            HelpText = "Specific backup name to restore (from central backup directory)")]
        public string? BackupName { get; set; }

        public void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder)
        {
            hostBuilder.Services.Configure<Options>(opts =>
            {
                opts.DryRun = DryRun;
                opts.ListOnly = ListOnly;
                opts.RestoreAll = RestoreAll;
                opts.UseAdjacentBackups = UseAdjacentBackups;
                opts.BackupName = BackupName;
            });

            hostBuilder.Services.AddHostedService<RestoreTask>();
        }
    }

}
