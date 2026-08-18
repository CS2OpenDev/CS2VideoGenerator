#region

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace Cs2VideoGenerator.Core.Compatibility;

/// <summary>
///     A plugin/game version pair that is known to be broken. Only pairs on this list
///     make session start fail (still overridable via
///     <see cref="Cs2VideoGeneratorOptions.ForceIncompatiblePlugin" /> / <c>--force</c>);
///     everything else warns at most.
/// </summary>
public sealed record KnownBrokenVersion
{
    /// <summary>
    ///     Plugin versions the entry applies to. <see langword="null" /> means all plugin versions.
    ///     Supports an exact version ("1.0.1") or a prefix wildcard ("1.0.*").
    /// </summary>
    public string? PluginVersionRange { get; init; }

    /// <summary>
    ///     The CS2 build number (steam.inf <c>ClientVersion</c>) that is known to break the plugin.
    /// </summary>
    public string GameBuild { get; init; } = "";

    /// <summary>
    ///     Human-readable explanation shown in the error message.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
///     Compatibility reference data for the plugin/game version check.
///     Shipped as an embedded JSON resource in the Core assembly (updatable via package update)
///     and overridable via <see cref="Cs2VideoGeneratorOptions.CompatibilityOverridePath" />.
/// </summary>
public sealed record PluginCompatibilityData
{
    private const string EmbeddedResourceName = "Cs2VideoGenerator.Core.Resources.plugin-compatibility.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    ///     The CS2 build number the bundled plugin was built/verified against, or
    ///     <see langword="null" /> when no reference build is recorded (in which case the game
    ///     build cannot be compared and is accepted silently).
    /// </summary>
    public string? BuiltForCs2Build { get; init; }

    /// <summary>
    ///     Version pairs known to be broken. Intentionally starts empty.
    /// </summary>
    public IReadOnlyList<KnownBrokenVersion> KnownBrokenVersions { get; init; } = [];

    /// <summary>
    ///     Loads compatibility data: from <paramref name="overridePath" /> when provided,
    ///     otherwise from the JSON resource embedded in this assembly.
    /// </summary>
    /// <exception cref="FileNotFoundException">The override path does not exist.</exception>
    /// <exception cref="JsonException">The JSON is malformed.</exception>
    public static PluginCompatibilityData Load(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            using FileStream fileStream = File.OpenRead(overridePath);
            return Deserialize(fileStream);
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
                              ?? throw new InvalidOperationException(
                                  $"Embedded resource '{EmbeddedResourceName}' not found.");
        return Deserialize(stream);
    }

    private static PluginCompatibilityData Deserialize(Stream stream) =>
        JsonSerializer.Deserialize<PluginCompatibilityData>(stream, SerializerOptions)
        ?? new PluginCompatibilityData();
}
