#region

using Cs2VideoGenerator.Cli;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Cli.Utils;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     Table-driven tests for the argv → exit-code mapping of the parse layer.
/// </summary>
public class CliArgumentParserTests
{
    [Theory]
    // Usage errors → exit code 2
    [InlineData(new string[] { }, ExitCodes.UsageError)]
    [InlineData(new[] { "unknownverb" }, ExitCodes.UsageError)]
    [InlineData(new[] { "doctor", "--bogus-flag" }, ExitCodes.UsageError)]
    [InlineData(new[] { "watch" }, ExitCodes.UsageError)] // missing required --demo
    [InlineData(new[] { "watch", "--demo" }, ExitCodes.UsageError)] // missing value for required option
    // Help/version requests → exit code 0
    [InlineData(new[] { "--help" }, ExitCodes.Success)]
    [InlineData(new[] { "--version" }, ExitCodes.Success)]
    [InlineData(new[] { "help" }, ExitCodes.Success)]
    [InlineData(new[] { "doctor", "--help" }, ExitCodes.Success)]
    public void Parse_WithoutRunnableVerb_ReturnsExpectedExitCode(string[] args, int expectedExitCode)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        Assert.Null(result.Task);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData(new[] { "doctor" }, typeof(DoctorTask.Options))]
    [InlineData(new[] { "doctor", "--mock" }, typeof(DoctorTask.Options))]
    [InlineData(new[] { "backup", "--dry-run" }, typeof(BackupTask.Options))]
    [InlineData(new[] { "restore", "--list" }, typeof(RestoreTask.Options))]
    [InlineData(new[] { "cleanup", "--list" }, typeof(CleanupTask.Options))]
    [InlineData(new[] { "generate", "--file", "compilation.json" }, typeof(GenerateTask.Options))]
    [InlineData(new[] { "watch", "--demo", "match.dem" }, typeof(WatchTask.Options))]
    public void Parse_WithValidVerb_ReturnsTask(string[] args, Type expectedOptionsType)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        Assert.NotNull(result.Task);
        Assert.IsType(expectedOptionsType, result.Task);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    [Theory]
    [InlineData(new[] { "doctor", "--mock" }, true)]
    [InlineData(new[] { "doctor", "--mock=true" }, true)]
    [InlineData(new[] { "doctor", "--mock", "--verbose" }, true)]
    [InlineData(new[] { "doctor", "--mock=false" }, false)]
    [InlineData(new[] { "doctor" }, false)]
    public void Parse_MockFlag_AllFormsAreAccepted(string[] args, bool expectedMockMode)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        DoctorTask.Options options = Assert.IsType<DoctorTask.Options>(result.Task);
        Assert.Equal(expectedMockMode, options.MockMode);
    }

    [Fact]
    public void Parse_MockWithVerbose_SetsBothFlags()
    {
        CliParseResult result = CliTestHelpers.Parse("doctor", "--mock", "--verbose");

        DoctorTask.Options options = Assert.IsType<DoctorTask.Options>(result.Task);
        Assert.True(options.MockMode);
        Assert.True(options.Verbose);
    }

    [Fact]
    public void Parse_MockServerPath_IsParsed()
    {
        CliParseResult result = CliTestHelpers.Parse("generate", "--file", "c.json", "--mock-server-path", "/tmp/mock_server");

        GenerateTask.Options options = Assert.IsType<GenerateTask.Options>(result.Task);
        Assert.Equal("/tmp/mock_server", options.ExternalMockServerPath);
    }
}
