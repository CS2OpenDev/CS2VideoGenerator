using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Cs2VideoGenerator.Core.Services;

/// <summary>
///     Provides access to native assets bundled with the Cs2VideoGenerator package.
///     Locates assets from the runtimes folder (NuGet package layout) with fallback
///     to the assembly directory (development builds).
/// </summary>
public sealed class NativeAssetProvider : INativeAssetProvider
{
    /// <summary>
    ///     Resolves the bundled native asset paths (server plugin and mock_server) once at
    ///     construction: the NuGet <c>runtimes/&lt;rid&gt;/native</c> folder is probed first,
    ///     then the assembly directory (development builds). An asset that resolves to nothing
    ///     leaves its path property null rather than throwing.
    /// </summary>
    /// <param name="logger">Optional logger for probe diagnostics; null disables logging.</param>
    public NativeAssetProvider(ILogger<NativeAssetProvider>? logger = null)
    {
        string assemblyDir = Path.GetDirectoryName(typeof(NativeAssetProvider).Assembly.Location)!;
        logger?.LogDebug("Assembly directory: {AssemblyDir}", assemblyDir);

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string serverPluginName = isWindows ? "server.dll" : "server.so";
        string mockServerName = isWindows ? "mock_server.exe" : "mock_server";
        string rid = GetRuntimeIdentifier();
        logger?.LogDebug("Runtime identifier: {RuntimeIdentifier}", rid);

        string runtimePath = Path.Combine(assemblyDir, "runtimes", rid, "native");
        ServerPluginPath = ResolveNativeAsset(assemblyDir, runtimePath, serverPluginName, "server plugin", logger);
        MockServerPath = ResolveNativeAsset(assemblyDir, runtimePath, mockServerName, "mock server", logger);
    }

    /// <summary>
    ///     Resolves a native asset by probing the NuGet runtimes layout first and the assembly
    ///     directory second; returns null when neither exists.
    /// </summary>
    /// <remarks>
    ///     A miss is reported at debug level, not as an error. Both assets are optional to this type -
    ///     every run needs at most one of them (mock mode needs only mock_server, a real run only the
    ///     server plugin), and this constructor cannot tell which run it is in: it is resolved from
    ///     inside the <c>Cs2VideoGeneratorOptions</c> post-configure step, so it cannot read the very
    ///     options that would say. The consumers that actually need an asset report its absence in
    ///     context and far more usefully - <c>PluginInstaller</c> throws
    ///     <c>CsvgServerPluginNotFoundException</c> naming the path it wanted, and <c>csvg doctor</c>
    ///     reports a failed check - so logging an error here only guarantees a scary line in every
    ///     mock-mode run about a file that run has no use for.
    /// </remarks>
    internal static string? ResolveNativeAsset(string assemblyDir, string runtimePath, string fileName,
        string kindLabel, ILogger<NativeAssetProvider>? logger)
    {
        string candidate = Path.Combine(runtimePath, fileName);
        logger?.LogDebug("Checking {Kind} at runtime path: {Path} — Found: {Found}",
            kindLabel, candidate, File.Exists(candidate));

        if (!File.Exists(candidate))
        {
            candidate = Path.Combine(assemblyDir, fileName);
            logger?.LogDebug("Fallback: checking {Kind} at assembly dir: {Path} — Found: {Found}",
                kindLabel, candidate, File.Exists(candidate));
        }

        if (File.Exists(candidate))
        {
            logger?.LogDebug("Resolved {Kind} path: {Path}", kindLabel, candidate);
            return candidate;
        }

        logger?.LogDebug(
            "{Kind} ({FileName}) not found in runtime path ({RuntimePath}) or assembly directory ({AssemblyDir})",
            kindLabel, fileName, runtimePath, assemblyDir);
        return null;
    }

    /// <inheritdoc />
    public string? ServerPluginPath { get; }

    /// <inheritdoc />
    public string? MockServerPath { get; }

    /// <summary>
    ///     Maps the current OS/architecture to the NuGet runtime identifier used for the
    ///     <c>runtimes/&lt;rid&gt;/native</c> package layout. Internal so tests locating native
    ///     build output use the exact same mapping.
    /// </summary>
    internal static string GetRuntimeIdentifier()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";

        return $"{os}-{arch}";
    }
}
