#region

using System.Diagnostics;

#endregion

namespace Cs2VideoGenerator.Core.Diagnostics;

/// <summary>
///     Provides tracing primitives (ActivitySource) for Cs2VideoGenerator.Core.
/// </summary>
public static class CsvgTracing
{
    /// <summary>
    ///     The name used for emitted activities.
    /// </summary>
    public const string ActivityName = "Cs2VideoGenerator.Core";

    /// <summary>
    ///     Version string sourced from NerdBank.GitVersioning informational version.
    /// </summary>
    public const string Version = ThisAssembly.AssemblyInformationalVersion;

    private static readonly ActivitySource ActivitySource = new(
        ActivityName,
        Version);

    internal static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal) =>
        ActivitySource.StartActivity(name, kind);

    // explicit parent context: links activities across async boundaries, e.g. a gRPC session back to
    // the task that initiated it.
    internal static Activity? StartActivity(
        string name,
        ActivityKind kind,
        ActivityContext parentContext)
    {
        // Use explicit IEnumerable types to resolve overload ambiguity
        IEnumerable<KeyValuePair<string, object?>>? tags = null;
        IEnumerable<ActivityLink>? links = null;
        return ActivitySource.StartActivity(name, kind, parentContext, tags, links);
    }
}
