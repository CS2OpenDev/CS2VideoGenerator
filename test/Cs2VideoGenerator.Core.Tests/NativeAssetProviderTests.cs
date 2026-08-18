#region

using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Native asset resolution is best-effort by design. A run needs at most one of the two assets
///     (mock mode only mock_server, a real run only the server plugin), and this type is resolved
///     from inside the options post-configure step, so it cannot tell which run it is in. A miss is
///     a debug note here; whoever actually needed the asset fails loudly and in context.
/// </summary>
public class NativeAssetProviderTests
{
    /// <summary>The NuGet <c>runtimes/&lt;rid&gt;/native</c> layout wins, full path returned.</summary>
    [Fact]
    public void ResolveNativeAsset_PrefersTheRuntimesLayout_OverTheAssemblyDirectory()
    {
        using TempDir dir = new();
        string assemblyDir = dir.Combine("bin");
        string runtimePath = Path.Combine(assemblyDir, "runtimes", "osx-arm64", "native");
        Directory.CreateDirectory(runtimePath);

        string preferred = Path.Combine(runtimePath, "server.so");
        File.WriteAllBytes(preferred, [0x00]);
        File.WriteAllBytes(Path.Combine(assemblyDir, "server.so"), [0x00]);

        RecordingLogger logger = new();
        string? resolved = NativeAssetProvider.ResolveNativeAsset(assemblyDir, runtimePath, "server.so",
            "server plugin", logger);

        Assert.Equal(preferred, resolved);
    }

    /// <summary>The assembly directory is the dev-build fallback when the packaged layout is absent.</summary>
    [Fact]
    public void ResolveNativeAsset_FallsBackToTheAssemblyDirectory()
    {
        using TempDir dir = new();
        string assemblyDir = dir.Path;
        string runtimePath = Path.Combine(assemblyDir, "runtimes", "osx-arm64", "native");

        string fallback = Path.Combine(assemblyDir, "mock_server");
        File.WriteAllBytes(fallback, [0x00]);

        string? resolved = NativeAssetProvider.ResolveNativeAsset(assemblyDir, runtimePath, "mock_server",
            "mock server", new RecordingLogger());

        Assert.Equal(fallback, resolved);
    }

    /// <summary>
    ///     Every CLI command in mock mode used to print <c>fail: ... server plugin (server.so) not
    ///     found</c> before doing anything, for a file mock mode never loads. An unresolved asset is
    ///     a null path and a debug note, nothing at warning or above: nothing has gone wrong yet.
    /// </summary>
    [Fact]
    public void ResolveNativeAsset_WhenTheAssetIsMissing_ReturnsNullWithoutRaisingTheAlarm()
    {
        using TempDir dir = new();
        string runtimePath = Path.Combine(dir.Path, "runtimes", "osx-arm64", "native");

        RecordingLogger logger = new();
        string? resolved = NativeAssetProvider.ResolveNativeAsset(dir.Path, runtimePath, "server.so",
            "server plugin", logger);

        Assert.Null(resolved);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);

        // still said, so a support log with debug on explains the null
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("server.so") && e.Message.Contains("not found"));
    }

    /// <summary>Captures log entries so a test can assert on the level a miss is reported at.</summary>
    private sealed class RecordingLogger : ILogger<NativeAssetProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
