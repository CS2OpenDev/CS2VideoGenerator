#region

using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Tasks;

/// <summary>
///     Owns the application lifetime for a CLI verb: when the task finishes, however it finishes,
///     the host shuts down. The exit code from <see cref="RunAsync" /> goes through
///     <see cref="ExitCodeHolder" /> for <see cref="Program.Main" /> to return.
/// </summary>
public abstract class CliTaskBase(
    IHostApplicationLifetime applicationLifetime,
    ExitCodeHolder exitCodeHolder) : BackgroundService
{
    protected sealed override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            exitCodeHolder.ExitCode = await RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutdown requested before the task finished, so the operation did not complete
            exitCodeHolder.ExitCode = ExitCodes.OperationFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            exitCodeHolder.ExitCode = ExitCodes.OperationFailure;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }

    protected static void WriteTaskHeader(string title)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]{title}[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }

    /// <summary>The host shuts down as soon as this returns.</summary>
    /// <returns>
    ///     The process exit code: <see cref="ExitCodes.Success" />,
    ///     <see cref="ExitCodes.OperationFailure" />, or <see cref="ExitCodes.UsageError" />.
    /// </returns>
    protected abstract Task<int> RunAsync(CancellationToken cancellationToken);
}
