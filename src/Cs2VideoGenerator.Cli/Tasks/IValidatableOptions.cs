namespace Cs2VideoGenerator.Cli.Tasks;

/// <summary>
///     Implemented by CLI option types that carry semantic constraints beyond what
///     CommandLineParser can express (e.g. numeric ranges, cross-option rules).
///     Violations are usage errors: the parse layer reports them and exits with
///     <see cref="ExitCodes.UsageError" /> before the host is built.
/// </summary>
public interface IValidatableOptions
{
    /// <returns>A list of human-readable issues; empty when the options are valid.</returns>
    IReadOnlyList<string> Validate();
}
