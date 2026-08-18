#region

using System.Reflection;
using CommandLine;
using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Tasks;
using WatchTask = Cs2VideoGenerator.Cli.Tasks.WatchTask;

#endregion

namespace Cs2VideoGenerator.Cli.Utils;

/// <param name="Task">The parsed verb options when parsing succeeded; otherwise <c>null</c>.</param>
/// <param name="ExitCode">
///     The exit code the process should use when <paramref name="Task" /> is <c>null</c>:
///     <see cref="ExitCodes.Success" /> for explicit help/version requests,
///     <see cref="ExitCodes.UsageError" /> for unknown verbs or bad arguments.
///     <see cref="ExitCodes.Success" /> when parsing succeeded.
/// </param>
/// <param name="ParserResult">The underlying CommandLineParser result (for help-text generation).</param>
public sealed record CliParseResult(
    IRegisterTaskAndOptions? Task,
    int ExitCode,
    ParserResult<object> ParserResult);

/// <summary>
///     Single source of truth for CLI argument parsing. Wraps CommandLineParser so the
///     argv → verb/exit-code mapping is unit-testable without spinning up a host.
/// </summary>
public static class CliArgumentParser
{
    /// <param name="args">Raw process arguments.</param>
    /// <param name="helpWriter">
    ///     Where help/usage text is written on parse failure or help/version requests.
    ///     Pass <c>null</c> to suppress output (tests).
    /// </param>
    public static CliParseResult Parse(string[] args, TextWriter? helpWriter = null)
    {
        using Parser parser = new(settings =>
        {
            settings.HelpWriter = helpWriter;
        });

        ParserResult<object> parserResult = parser
            .ParseArguments<
                BackupTask.Options, CleanupTask.Options,
                DoctorTask.Options, GenerateTask.Options,
                RestoreTask.Options, WatchTask.Options>(args);

        if (parserResult is Parsed<object> { Value: IRegisterTaskAndOptions task })
        {
            NormalizeExplicitBooleanValues(task, args);

            // numeric ranges, cross-option rules; violations are usage errors
            if (task is IValidatableOptions validatable)
            {
                IReadOnlyList<string> issues = validatable.Validate();
                if (issues.Count > 0)
                {
                    foreach (string issue in issues)
                    {
                        helpWriter?.WriteLine($"Error: {issue}");
                    }

                    return new CliParseResult(null, ExitCodes.UsageError, parserResult);
                }
            }

            return new CliParseResult(task, ExitCodes.Success, parserResult);
        }

        // Explicit --help/--version/help-verb requests are successful outcomes, not usage errors.
        bool helpOrVersionRequested = parserResult is NotParsed<object> notParsed &&
                                      notParsed.Errors.Any() &&
                                      notParsed.Errors.All(e =>
                                          e is HelpRequestedError or HelpVerbRequestedError or VersionRequestedError);

        return new CliParseResult(
            null,
            helpOrVersionRequested ? ExitCodes.Success : ExitCodes.UsageError,
            parserResult);
    }

    /// <summary>
    ///     CommandLineParser treats <c>bool</c> options as pure switches: <c>--mock=false</c> parses
    ///     as <c>true</c> because the presence of the switch wins and the inline value is ignored.
    ///     This honors an explicit inline boolean value (<c>--mock=false</c>, <c>--verbose=true</c>)
    ///     so all documented flag forms behave consistently.
    /// </summary>
    private static void NormalizeExplicitBooleanValues(object parsedOptions, string[] args)
    {
        foreach (PropertyInfo prop in parsedOptions.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(bool) || !prop.CanWrite)
            {
                continue;
            }

            OptionAttribute? optionAttr = prop.GetCustomAttribute<OptionAttribute>();
            if (optionAttr is null)
            {
                continue;
            }

            string? inlineValue = OptionArgumentInspector.GetExplicitInlineValue(optionAttr, prop, args);
            if (inlineValue is not null && bool.TryParse(inlineValue, out bool value))
            {
                prop.SetValue(parsedOptions, value);
            }
        }
    }
}
