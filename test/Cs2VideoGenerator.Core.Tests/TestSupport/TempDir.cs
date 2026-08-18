namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Shared test fixture: creates a unique temp directory and deletes it (recursively,
///     best-effort) on dispose. Replaces the per-file temp-dir try/finally patterns.
/// </summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string prefix = "csvg-test-")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Returns the absolute path of the given relative segments inside this directory.</summary>
    public string Combine(params string[] paths) => System.IO.Path.Combine([Path, .. paths]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
