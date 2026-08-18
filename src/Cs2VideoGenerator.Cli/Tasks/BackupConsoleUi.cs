#region

using Cs2VideoGenerator.Core.Services;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

/// <summary>
///     Shared console rendering for the backup family of tasks: backup, restore, cleanup.
/// </summary>
internal static class BackupConsoleUi
{
    public static List<(string Name, BackupIndexEntry Entry, bool Exists)> BuildBackupList(
        IReadOnlyDictionary<string, BackupIndexEntry> backupIndex, string centralBackupDir)
    {
        List<(string Name, BackupIndexEntry Entry, bool Exists)> backups = new();
        foreach ((string name, BackupIndexEntry entry) in backupIndex)
        {
            string backupPath = Path.Combine(centralBackupDir, name);
            bool exists = File.Exists(backupPath) || Directory.Exists(backupPath);
            backups.Add((name, entry, exists));
        }

        return backups;
    }

    /// <summary>Newest first, and it leaves a blank line behind it.</summary>
    public static void DisplayBackupTable(IReadOnlyList<(string Name, BackupIndexEntry Entry, bool Exists)> backups)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]#[/]").Centered())
            .AddColumn(new TableColumn("[bold]Backup Name[/]"))
            .AddColumn(new TableColumn("[bold]Original Path[/]"))
            .AddColumn(new TableColumn("[bold]Backed Up[/]"))
            .AddColumn(new TableColumn("[bold]Status[/]"));

        int index = 1;
        foreach ((string name, BackupIndexEntry entry, bool exists) in backups.OrderByDescending(b =>
                     b.Entry.BackedUpAt))
        {
            string status = exists ? "[green]Available[/]" : "[red]Missing[/]";
            string displayPath = entry.OriginalPath.Length > 50
                ? "..." + entry.OriginalPath[^47..]
                : entry.OriginalPath;

            table.AddRow(
                index.ToString(),
                Markup.Escape(name),
                Markup.Escape(displayPath),
                entry.BackedUpAt.LocalDateTime.ToString("g"),
                status
            );
            index++;
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Interactive multi-selection over the candidate backups, newest first. The display strings
    ///     are parsed back to index entries. Empty when the user selected nothing.
    /// </summary>
    public static List<(string Name, BackupIndexEntry Entry)> PromptBackupSelection(
        IReadOnlyDictionary<string, BackupIndexEntry> backupIndex,
        IEnumerable<(string Name, BackupIndexEntry Entry, bool Exists)> candidates,
        string title)
    {
        List<string> choices = candidates
            .OrderByDescending(b => b.Entry.BackedUpAt)
            .Select(b => $"{b.Name} ({Path.GetFileName(b.Entry.OriginalPath)})")
            .ToList();

        List<string> selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title(title)
                .NotRequired()
                .PageSize(10)
                .AddChoices(choices));

        List<(string Name, BackupIndexEntry Entry)> result = new();
        foreach (string selection in selected)
        {
            string backupName = selection.Split(" (")[0];
            if (backupIndex.TryGetValue(backupName, out BackupIndexEntry? entry))
            {
                result.Add((backupName, entry));
            }
        }

        return result;
    }

    /// <summary>
    ///     Runs a per-item backup/restore operation under an <see cref="AnsiConsole.Status" />
    ///     spinner, printing a ✓/✗ line per item, and returns the success/failure counts.
    /// </summary>
    /// <param name="overallStatus">Initial status text, e.g. "Restoring files...".</param>
    /// <param name="statusText">Per-item status text, e.g. "Restoring video.txt...".</param>
    /// <param name="itemName">Per-item display name for the ✓/✗ lines.</param>
    /// <param name="action">The operation to perform for each item.</param>
    /// <param name="successLabel">Past-tense label for success lines, e.g. "Restored".</param>
    /// <param name="failureLabel">Infinitive label for failure lines, e.g. "restore".</param>
    public static async Task<(int SuccessCount, int FailCount)> RunItemsWithStatusAsync<T>(
        string overallStatus,
        IReadOnlyList<T> items,
        Func<T, string> statusText,
        Func<T, string> itemName,
        Action<T> action,
        string successLabel,
        string failureLabel)
    {
        int successCount = 0;
        int failCount = 0;

        await AnsiConsole.Status()
            .StartAsync(overallStatus, async ctx =>
            {
                foreach (T item in items)
                {
                    ctx.Status(statusText(item));

                    try
                    {
                        action(item);
                        AnsiConsole.MarkupLine(
                            $"[green]✓[/] {successLabel}: {Markup.Escape(itemName(item))}");
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]✗[/] Failed to {failureLabel} {Markup.Escape(itemName(item))}: {Markup.Escape(ex.Message)}");
                        failCount++;
                    }
                }

                await Task.CompletedTask;
            });

        return (successCount, failCount);
    }

    /// <summary>
    ///     Green "Successfully &lt;verb&gt; N &lt;noun&gt;(s)", or yellow
    ///     "&lt;Verb&gt; N &lt;noun&gt;(s), M failed". Leaves a blank line behind it.
    /// </summary>
    /// <param name="pastTenseVerb">Lower-case past-tense verb, e.g. "restored", "backed up".</param>
    /// <param name="noun">Singular noun, e.g. "item", "backup".</param>
    public static void DisplaySummary(int successCount, int failCount, string pastTenseVerb, string noun)
    {
        if (failCount == 0)
        {
            AnsiConsole.Write(new Panel($"[green]Successfully {pastTenseVerb} {successCount} {noun}(s)[/]")
            {
                Border = BoxBorder.Rounded, BorderStyle = Style.Parse("green")
            });
        }
        else
        {
            string capitalizedVerb = char.ToUpperInvariant(pastTenseVerb[0]) + pastTenseVerb[1..];
            AnsiConsole.Write(new Panel($"[yellow]{capitalizedVerb} {successCount} {noun}(s), {failCount} failed[/]")
            {
                Border = BoxBorder.Rounded, BorderStyle = Style.Parse("yellow")
            });
        }

        AnsiConsole.WriteLine();
    }
}
