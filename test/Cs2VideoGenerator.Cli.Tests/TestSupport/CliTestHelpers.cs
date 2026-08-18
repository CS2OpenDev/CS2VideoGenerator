#region

using Cs2VideoGenerator.Cli;
using Cs2VideoGenerator.Cli.Utils;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     Shared test helper: parse-layer invocation with suppressed help/error output.
///     Tests that assert on the parser's output call <see cref="CliArgumentParser.Parse" />
///     directly with a real <see cref="StringWriter" />.
/// </summary>
internal static class CliTestHelpers
{
    public static CliParseResult Parse(params string[] args) =>
        CliArgumentParser.Parse(args, TextWriter.Null);
}
