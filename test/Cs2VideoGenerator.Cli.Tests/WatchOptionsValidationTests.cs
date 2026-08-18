#region

using Cs2VideoGenerator.Cli;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Cli.Utils;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     Semantic validation of `watch` numeric options at the parse layer:
///     invalid values are usage errors (exit code 2) reported before the host is built.
/// </summary>
public class WatchOptionsValidationTests
{
    [Theory]
    // Negative start tick
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "-1")]
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "-5000")]
    // End tick below/at start tick (nonzero)
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "5000", "--end-tick", "4000")]
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "5000", "--end-tick", "5000")]
    [InlineData("watch", "--demo", "match.dem", "--end-tick", "-100")]
    // Non-positive dimensions
    [InlineData("watch", "--demo", "match.dem", "--width", "0")]
    [InlineData("watch", "--demo", "match.dem", "--width", "-1920")]
    [InlineData("watch", "--demo", "match.dem", "--height", "0")]
    [InlineData("watch", "--demo", "match.dem", "--height", "-1080")]
    public void Parse_InvalidWatchValues_ReturnsUsageError(params string[] args)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        Assert.Null(result.Task);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public void Parse_InvalidWatchValues_ReportsEveryIssue()
    {
        StringWriter writer = new();

        CliParseResult result = CliArgumentParser.Parse(
            ["watch", "--demo", "match.dem", "--start-tick", "-1", "--width", "0", "--height", "0"],
            writer);

        Assert.Null(result.Task);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);

        string output = writer.ToString();
        Assert.Contains("--start-tick", output);
        Assert.Contains("--width", output);
        Assert.Contains("--height", output);
    }

    [Theory]
    // Defaults are valid
    [InlineData("watch", "--demo", "match.dem")]
    // End tick 0 = default range
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "5000", "--end-tick", "0")]
    // Explicit valid range
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "5000", "--end-tick", "15000")]
    // Start tick 0 is valid
    [InlineData("watch", "--demo", "match.dem", "--start-tick", "0")]
    public void Parse_ValidWatchValues_ReturnsTask(params string[] args)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        Assert.NotNull(result.Task);
        Assert.IsType<WatchTask.Options>(result.Task);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    [Theory]
    [InlineData(new[] { "watch", "--demo", "match.dem" }, false)]
    [InlineData(new[] { "watch", "--demo", "match.dem", "--no-capture" }, true)]
    [InlineData(new[] { "watch", "--demo", "match.dem", "--no-capture=true" }, true)]
    [InlineData(new[] { "watch", "--demo", "match.dem", "--no-capture=false" }, false)]
    public void Parse_NoCaptureFlag_AllFormsAreAccepted(string[] args, bool expectedNoCapture)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        WatchTask.Options options = Assert.IsType<WatchTask.Options>(result.Task);
        Assert.Equal(expectedNoCapture, options.NoCapture);
    }
}
