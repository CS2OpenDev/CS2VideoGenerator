#region

using Cs2VideoGenerator.Core.Compatibility;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests.Compatibility;

/// <summary>
///     Tests for the pure plugin/game version comparison logic.
///     Policy under test: match/known-good is silent, newer or unknown versions warn (never block),
///     and only pairs on the explicit known-broken list are reported as incompatible.
/// </summary>
public class PluginCompatibilityCheckerTests
{
    private static PluginCompatibilityChecker CreateChecker(
        string? builtForCs2Build = null,
        params KnownBrokenVersion[] knownBroken) =>
        new(new PluginCompatibilityData
        {
            BuiltForCs2Build = builtForCs2Build,
            KnownBrokenVersions = knownBroken
        });

    [Fact]
    public void Check_GameBuildMatchesReference_IsCompatible()
    {
        PluginCompatibilityResult result = CreateChecker("14093").Check("1.0.1", "14093");

        Assert.Equal(PluginCompatibilityStatus.Compatible, result.Status);
        Assert.False(result.IsKnownIncompatible);
    }

    [Fact]
    public void Check_NoReferenceBuildRecorded_IsCompatible()
    {
        // With no reference build there is nothing to contradict - stay silent rather than
        // warning on every single session.
        PluginCompatibilityResult result = CreateChecker().Check("1.0.1", "14093");

        Assert.Equal(PluginCompatibilityStatus.Compatible, result.Status);
    }

    [Fact]
    public void Check_GameNewerThanReferenceBuild_WarnsButDoesNotBlock()
    {
        PluginCompatibilityResult result = CreateChecker("14093").Check("1.0.1", "14100");

        Assert.Equal(PluginCompatibilityStatus.GameVersionMismatch, result.Status);
        Assert.False(result.IsKnownIncompatible);
        Assert.Contains("built for CS2 build 14093", result.Message);
        Assert.Contains("14100", result.Message);
        Assert.Contains("usually fine", result.Message);
    }

    [Theory]
    [InlineData(null, "14093")] // old plugin: no version fields at all
    [InlineData("", "14093")]
    [InlineData("1.0.1", null)] // game version could not be determined
    [InlineData("1.0.1", "")]
    [InlineData(null, null)]
    public void Check_MissingVersionFields_WarnsButDoesNotBlock(string? pluginVersion, string? gameVersion)
    {
        PluginCompatibilityResult result = CreateChecker("14093").Check(pluginVersion, gameVersion);

        Assert.Equal(PluginCompatibilityStatus.UnknownVersion, result.Status);
        Assert.False(result.IsKnownIncompatible);
    }

    [Fact]
    public void Check_GameBuildOnKnownBrokenList_IsKnownIncompatible()
    {
        PluginCompatibilityChecker checker = CreateChecker("14093",
            new KnownBrokenVersion { GameBuild = "14200", Reason = "engine interface changed" });

        PluginCompatibilityResult result = checker.Check("1.0.1", "14200");

        Assert.Equal(PluginCompatibilityStatus.KnownIncompatible, result.Status);
        Assert.True(result.IsKnownIncompatible);
        Assert.Contains("engine interface changed", result.Message);
        Assert.Contains("--force", result.Message);
    }

    [Fact]
    public void Check_KnownBrokenWithMatchingPluginRange_IsKnownIncompatible()
    {
        PluginCompatibilityChecker checker = CreateChecker(null,
            new KnownBrokenVersion { PluginVersionRange = "1.0.*", GameBuild = "14200" });

        Assert.True(checker.Check("1.0.5", "14200").IsKnownIncompatible);
    }

    [Fact]
    public void Check_KnownBrokenWithNonMatchingPluginRange_DoesNotBlock()
    {
        // The broken entry only applies to 1.0.x plugins; a 1.1.0 plugin must not be blocked.
        PluginCompatibilityChecker checker = CreateChecker(null,
            new KnownBrokenVersion { PluginVersionRange = "1.0.*", GameBuild = "14200" });

        PluginCompatibilityResult result = checker.Check("1.1.0", "14200");

        Assert.False(result.IsKnownIncompatible);
        Assert.Equal(PluginCompatibilityStatus.Compatible, result.Status);
    }

    [Fact]
    public void Check_KnownBrokenWithRange_UnknownPluginVersionDoesNotBlock()
    {
        // A restricted range cannot be proven to match an unreported plugin version -> warn only.
        PluginCompatibilityChecker checker = CreateChecker(null,
            new KnownBrokenVersion { PluginVersionRange = "1.0.*", GameBuild = "14200" });

        PluginCompatibilityResult result = checker.Check(null, "14200");

        Assert.Equal(PluginCompatibilityStatus.UnknownVersion, result.Status);
    }

    [Theory]
    [InlineData("1.0.1", null, true)] // null range matches everything
    [InlineData("1.0.1", "", true)]
    [InlineData("1.0.1", "1.0.1", true)] // exact
    [InlineData("1.0.2", "1.0.1", false)]
    [InlineData("1.0.9", "1.0.*", true)] // prefix wildcard
    [InlineData("1.1.0", "1.0.*", false)]
    [InlineData(null, "1.0.*", false)] // unknown version only matches match-everything ranges
    [InlineData(null, null, true)]
    public void VersionMatchesRange_Cases(string? version, string? range, bool expected) =>
        Assert.Equal(expected, PluginCompatibilityChecker.VersionMatchesRange(version, range));

    [Fact]
    public void Load_EmbeddedResource_StartsEmpty()
    {
        PluginCompatibilityData data = PluginCompatibilityData.Load();

        // The shipped known-broken list intentionally starts empty (warn-not-block policy).
        Assert.Null(data.BuiltForCs2Build);
        Assert.Empty(data.KnownBrokenVersions);
    }

    [Fact]
    public void Load_OverridePath_ReplacesEmbeddedData()
    {
        using TempDir tempDir = new("csvg-compat-");
        string path = tempDir.Combine("override.json");
        File.WriteAllText(path,
            """
            {
              "builtForCs2Build": "14093",
              "knownBrokenVersions": [
                { "pluginVersionRange": "1.0.*", "gameBuild": "14200", "reason": "test entry" }
              ]
            }
            """);

        PluginCompatibilityData data = PluginCompatibilityData.Load(path);

        Assert.Equal("14093", data.BuiltForCs2Build);
        KnownBrokenVersion entry = Assert.Single(data.KnownBrokenVersions);
        Assert.Equal("1.0.*", entry.PluginVersionRange);
        Assert.Equal("14200", entry.GameBuild);
        Assert.Equal("test entry", entry.Reason);
    }
}
