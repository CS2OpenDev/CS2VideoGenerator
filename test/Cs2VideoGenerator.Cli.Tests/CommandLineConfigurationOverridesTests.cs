#region

using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     Tests for the parsed-options → IConfiguration injection that replaced the
///     AddCommandLine switch mapping.
/// </summary>
public class CommandLineConfigurationOverridesTests
{
    private const string MockModeKey = "Cs2VideoGenerator:MockMode";
    private const string MockServerPathKey = "Cs2VideoGenerator:ExternalMockServerPath";
    private const string PluginPathKey = "Cs2VideoGenerator:ExternalPluginPath";

    private static IReadOnlyDictionary<string, string?> BuildFromArgs(params string[] args)
    {
        CliParseResult result = CliTestHelpers.Parse(args);
        Assert.NotNull(result.Task);
        return CommandLineConfigurationOverrides.Build(result.Task, args);
    }

    [Theory]
    [InlineData(new object[] { new[] { "doctor", "--mock" } })]
    [InlineData(new object[] { new[] { "doctor", "--mock=true" } })]
    [InlineData(new object[] { new[] { "doctor", "--mock", "--verbose" } })]
    [InlineData(new object[] { new[] { "generate", "--file", "c.json", "--mock" } })]
    [InlineData(new object[] { new[] { "watch", "--demo", "x.dem", "--mock" } })]
    public void Build_MockEnabled_InjectsMockModeTrue(string[] args)
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs(args);

        Assert.Equal("true", values[MockModeKey]);
    }

    [Fact]
    public void Build_ExplicitMockFalse_InjectsMockModeFalse()
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("doctor", "--mock=false");

        Assert.Equal("false", values[MockModeKey]);
    }

    [Fact]
    public void Build_MockOmitted_DoesNotInjectMockMode()
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("doctor");

        Assert.False(values.ContainsKey(MockModeKey));
    }

    [Fact]
    public void Build_MockServerPath_InjectsConfiguredPath()
    {
        IReadOnlyDictionary<string, string?> values =
            BuildFromArgs("doctor", "--mock-server-path", "/tmp/mock_server");

        Assert.Equal("/tmp/mock_server", values[MockServerPathKey]);
    }

    [Fact]
    public void Build_PluginPath_InjectsConfiguredPath()
    {
        IReadOnlyDictionary<string, string?> values =
            BuildFromArgs("doctor", "--plugin-path", "/tmp/server.dll");

        Assert.Equal("/tmp/server.dll", values[PluginPathKey]);
    }

    [Theory]
    [InlineData(new object[] { new[] { "generate", "--file", "c.json", "--force" } })]
    [InlineData(new object[] { new[] { "watch", "--demo", "x.dem", "--force" } })]
    public void Build_Force_InjectsForceIncompatiblePluginTrue(string[] args)
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs(args);

        Assert.Equal("true", values["Cs2VideoGenerator:ForceIncompatiblePlugin"]);
    }

    [Fact]
    public void Build_ForceOmitted_DoesNotInjectForceIncompatiblePlugin()
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("generate", "--file", "c.json");

        Assert.False(values.ContainsKey("Cs2VideoGenerator:ForceIncompatiblePlugin"));
    }

    [Theory]
    [InlineData(new object[] { new[] { "generate", "--file", "c.json", "--capture-provider", "OBS" } })]
    [InlineData(new object[] { new[] { "watch", "--demo", "x.dem", "--capture-provider", "OBS" } })]
    public void Build_CaptureProvider_InjectsSelectedProvider(string[] args)
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs(args);

        Assert.Equal("OBS", values[CommonOptions.CaptureProviderConfigurationPath]);
    }

    /// <summary>
    ///     An omitted --capture-provider must inject nothing, or it would beat a configured
    ///     Cs2VideoGenerator:VideoCaptureProvider on every invocation.
    /// </summary>
    [Fact]
    public void Build_CaptureProviderOmitted_DoesNotInjectProvider()
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("watch", "--demo", "x.dem");

        Assert.False(values.ContainsKey(CommonOptions.CaptureProviderConfigurationPath));
    }

    [Fact]
    public void Build_NonOverrideOptions_AreNotInjected()
    {
        // --verbose has no ConfigurationOverride attribute; it must not leak into configuration.
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("doctor", "--verbose");

        Assert.Empty(values);
    }

    [Fact]
    public void ConfigurationSource_ValuesAreReadableThroughIConfiguration()
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("doctor", "--mock");

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .Add(new CommandLineOverridesConfigurationSource(values))
            .Build();

        Assert.Equal("true", configuration[MockModeKey]);
    }

    [Fact]
    public void ConfigurationSource_MockFalseOverridesEarlierTrue()
    {
        IReadOnlyDictionary<string, string?> values = BuildFromArgs("doctor", "--mock=false");

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MockModeKey] = "true" })
            .Add(new CommandLineOverridesConfigurationSource(values))
            .Build();

        Assert.Equal("false", configuration[MockModeKey]);
    }

    [Fact]
    public void ConfigurationProvider_TypeNameIdentifiesCommandLine()
    {
        // Configuration-source reporting (doctor --verbose) keys off "CommandLine" in the provider type name.
        IConfigurationProvider provider =
            new CommandLineOverridesConfigurationSource(new Dictionary<string, string?>())
                .Build(new ConfigurationBuilder());

        Assert.Contains("CommandLine", provider.GetType().Name, StringComparison.Ordinal);
    }
}
