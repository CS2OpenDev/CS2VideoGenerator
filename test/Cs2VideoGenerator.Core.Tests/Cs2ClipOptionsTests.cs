#region

using Cs2VideoGenerator.Core.Models;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

public class Cs2ClipOptionsTests
{
    /// <summary>NoHudDefault is Default minus the HUD, plus death notices - nothing else moves.</summary>
    [Fact]
    public void NoHudDefault_ClearsHudAndShowsOnlyDeathNotices()
    {
        Assert.False(Cs2ClipOptions.NoHudDefault.HasFlag(Cs2ClipOptions.HudEnabled));
        Assert.True(Cs2ClipOptions.NoHudDefault.HasFlag(Cs2ClipOptions.ShowOnlyDeathNotices));

        // The rest of Default is preserved.
        Assert.True(Cs2ClipOptions.NoHudDefault.HasFlag(Cs2ClipOptions.XrayEnabled));
        Assert.True(Cs2ClipOptions.NoHudDefault.HasFlag(Cs2ClipOptions.TrueViewEnabled));
        Assert.True(Cs2ClipOptions.NoHudDefault.HasFlag(Cs2ClipOptions.ShowAssistsInFeed));

        Assert.Equal(
            (Cs2ClipOptions.Default & ~Cs2ClipOptions.HudEnabled) | Cs2ClipOptions.ShowOnlyDeathNotices,
            Cs2ClipOptions.NoHudDefault);
    }

    [Fact]
    public void EnumValues_AreConsistent()
    {
        Assert.Equal(Cs2ClipOptions.None, (Cs2ClipOptions)0);

        Assert.Equal(
            Cs2ClipOptions.XrayEnabled
            | Cs2ClipOptions.TrueViewEnabled
            | Cs2ClipOptions.HudEnabled
            | Cs2ClipOptions.ShowAssistsInFeed,
            Cs2ClipOptions.Default);

        Assert.True(Cs2ClipOptions.Default.HasFlag(Cs2ClipOptions.HudEnabled));
        Assert.False(Cs2ClipOptions.Default.HasFlag(Cs2ClipOptions.ShowOnlyDeathNotices));

        // Individual flags are distinct single bits.
        Cs2ClipOptions[] singleFlags =
        [
            Cs2ClipOptions.XrayEnabled,
            Cs2ClipOptions.TrueViewEnabled,
            Cs2ClipOptions.HudEnabled,
            Cs2ClipOptions.ShowAssistsInFeed,
            Cs2ClipOptions.ShowOnlyDeathNotices
        ];
        foreach (Cs2ClipOptions flag in singleFlags)
        {
            Assert.NotEqual(Cs2ClipOptions.None, flag);
            Assert.Equal(0, (int)flag & ((int)flag - 1)); // power of two
        }

        Assert.Equal(singleFlags.Length, singleFlags.Distinct().Count());
    }
}
