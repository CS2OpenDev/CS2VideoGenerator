#region

using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Platform;
using Cs2VideoGenerator.Core.ProcessManagement;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     OBS executable discovery. <see cref="CachedPathDiscoveryService" /> is
///     the single implementation; <see cref="ObsProcessManager" /> (and through it
///     <see cref="ObsCaptureSessionFactory" />) only delegates via
///     <see cref="IPathDiscoveryService" />.
/// </summary>
public class ObsDiscoveryTests
{
    private static CachedPathDiscoveryService CreateService(Mock<IPathDiscoveryCache> cache) =>
        new(cache.Object,
            OperatingSystem.IsWindows()
                ? new WindowsPlatformPathProvider()
                : new LinuxPlatformPathProvider(),
            NullLogger<CachedPathDiscoveryService>.Instance);

    [Fact]
    public void GetObsExecutablePath_ReturnsCachedValueWithoutProbing()
    {
        Mock<IPathDiscoveryCache> cache = new();
        cache.Setup(c => c.Load()).Returns(new CachedPathData { ObsExecutablePath = "/cached/obs64" });

        CachedPathDiscoveryService service = CreateService(cache);
        service.FileExists = _ => throw new InvalidOperationException("Cache hit must not probe the filesystem");
        service.LinuxObsLocator = () => throw new InvalidOperationException("Cache hit must not run discovery");

        Assert.Equal("/cached/obs64", service.GetObsExecutablePath());
        cache.Verify(c => c.Save(It.IsAny<CachedPathData>()), Times.Never);
    }

    [Fact]
    public void GetObsExecutablePath_DiscoversFirstExistingCandidate_AndCachesIt()
    {
        Mock<IPathDiscoveryCache> cache = new();
        cache.Setup(c => c.Load()).Returns((CachedPathData?)null);

        IReadOnlyList<string> candidates = CachedPathDiscoveryService.GetObsCandidatePaths();
        Assert.NotEmpty(candidates); // sanity: every supported OS has well-known candidates
        string expected = candidates[0];

        CachedPathDiscoveryService service = CreateService(cache);
        service.FileExists = path => path == expected;
        service.LinuxObsLocator = () => null;

        Assert.Equal(expected, service.GetObsExecutablePath());
        cache.Verify(c => c.Save(It.Is<CachedPathData>(d => d.ObsExecutablePath == expected)), Times.Once);
    }

    [Fact]
    public void GetObsExecutablePath_NothingFound_ReturnsNullAndDoesNotCache()
    {
        Mock<IPathDiscoveryCache> cache = new();
        cache.Setup(c => c.Load()).Returns((CachedPathData?)null);

        CachedPathDiscoveryService service = CreateService(cache);
        service.FileExists = _ => false;
        service.LinuxObsLocator = () => null;

        Assert.Null(service.GetObsExecutablePath());
        cache.Verify(c => c.Save(It.IsAny<CachedPathData>()), Times.Never);
    }

    [Fact]
    public void ObsProcessManager_ResolveExecutablePath_PrefersConfiguredPath()
    {
        Mock<IPathDiscoveryService> discovery = new();
        ObsProcessManager manager = new(NullLoggerFactory.Instance, discovery.Object);

        ObsCaptureConfiguration config = new() { ExecutablePath = "/custom/obs" };

        Assert.Equal("/custom/obs", manager.ResolveExecutablePath(config));
        discovery.Verify(d => d.GetObsExecutablePath(), Times.Never);
    }

    [Fact]
    public void ObsProcessManager_ResolveExecutablePath_DelegatesToSingleDiscoveryImplementation()
    {
        // ObsProcessManager (and the OBS capture factory, which delegates to it) has no local
        // OBS path list — discovery must flow through IPathDiscoveryService.
        Mock<IPathDiscoveryService> discovery = new();
        discovery.Setup(d => d.GetObsExecutablePath()).Returns("/discovered/obs");
        ObsProcessManager manager = new(NullLoggerFactory.Instance, discovery.Object);

        Assert.Equal("/discovered/obs", manager.ResolveExecutablePath(new ObsCaptureConfiguration()));
        discovery.Verify(d => d.GetObsExecutablePath(), Times.Once);
    }

    [Fact]
    public void ObsProcessManager_RequiresPathDiscoveryService()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ObsProcessManager(NullLoggerFactory.Instance, null!));
    }
}
