namespace Cs2VideoGenerator.Core.Compatibility;

/// <summary>
///     Outcome category of a plugin/game compatibility check.
/// </summary>
public enum PluginCompatibilityStatus
{
    /// <summary>Versions match the reference data, or nothing contradicts them. Log debug, stay silent.</summary>
    Compatible,

    /// <summary>Plugin and/or game version was not reported. Warn once and proceed.</summary>
    UnknownVersion,

    /// <summary>The running game build differs from the build the plugin targets. Warn once and proceed.</summary>
    GameVersionMismatch,

    /// <summary>The version pair is on the known-broken list. Error unless forced.</summary>
    KnownIncompatible
}

/// <summary>
///     Result of a compatibility check: a status category and a human-readable message
///     suitable for the corresponding log line or error.
/// </summary>
public sealed record PluginCompatibilityResult(PluginCompatibilityStatus Status, string Message)
{
    /// <summary>Whether the pair is on the known-broken list (the only status that may block).</summary>
    public bool IsKnownIncompatible => Status == PluginCompatibilityStatus.KnownIncompatible;
}

/// <summary>
///     Pure comparison logic for the plugin/game version handshake.
///     The policy is forward-compatible, because a game update usually does not break the plugin:
///     newer or unknown versions produce a warning and the session proceeds, and only pairs on the
///     explicit known-broken list are reported as incompatible. Even those are overridable via
///     <c>--force</c> / <see cref="Cs2VideoGeneratorOptions.ForceIncompatiblePlugin" />.
/// </summary>
public sealed class PluginCompatibilityChecker(PluginCompatibilityData data)
{
    private readonly PluginCompatibilityData _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>
    ///     Compares the versions reported by the plugin handshake against the reference data.
    /// </summary>
    /// <param name="pluginVersion">The CSVG plugin version reported by the plugin, or null/empty if not reported.</param>
    /// <param name="gameVersion">The running CS2 build number reported by the plugin, or null/empty if unknown.</param>
    public PluginCompatibilityResult Check(string? pluginVersion, string? gameVersion)
    {
        bool hasPluginVersion = !string.IsNullOrWhiteSpace(pluginVersion);
        bool hasGameVersion = !string.IsNullOrWhiteSpace(gameVersion);

        // the known-broken list is the only path that may block, and --force still overrides it
        if (hasGameVersion)
        {
            KnownBrokenVersion? broken = _data.KnownBrokenVersions.FirstOrDefault(entry =>
                string.Equals(entry.GameBuild, gameVersion, StringComparison.OrdinalIgnoreCase) &&
                VersionMatchesRange(pluginVersion, entry.PluginVersionRange));

            if (broken is not null)
            {
                string reason = string.IsNullOrWhiteSpace(broken.Reason) ? "known incompatibility" : broken.Reason;
                return new PluginCompatibilityResult(PluginCompatibilityStatus.KnownIncompatible,
                    $"Plugin {pluginVersion ?? "(unknown version)"} is known to be incompatible with CS2 build " +
                    $"{gameVersion}: {reason}. Update CSVG, or pass --force " +
                    "(Cs2VideoGenerator:ForceIncompatiblePlugin) to try anyway.");
            }
        }

        // missing fields, e.g. an older plugin that predates the handshake: warn and proceed
        if (!hasPluginVersion || !hasGameVersion)
        {
            string missing = (hasPluginVersion, hasGameVersion) switch
            {
                (false, false) => "plugin and game versions are unknown (plugin predates the version handshake?)",
                (false, true) => $"plugin version is unknown (game build {gameVersion})",
                _ => $"game version is unknown (plugin {pluginVersion})"
            };
            return new PluginCompatibilityResult(PluginCompatibilityStatus.UnknownVersion,
                $"Plugin compatibility could not be verified: {missing} - usually fine; if the session fails " +
                "to connect or behaves oddly, check for a CSVG update.");
        }

        // a different (usually newer) game build is expected to keep working. warn once, never block.
        if (!string.IsNullOrWhiteSpace(_data.BuiltForCs2Build) &&
            !string.Equals(_data.BuiltForCs2Build, gameVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new PluginCompatibilityResult(PluginCompatibilityStatus.GameVersionMismatch,
                $"Plugin built for CS2 build {_data.BuiltForCs2Build}, game is {gameVersion} - usually fine; " +
                "if the session fails to connect or behaves oddly, check for a CSVG update.");
        }

        string detail = string.IsNullOrWhiteSpace(_data.BuiltForCs2Build)
            ? $"plugin {pluginVersion}, game build {gameVersion} (no reference build recorded)"
            : $"plugin {pluginVersion}, game build {gameVersion} (matches reference build)";
        return new PluginCompatibilityResult(PluginCompatibilityStatus.Compatible, detail);
    }

    /// <summary>
    ///     Matches a version against a range expression: <see langword="null" />/empty matches everything,
    ///     "0.10.0" matches exactly, "0.10.*" matches any version with that prefix.
    ///     An unknown version only matches the match-everything range.
    /// </summary>
    internal static bool VersionMatchesRange(string? version, string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true; // no range restriction: applies to all plugin versions
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return false; // a restricted range cannot match an unreported version
        }

        return range.EndsWith("*", StringComparison.Ordinal)
            ? version.StartsWith(range[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(version, range, StringComparison.OrdinalIgnoreCase);
    }
}
