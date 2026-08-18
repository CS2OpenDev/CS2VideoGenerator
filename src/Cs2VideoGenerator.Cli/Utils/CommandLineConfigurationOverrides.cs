#region

using System.Globalization;
using System.Reflection;
using CommandLine;
using Cs2VideoGenerator.Cli.Options;

#endregion

namespace Cs2VideoGenerator.Cli.Utils;

/// <summary>
///     Injects option values parsed by CommandLineParser into <see cref="IConfiguration" />.
///     CommandLineParser is the single source of truth for argument parsing; this replaces the old
///     <c>AddCommandLine</c> switch-mapping path, which could not handle value-less flags (a bare
///     <c>--mock</c> was silently dropped and <c>--mock --verbose=true</c> crashed the config binder).
/// </summary>
public static class CommandLineConfigurationOverrides
{
    /// <summary>
    ///     Only properties carrying both <see cref="OptionAttribute" /> and
    ///     <see cref="ConfigurationOverrideAttribute" /> contribute.
    /// </summary>
    /// <param name="args">
    ///     The raw argument vector, used to detect explicitly provided flags so that an explicit
    ///     <c>--mock=false</c> still overrides configuration while an omitted flag does not.
    /// </param>
    public static IReadOnlyDictionary<string, string?> Build(object parsedOptions, IReadOnlyList<string> args)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        foreach (PropertyInfo prop in parsedOptions.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            OptionAttribute? optionAttr = prop.GetCustomAttribute<OptionAttribute>();
            ConfigurationOverrideAttribute? overrideAttr = prop.GetCustomAttribute<ConfigurationOverrideAttribute>();
            if (optionAttr is null || overrideAttr is null)
            {
                continue;
            }

            object? value = prop.GetValue(parsedOptions);

            if (value is bool boolValue)
            {
                // Include when the flag is set (bare `--mock` or `--mock=true`) or when it was passed
                // explicitly (`--mock=false` must override a config-file `true`). An omitted flag adds
                // nothing so configuration/appsettings defaults still apply.
                if (boolValue || OptionArgumentInspector.WasExplicitlyProvided(optionAttr, prop, args))
                {
                    values[overrideAttr.ConfigurationPath] = boolValue ? "true" : "false";
                }
            }
            else if (value is string stringValue)
            {
                if (!string.IsNullOrWhiteSpace(stringValue))
                {
                    values[overrideAttr.ConfigurationPath] = stringValue;
                }
            }
            else if (value is not null && OptionArgumentInspector.WasExplicitlyProvided(optionAttr, prop, args))
            {
                values[overrideAttr.ConfigurationPath] = Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        return values;
    }
}

/// <summary>
///     Configuration source carrying command-line derived overrides. Named so that configuration-source
///     reporting (e.g. <c>doctor --verbose</c>) identifies these values as coming from the command line.
/// </summary>
public sealed class CommandLineOverridesConfigurationSource(IReadOnlyDictionary<string, string?> values)
    : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new CommandLineOverridesConfigurationProvider(values);
}

public sealed class CommandLineOverridesConfigurationProvider : ConfigurationProvider
{
    public CommandLineOverridesConfigurationProvider(IReadOnlyDictionary<string, string?> values)
    {
        foreach ((string key, string? value) in values)
        {
            Data[key] = value;
        }
    }
}
