#region

using Cs2VideoGenerator.Cli;
using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Cli.Utils;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     Tests for per-verb gRPC hosting: only session-launching verbs host the gRPC
///     endpoint, and the port is configurable via --grpc-port with strict validation.
/// </summary>
public class GrpcHostingTests
{
    [Theory]
    // Session-launching verbs host gRPC
    [InlineData(new[] { "generate", "--file", "compilation.json" }, true)]
    [InlineData(new[] { "watch", "--demo", "match.dem" }, true)]
    // All other verbs must not bind any port
    [InlineData(new[] { "doctor" }, false)]
    [InlineData(new[] { "doctor", "--mock" }, false)]
    [InlineData(new[] { "backup", "--dry-run" }, false)]
    [InlineData(new[] { "restore", "--list" }, false)]
    [InlineData(new[] { "cleanup", "--list" }, false)]
    public void RequiresGrpcHost_MapsVerbsCorrectly(string[] args, bool expectedNeedsGrpc)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        Assert.NotNull(result.Task);
        Assert.Equal(expectedNeedsGrpc, Program.RequiresGrpcHost(result.Task));
    }

    [Fact]
    public void Parse_GrpcPortOmitted_DefaultsTo50051()
    {
        CliParseResult result = CliTestHelpers.Parse("generate", "--file", "c.json");

        CommonOptions options = Assert.IsAssignableFrom<CommonOptions>(result.Task);
        Assert.Equal(CommonOptions.DefaultGrpcPort, options.GrpcPort);
        Assert.Equal(50051, options.GrpcPort);
    }

    [Theory]
    [InlineData(new[] { "generate", "--file", "c.json", "--grpc-port", "50052" }, 50052)]
    [InlineData(new[] { "generate", "--file", "c.json", "--grpc-port=50123" }, 50123)]
    [InlineData(new[] { "watch", "--demo", "m.dem", "--grpc-port", "6000" }, 6000)]
    [InlineData(new[] { "generate", "--file", "c.json", "--grpc-port", "65535" }, 65535)]
    [InlineData(new[] { "generate", "--file", "c.json", "--grpc-port", "1" }, 1)]
    public void Parse_GrpcPortProvided_IsParsed(string[] args, int expectedPort)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        CommonOptions options = Assert.IsAssignableFrom<CommonOptions>(result.Task);
        Assert.Equal(expectedPort, options.GrpcPort);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    [Theory]
    // Dynamic port is not supported (plugin-side port is fixed at plugin build time)
    [InlineData("generate", "--file", "c.json", "--grpc-port", "0")]
    [InlineData("watch", "--demo", "m.dem", "--grpc-port=0")]
    // Out of range
    [InlineData("generate", "--file", "c.json", "--grpc-port", "65536")]
    [InlineData("generate", "--file", "c.json", "--grpc-port=-1")]
    // Not a number
    [InlineData("generate", "--file", "c.json", "--grpc-port", "abc")]
    public void Parse_InvalidGrpcPort_IsUsageError(params string[] args)
    {
        CliParseResult result = CliTestHelpers.Parse(args);

        Assert.Null(result.Task);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public void Parse_GrpcPortZero_ExplainsDynamicPortsAreUnsupported()
    {
        using StringWriter writer = new();

        CliParseResult result = CliArgumentParser.Parse(
            ["generate", "--file", "c.json", "--grpc-port", "0"], writer);

        Assert.Null(result.Task);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("--grpc-port 0", writer.ToString());
        Assert.Contains("not supported", writer.ToString());
    }

    [Fact]
    public void Parse_GrpcPortOnNonGrpcVerb_IsUsageError()
    {
        // doctor does not host gRPC, so it must not accept --grpc-port
        CliParseResult result = CliTestHelpers.Parse("doctor", "--grpc-port", "50052");

        Assert.Null(result.Task);
        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
    }

    [Fact]
    public void Build_GrpcPortProvided_IsIncludedInConfigurationOverrides()
    {
        string[] args = ["generate", "--file", "c.json", "--grpc-port", "50123"];
        CliParseResult result = CliTestHelpers.Parse(args);
        Assert.NotNull(result.Task);

        IReadOnlyDictionary<string, string?> overrides =
            CommandLineConfigurationOverrides.Build(result.Task, args);

        Assert.Equal("50123", overrides[CommonOptions.GrpcPortConfigurationPath]);
    }

    [Fact]
    public void Build_GrpcPortOmitted_IsNotIncludedInConfigurationOverrides()
    {
        // When omitted, configuration (appsettings/env) must keep supplying the value.
        string[] args = ["generate", "--file", "c.json"];
        CliParseResult result = CliTestHelpers.Parse(args);
        Assert.NotNull(result.Task);

        IReadOnlyDictionary<string, string?> overrides =
            CommandLineConfigurationOverrides.Build(result.Task, args);

        Assert.False(overrides.ContainsKey(CommonOptions.GrpcPortConfigurationPath));
    }
}
