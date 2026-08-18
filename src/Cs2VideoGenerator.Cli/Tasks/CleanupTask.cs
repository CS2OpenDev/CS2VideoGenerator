#region

using System.Text.RegularExpressions;
using CommandLine;
using CommandLine.Text;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Options;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

public sealed partial class CleanupTask(
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder,
    IOptions<CleanupTask.Options> taskOptions,
    IBackupManager backupManager) : CliTaskBase(applicationLifetime, exitCodeHolder)
{
    protected override async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Options opts = taskOptions.Value;

        WriteTaskHeader("CS2 Backup Cleanup");

        AnsiConsole.MarkupLine($"[cyan]Central backup directory:[/] {Markup.Escape(backupManager.CentralBackupDirectory)}");
        AnsiConsole.WriteLine();

        IReadOnlyDictionary<string, BackupIndexEntry> backupIndex = backupManager.GetBackupIndex();

        if (backupIndex.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No backups found in index[/]");
            return ExitCodes.Success;
        }

        List<(string Name, BackupIndexEntry Entry, bool Exists)> allBackups =
            BackupConsoleUi.BuildBackupList(backupIndex, backupManager.CentralBackupDirectory);
        BackupConsoleUi.DisplayBackupTable(allBackups);

        if (opts.ListOnly)
        {
            return ExitCodes.Success;
        }

        List<(string Name, BackupIndexEntry Entry)> backupsToDelete;

        if (opts.DeleteAll)
        {
            backupsToDelete = allBackups.Select(b => (b.Name, b.Entry)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(opts.MaxAge))
        {
            TimeSpan? maxAge = ParseDuration(opts.MaxAge);
            if (maxAge == null)
            {
                AnsiConsole.MarkupLine($"[red]Invalid duration format: {Markup.Escape(opts.MaxAge)}[/]");
                AnsiConsole.MarkupLine("[dim]Supported formats: 24h, 7d, 2w (hours, days, weeks)[/]");
                return ExitCodes.UsageError;
            }

            DateTimeOffset cutoff = DateTimeOffset.UtcNow - maxAge.Value;
            backupsToDelete = allBackups
                .Where(b => b.Entry.BackedUpAt < cutoff)
                .Select(b => (b.Name, b.Entry))
                .ToList();

            if (backupsToDelete.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No backups older than {Markup.Escape(opts.MaxAge)}[/]");
                return ExitCodes.Success;
            }
        }
        else
        {
            backupsToDelete =
                BackupConsoleUi.PromptBackupSelection(backupIndex, allBackups, "Select backups to delete:");

            if (backupsToDelete.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No backups selected[/]");
                return ExitCodes.Success;
            }
        }

        if (backupsToDelete.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No backups to delete[/]");
            return ExitCodes.Success;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Will delete {backupsToDelete.Count} backup(s):[/]");
        foreach ((string name, BackupIndexEntry entry) in backupsToDelete)
        {
            AnsiConsole.MarkupLine($"  [red]\u00d7[/] {Markup.Escape(name)} \u2190 {Markup.Escape(entry.OriginalPath)}");
        }

        AnsiConsole.WriteLine();

        if (opts.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry run - no backups were deleted[/]");
            return ExitCodes.Success;
        }

        // defaults to No
        if (!AnsiConsole.Confirm("Proceed with deletion?", false))
        {
            AnsiConsole.MarkupLine("[dim]Cleanup cancelled[/]");
            return ExitCodes.Success;
        }

        int deletedCount = backupManager.DeleteBackups(backupsToDelete.Select(b => b.Name));

        AnsiConsole.WriteLine();
        BackupConsoleUi.DisplaySummary(deletedCount, backupsToDelete.Count - deletedCount, "deleted", "backup");

        await Task.CompletedTask;

        return deletedCount == backupsToDelete.Count ? ExitCodes.Success : ExitCodes.OperationFailure;
    }

    /// <summary>Accepts 24h, 7d, 2w. Null for anything else.</summary>
    private static TimeSpan? ParseDuration(string input)
    {
        Match match = DurationRegex().Match(input.Trim());
        if (!match.Success)
        {
            return null;
        }

        double value = double.Parse(match.Groups[1].Value);
        string unit = match.Groups[2].Value.ToLowerInvariant();

        return unit switch
        {
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            "w" => TimeSpan.FromDays(value * 7),
            _ => null
        };
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(h|d|w)$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [Verb("cleanup", HelpText = "Clean up (delete) backups from the central backup directory")]
    public sealed class Options : IRegisterTaskAndOptions
    {
        [Usage(ApplicationAlias = "csvg")]
        public static IEnumerable<Example> Examples =>
        [
            new("List all backups", new Options { ListOnly = true }),
            new("Preview what would be deleted", new Options { DryRun = true, DeleteAll = true }),
            new("Delete all backups", new Options { DeleteAll = true }),
            new("Delete backups older than 7 days", new Options { MaxAge = "7d" }),
            new("Interactively select backups to delete", new Options())
        ];

        [Option("dry-run", Default = false, HelpText = "Show what would be deleted without actually deleting")]
        public bool DryRun { get; set; }

        [Option("list", Default = false, HelpText = "List available backups without deleting")]
        public bool ListOnly { get; set; }

        [Option("all", Default = false, HelpText = "Delete all backups")]
        public bool DeleteAll { get; set; }

        [Option("max-age", HelpText = "Delete backups older than the specified duration (e.g. 24h, 7d, 2w)")]
        public string? MaxAge { get; set; }

        public void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder)
        {
            hostBuilder.Services.Configure<Options>(opts =>
            {
                opts.DryRun = DryRun;
                opts.ListOnly = ListOnly;
                opts.DeleteAll = DeleteAll;
                opts.MaxAge = MaxAge;
            });

            hostBuilder.Services.AddHostedService<CleanupTask>();
        }
    }
}
