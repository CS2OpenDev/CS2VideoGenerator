namespace Cs2VideoGenerator.Cli.Options;

/// <summary>
///     Marks a CLI option property as overriding a configuration value at the specified configuration path.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigurationOverrideAttribute : Attribute
{
    /// <summary>The key this option writes to, e.g. "Cs2VideoGenerator:ExternalMockServerPath".</summary>
    public required string ConfigurationPath { get; init; }
}
