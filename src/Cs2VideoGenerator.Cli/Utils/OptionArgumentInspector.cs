#region

using System.Reflection;
using CommandLine;

#endregion

namespace Cs2VideoGenerator.Cli.Utils;

/// <summary>
///     Inspects the raw argument vector for tokens belonging to a specific [Option] property.
///     Shared by <see cref="CliArgumentParser" /> (boolean-value normalization) and
///     <see cref="CommandLineConfigurationOverrides" /> (explicit-flag detection).
/// </summary>
internal static class OptionArgumentInspector
{
    public static string GetLongName(OptionAttribute optionAttr, PropertyInfo prop) =>
        string.IsNullOrWhiteSpace(optionAttr.LongName) ? prop.Name : optionAttr.LongName;

    /// <summary>
    ///     Returns <c>true</c> when the option appears explicitly in the argument vector
    ///     (as <c>--name</c>, <c>--name=value</c>, <c>-s</c>, or <c>-s=value</c>).
    /// </summary>
    public static bool WasExplicitlyProvided(OptionAttribute optionAttr, PropertyInfo prop,
        IReadOnlyList<string> args)
    {
        string longToken = $"--{GetLongName(optionAttr, prop)}";
        string? shortToken = string.IsNullOrWhiteSpace(optionAttr.ShortName) ? null : $"-{optionAttr.ShortName}";

        foreach (string arg in args)
        {
            if (arg.Equals(longToken, StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith(longToken + "=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (shortToken is not null &&
                (arg.Equals(shortToken, StringComparison.Ordinal) ||
                 arg.StartsWith(shortToken + "=", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Returns the inline value of the option when it was passed as <c>--name=value</c> or
    ///     <c>-s=value</c>; otherwise <c>null</c>. The last occurrence wins.
    /// </summary>
    public static string? GetExplicitInlineValue(OptionAttribute optionAttr, PropertyInfo prop,
        IReadOnlyList<string> args)
    {
        string longPrefix = $"--{GetLongName(optionAttr, prop)}=";
        string? shortPrefix = string.IsNullOrWhiteSpace(optionAttr.ShortName) ? null : $"-{optionAttr.ShortName}=";

        string? value = null;

        foreach (string arg in args)
        {
            if (arg.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = arg[longPrefix.Length..];
            }
            else if (shortPrefix is not null && arg.StartsWith(shortPrefix, StringComparison.Ordinal))
            {
                value = arg[shortPrefix.Length..];
            }
        }

        return value;
    }
}
