#region

using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The launch end of the display-settings flow:
///     Cs2CompilationSettings.Fullscreen/Width/Height -> CsvgVideoSession.StartAsync ->
///     ICs2ProcessManager.LaunchAsync(width, height, fullscreen) -> BuildArguments.
/// </summary>
public class Cs2ProcessManagerLaunchArgumentsTests
{
    private static ArgumentExposingCs2ProcessManager CreateManager()
    {
        // mock mode skips CS2 installation discovery, so this stays hermetic
        Cs2VideoGeneratorOptions options = new() { MockMode = true };
        Cs2DirectoryUtil directoryUtil = new(options);

        return new ArgumentExposingCs2ProcessManager(
            directoryUtil,
            new Mock<IBackupManager>().Object,
            new Mock<IPluginInstaller>().Object);
    }

    [Fact]
    public async Task Fullscreen_ProducesFullscreenFlag_AndNoWindowedFlag()
    {
        await using ArgumentExposingCs2ProcessManager manager = CreateManager();

        IReadOnlyList<string> args = manager.GetLaunchArguments(new Cs2LaunchConfiguration { Fullscreen = true });

        Assert.Contains("-fullscreen", args);
        Assert.DoesNotContain("-sw", args);
    }

    [Fact]
    public async Task WindowedDefault_ProducesWindowedFlag_AndNoFullscreenFlag()
    {
        await using ArgumentExposingCs2ProcessManager manager = CreateManager();

        IReadOnlyList<string> args = manager.GetLaunchArguments(new Cs2LaunchConfiguration());

        Assert.Contains("-sw", args);
        Assert.DoesNotContain("-fullscreen", args);
    }

    [Fact]
    public async Task WidthAndHeight_ProduceResolutionArguments()
    {
        await using ArgumentExposingCs2ProcessManager manager = CreateManager();

        IReadOnlyList<string> args = manager.GetLaunchArguments(new Cs2LaunchConfiguration
        {
            Width = 1920,
            Height = 1080
        });

        int widthIndex = args.ToList().IndexOf("-w");
        int heightIndex = args.ToList().IndexOf("-h");
        Assert.True(widthIndex >= 0);
        Assert.True(heightIndex >= 0);
        Assert.Equal("1920", args[widthIndex + 1]);
        Assert.Equal("1080", args[heightIndex + 1]);
    }

    [Fact]
    public async Task NoResolution_OmitsResolutionArguments()
    {
        await using ArgumentExposingCs2ProcessManager manager = CreateManager();

        IReadOnlyList<string> args = manager.GetLaunchArguments(new Cs2LaunchConfiguration());

        Assert.DoesNotContain("-w", args);
        Assert.DoesNotContain("-h", args);
    }

    /// <summary>Exposes the protected BuildArguments.</summary>
    private sealed class ArgumentExposingCs2ProcessManager(
        Cs2DirectoryUtil directoryUtil,
        IBackupManager backupManager,
        IPluginInstaller pluginInstaller)
        : Cs2ProcessManager(directoryUtil, backupManager, pluginInstaller, NullLoggerFactory.Instance)
    {
        public IReadOnlyList<string> GetLaunchArguments(Cs2LaunchConfiguration config) =>
            BuildArguments(config).ToList();
    }
}
