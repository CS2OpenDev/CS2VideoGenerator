#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Cs2VideoGenerator.Core.Models;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests.Models;

/// <summary>
///     Parse/validate coverage for the compilation model consumed by <c>generate --file compilation.json</c>:
///     <see cref="Cs2Compilation" />, <see cref="Cs2CompilationSettings" />, <see cref="Cs2CompilationClip" /> and
///     <see cref="Cs2ClipOptions" />.
/// </summary>
public class Cs2CompilationTests
{
    /// <summary>
    ///     Mirrors the exact serializer options the CLI uses to parse compilation definitions
    ///     (src/Cs2VideoGenerator.Cli/Tasks/GenerateTask.cs).
    /// </summary>
    private static readonly JsonSerializerOptions CliJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    ///     A fully-populated, structurally valid compilation JSON (every settings field, every clip field, two clips
    ///     spanning two different demo files). Used as the baseline for required-member removal tests.
    /// </summary>
    private const string FullyPopulatedJson = """
        {
          "settings": {
            "outputDirectory": "C:/Videos/CS2",
            "baseFileName": "highlights",
            "containerFormat": "mkv",
            "frameRate": 120,
            "width": 2560,
            "height": 1440,
            "fullscreen": true,
            "concatenateClips": false,
            "deleteClipsAfterConcat": true,
            "encoding": {
              "videoCodec": "libx264",
              "crf": 23,
              "audioCodec": "aac",
              "audioBitrate": "192k",
              "extraInputArgs": "-hwaccel auto",
              "extraOutputArgs": "-movflags +faststart"
            }
          },
          "clips": [
            {
              "playerSteamId": 76561198000000001,
              "matchChecksum": "checksum-match-1",
              "demoFilePath": "C:/demos/match1.dem",
              "startTick": 15000,
              "endTick": 16500,
              "playerNameToSpectate": "PlayerOne",
              "clipOptions": "XrayEnabled, HudEnabled"
            },
            {
              "playerSteamId": 76561198000000002,
              "matchChecksum": "checksum-match-2",
              "demoFilePath": "C:/demos/match2.dem",
              "startTick": 200,
              "endTick": 900,
              "playerNameToSpectate": "PlayerTwo",
              "clipOptions": "None"
            }
          ]
        }
        """;

    #region Deserialization

    /// <summary>Multi-demo: each clip references a different demo file.</summary>
    [Fact]
    public void FullyPopulatedJson_DeserializesAllFields()
    {
        Cs2Compilation? compilation = JsonSerializer.Deserialize<Cs2Compilation>(FullyPopulatedJson, CliJsonOptions);

        Assert.NotNull(compilation);

        Assert.Equal("C:/Videos/CS2", compilation.Settings.OutputDirectory);
        Assert.Equal("highlights", compilation.Settings.BaseFileName);
        Assert.Equal("mkv", compilation.Settings.ContainerFormat);
        Assert.Equal(120, compilation.Settings.FrameRate);
        Assert.Equal(2560, compilation.Settings.Width);
        Assert.Equal(1440, compilation.Settings.Height);
        Assert.True(compilation.Settings.Fullscreen);
        Assert.False(compilation.Settings.ConcatenateClips);
        Assert.True(compilation.Settings.DeleteClipsAfterConcat);

        Cs2EncodingSettings? encoding = compilation.Settings.Encoding;
        Assert.NotNull(encoding);
        Assert.Equal("libx264", encoding.VideoCodec);
        Assert.Equal(23, encoding.Crf);
        Assert.Null(encoding.VideoBitrate);
        Assert.Equal("aac", encoding.AudioCodec);
        Assert.Equal("192k", encoding.AudioBitrate);
        Assert.Equal("-hwaccel auto", encoding.ExtraInputArgs);
        Assert.Equal("-movflags +faststart", encoding.ExtraOutputArgs);

        Assert.Equal(2, compilation.Clips.Count);

        Cs2CompilationClip first = compilation.Clips[0];
        Assert.Equal(76561198000000001, first.PlayerSteamId);
        Assert.Equal("checksum-match-1", first.MatchChecksum);
        Assert.Equal("C:/demos/match1.dem", first.DemoFilePath);
        Assert.Equal(15000, first.StartTick);
        Assert.Equal(16500, first.EndTick);
        Assert.Equal("PlayerOne", first.PlayerNameToSpectate);
        Assert.Equal(Cs2ClipOptions.XrayEnabled | Cs2ClipOptions.HudEnabled, first.ClipOptions);

        Cs2CompilationClip second = compilation.Clips[1];
        Assert.Equal(76561198000000002, second.PlayerSteamId);
        Assert.Equal("checksum-match-2", second.MatchChecksum);
        Assert.Equal("C:/demos/match2.dem", second.DemoFilePath);
        Assert.Equal(200, second.StartTick);
        Assert.Equal(900, second.EndTick);
        Assert.Equal("PlayerTwo", second.PlayerNameToSpectate);
        Assert.Equal(Cs2ClipOptions.None, second.ClipOptions);

        // Multi-demo compilations are a supported differentiator: clips may reference different demo files.
        Assert.NotEqual(compilation.Clips[0].DemoFilePath, compilation.Clips[1].DemoFilePath);
    }

    [Fact]
    public void FullyPopulatedCompilation_RoundTrips()
    {
        Cs2Compilation original = JsonSerializer.Deserialize<Cs2Compilation>(FullyPopulatedJson, CliJsonOptions)!;

        string json = JsonSerializer.Serialize(original, CliJsonOptions);
        Cs2Compilation? roundTripped = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Settings.OutputDirectory, roundTripped.Settings.OutputDirectory);
        Assert.Equal(original.Settings.BaseFileName, roundTripped.Settings.BaseFileName);
        Assert.Equal(original.Settings.ContainerFormat, roundTripped.Settings.ContainerFormat);
        Assert.Equal(original.Settings.FrameRate, roundTripped.Settings.FrameRate);
        Assert.Equal(original.Settings.Width, roundTripped.Settings.Width);
        Assert.Equal(original.Settings.Height, roundTripped.Settings.Height);
        Assert.Equal(original.Settings.Fullscreen, roundTripped.Settings.Fullscreen);
        Assert.Equal(original.Settings.ConcatenateClips, roundTripped.Settings.ConcatenateClips);
        Assert.Equal(original.Settings.DeleteClipsAfterConcat, roundTripped.Settings.DeleteClipsAfterConcat);

        Assert.NotNull(roundTripped.Settings.Encoding);
        Assert.Equal(original.Settings.Encoding!.VideoCodec, roundTripped.Settings.Encoding.VideoCodec);
        Assert.Equal(original.Settings.Encoding.Crf, roundTripped.Settings.Encoding.Crf);
        Assert.Equal(original.Settings.Encoding.VideoBitrate, roundTripped.Settings.Encoding.VideoBitrate);
        Assert.Equal(original.Settings.Encoding.AudioCodec, roundTripped.Settings.Encoding.AudioCodec);
        Assert.Equal(original.Settings.Encoding.AudioBitrate, roundTripped.Settings.Encoding.AudioBitrate);
        Assert.Equal(original.Settings.Encoding.ExtraInputArgs, roundTripped.Settings.Encoding.ExtraInputArgs);
        Assert.Equal(original.Settings.Encoding.ExtraOutputArgs, roundTripped.Settings.Encoding.ExtraOutputArgs);

        Assert.Equal(original.Clips.Count, roundTripped.Clips.Count);
        for (int i = 0; i < original.Clips.Count; i++)
        {
            Assert.Equal(original.Clips[i].PlayerSteamId, roundTripped.Clips[i].PlayerSteamId);
            Assert.Equal(original.Clips[i].MatchChecksum, roundTripped.Clips[i].MatchChecksum);
            Assert.Equal(original.Clips[i].DemoFilePath, roundTripped.Clips[i].DemoFilePath);
            Assert.Equal(original.Clips[i].StartTick, roundTripped.Clips[i].StartTick);
            Assert.Equal(original.Clips[i].EndTick, roundTripped.Clips[i].EndTick);
            Assert.Equal(original.Clips[i].PlayerNameToSpectate, roundTripped.Clips[i].PlayerNameToSpectate);
            Assert.Equal(original.Clips[i].ClipOptions, roundTripped.Clips[i].ClipOptions);
        }
    }

    /// <summary>
    ///     Property matching is case-insensitive with the CLI options, so PascalCase JSON works as well as camelCase.
    /// </summary>
    [Fact]
    public void PascalCaseJson_Deserializes()
    {
        const string json = """
            {
              "Settings": { "OutputDirectory": "out", "BaseFileName": "clip" },
              "Clips": [
                {
                  "PlayerSteamId": 1,
                  "MatchChecksum": "abc",
                  "DemoFilePath": "match.dem",
                  "StartTick": 0,
                  "EndTick": 10,
                  "PlayerNameToSpectate": "p"
                }
              ]
            }
            """;

        Cs2Compilation? compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions);

        Assert.NotNull(compilation);
        Assert.Equal("out", compilation.Settings.OutputDirectory);
        Assert.Single(compilation.Clips);
    }

    #endregion

    #region Required members

    /// <summary>
    ///     Every <c>required</c> member is enforced by System.Text.Json: removing it from otherwise-valid JSON throws
    ///     <see cref="JsonException" />. Required members: settings, clips, settings.outputDirectory,
    ///     settings.baseFileName, and on each clip: playerSteamId, matchChecksum, demoFilePath, startTick, endTick,
    ///     playerNameToSpectate.
    /// </summary>
    [Theory]
    [InlineData("settings")]
    [InlineData("clips")]
    [InlineData("settings.outputDirectory")]
    [InlineData("settings.baseFileName")]
    [InlineData("clips.0.playerSteamId")]
    [InlineData("clips.0.matchChecksum")]
    [InlineData("clips.0.demoFilePath")]
    [InlineData("clips.0.startTick")]
    [InlineData("clips.0.endTick")]
    [InlineData("clips.0.playerNameToSpectate")]
    public void MissingRequiredMember_ThrowsJsonException(string propertyPath)
    {
        string json = RemoveProperty(FullyPopulatedJson, propertyPath);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions));
    }

    /// <summary>
    ///     Optional members (everything with an initializer or nullable type) may be omitted without error.
    /// </summary>
    [Theory]
    [InlineData("settings.containerFormat")]
    [InlineData("settings.frameRate")]
    [InlineData("settings.width")]
    [InlineData("settings.height")]
    [InlineData("settings.fullscreen")]
    [InlineData("settings.concatenateClips")]
    [InlineData("settings.deleteClipsAfterConcat")]
    [InlineData("settings.encoding")]
    [InlineData("settings.encoding.videoCodec")]
    [InlineData("settings.encoding.crf")]
    [InlineData("settings.encoding.audioCodec")]
    [InlineData("settings.encoding.audioBitrate")]
    [InlineData("settings.encoding.extraInputArgs")]
    [InlineData("settings.encoding.extraOutputArgs")]
    [InlineData("clips.0.clipOptions")]
    public void MissingOptionalMember_Deserializes(string propertyPath)
    {
        string json = RemoveProperty(FullyPopulatedJson, propertyPath);

        Cs2Compilation? compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions);

        Assert.NotNull(compilation);
    }

    #endregion

    #region Defaults

    /// <summary>
    ///     A minimal valid JSON (only required members) yields the documented defaults:
    ///     containerFormat "mp4", frameRate 60, width/height null, fullscreen false, concatenateClips true,
    ///     deleteClipsAfterConcat false, clipOptions Default.
    /// </summary>
    [Fact]
    public void MinimalJson_AppliesDefaults()
    {
        const string json = """
            {
              "settings": { "outputDirectory": "out", "baseFileName": "clip" },
              "clips": [
                {
                  "playerSteamId": 76561198000000001,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 100,
                  "endTick": 200,
                  "playerNameToSpectate": "p"
                }
              ]
            }
            """;

        Cs2Compilation compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions)!;

        Assert.Equal("mp4", compilation.Settings.ContainerFormat);
        Assert.Equal(60, compilation.Settings.FrameRate);
        Assert.Null(compilation.Settings.Width);
        Assert.Null(compilation.Settings.Height);
        Assert.False(compilation.Settings.Fullscreen);
        Assert.True(compilation.Settings.ConcatenateClips);
        Assert.False(compilation.Settings.DeleteClipsAfterConcat);
        Assert.Null(compilation.Settings.Encoding);
        Assert.Equal(Cs2ClipOptions.Default, compilation.Clips[0].ClipOptions);
    }

    /// <summary>
    ///     captureAudio defaults to true at the settings level and to null (inherit) at
    ///     the clip level when omitted from the JSON.
    /// </summary>
    [Fact]
    public void CaptureAudio_OmittedFromJson_DefaultsToTrueWithNoClipOverride()
    {
        const string json = """
            {
              "settings": { "outputDirectory": "out", "baseFileName": "clip" },
              "clips": [
                {
                  "playerSteamId": 76561198000000001,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 100,
                  "endTick": 200,
                  "playerNameToSpectate": "p"
                }
              ]
            }
            """;

        Cs2Compilation compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions)!;

        Assert.True(compilation.Settings.CaptureAudio);
        Assert.Null(compilation.Clips[0].CaptureAudio);
    }

    /// <summary>
    ///     captureAudio deserializes at both levels (settings-wide value plus per-clip
    ///     override) and round-trips losslessly through the CLI serializer options.
    /// </summary>
    [Fact]
    public void CaptureAudio_SettingsAndClipOverride_DeserializeAndRoundTrip()
    {
        const string json = """
            {
              "settings": { "outputDirectory": "out", "baseFileName": "clip", "captureAudio": false },
              "clips": [
                {
                  "playerSteamId": 76561198000000001,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 100,
                  "endTick": 200,
                  "playerNameToSpectate": "p",
                  "captureAudio": true
                },
                {
                  "playerSteamId": 76561198000000002,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 300,
                  "endTick": 400,
                  "playerNameToSpectate": "q"
                }
              ]
            }
            """;

        Cs2Compilation compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions)!;

        Assert.False(compilation.Settings.CaptureAudio);
        Assert.True(compilation.Clips[0].CaptureAudio);
        Assert.Null(compilation.Clips[1].CaptureAudio);

        string serialized = JsonSerializer.Serialize(compilation, CliJsonOptions);
        Cs2Compilation roundTripped = JsonSerializer.Deserialize<Cs2Compilation>(serialized, CliJsonOptions)!;

        Assert.False(roundTripped.Settings.CaptureAudio);
        Assert.True(roundTripped.Clips[0].CaptureAudio);
        Assert.Null(roundTripped.Clips[1].CaptureAudio);
    }

    /// <summary>
    ///     playbackSpeed and produceNormalizedCopy deserialize per clip, default to 1.0 / false when
    ///     omitted, and round-trip - the compilation-JSON surface of offline slow motion.
    /// </summary>
    [Fact]
    public void PlaybackSpeed_DeserializesPerClip_AndDefaults()
    {
        const string json = """
            {
              "settings": { "outputDirectory": "out", "baseFileName": "clip" },
              "clips": [
                {
                  "playerSteamId": 76561198000000001,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 100,
                  "endTick": 200,
                  "playerNameToSpectate": "p",
                  "playbackSpeed": 0.5,
                  "produceNormalizedCopy": true
                },
                {
                  "playerSteamId": 76561198000000002,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 300,
                  "endTick": 400,
                  "playerNameToSpectate": "q"
                }
              ]
            }
            """;

        Cs2Compilation compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions)!;

        Assert.Equal(0.5, compilation.Clips[0].PlaybackSpeed);
        Assert.True(compilation.Clips[0].ProduceNormalizedCopy);
        Assert.Equal(1.0, compilation.Clips[1].PlaybackSpeed); // omitted -> real time
        Assert.False(compilation.Clips[1].ProduceNormalizedCopy);

        string serialized = JsonSerializer.Serialize(compilation, CliJsonOptions);
        Cs2Compilation roundTripped = JsonSerializer.Deserialize<Cs2Compilation>(serialized, CliJsonOptions)!;
        Assert.Equal(0.5, roundTripped.Clips[0].PlaybackSpeed);
        Assert.True(roundTripped.Clips[0].ProduceNormalizedCopy);
    }

    /// <summary>Caught at validation, naming the clip - not at capture time.</summary>
    [Fact]
    public void PlaybackSpeed_NonPositive_FailsValidation()
    {
        using TempDir dir = new();
        string demo = Path.Combine(dir.Path, "match.dem");
        File.WriteAllText(demo, "x");

        Cs2Compilation compilation = new()
        {
            Settings = new Cs2CompilationSettings { OutputDirectory = dir.Path, BaseFileName = "clip" },
            Clips =
            [
                new Cs2CompilationClip
                {
                    PlayerSteamId = 1,
                    MatchChecksum = "abc",
                    DemoFilePath = demo,
                    StartTick = 100,
                    EndTick = 200,
                    PlayerNameToSpectate = "p",
                    PlaybackSpeed = 0
                }
            ]
        };

        Assert.Contains(compilation.Validate(),
            issue => issue.Contains("PlaybackSpeed") && issue.Contains("positive"));
    }

    [Fact]
    public void GetConcatenatedOutputPath_CombinesSettingsFields()
    {
        Cs2CompilationSettings settings = new()
        {
            OutputDirectory = "out", BaseFileName = "highlights", ContainerFormat = "mkv"
        };

        Assert.Equal(Path.Combine("out", "highlights.mkv"), settings.GetConcatenatedOutputPath());
    }

    #endregion

    #region README example

    /// <summary>
    ///     The README's compilation example (Usage -> generate), corrected to include the required
    ///     playerSteamId/matchChecksum clip fields, must deserialize successfully. Guards the primary user-facing
    ///     contract documented in README.md.
    /// </summary>
    [Fact]
    public void ReadmeExample_Deserializes()
    {
        const string readmeExample = """
            {
              "settings": {
                "outputDirectory": "C:/Videos/CS2",
                "baseFileName": "highlights",
                "containerFormat": "mp4",
                "frameRate": 60,
                "width": 1920,
                "height": 1080,
                "fullscreen": false,
                "concatenateClips": true,
                "deleteClipsAfterConcat": false
              },
              "clips": [
                {
                  "playerSteamId": 76561198000000001,
                  "matchChecksum": "8b3f2a1c9d4e5f60",
                  "demoFilePath": "C:/demos/match1.dem",
                  "startTick": 15000,
                  "endTick": 16500,
                  "playerNameToSpectate": "PlayerName"
                }
              ]
            }
            """;

        Cs2Compilation? compilation = JsonSerializer.Deserialize<Cs2Compilation>(readmeExample, CliJsonOptions);

        Assert.NotNull(compilation);
        Assert.Equal("C:/Videos/CS2", compilation.Settings.OutputDirectory);
        Assert.Equal("highlights", compilation.Settings.BaseFileName);
        Assert.Equal("mp4", compilation.Settings.ContainerFormat);
        Assert.Equal(60, compilation.Settings.FrameRate);
        Assert.Equal(1920, compilation.Settings.Width);
        Assert.Equal(1080, compilation.Settings.Height);
        Assert.False(compilation.Settings.Fullscreen);
        Assert.True(compilation.Settings.ConcatenateClips);
        Assert.False(compilation.Settings.DeleteClipsAfterConcat);

        Cs2CompilationClip clip = Assert.Single(compilation.Clips);
        Assert.Equal(76561198000000001, clip.PlayerSteamId);
        Assert.Equal("8b3f2a1c9d4e5f60", clip.MatchChecksum);
        Assert.Equal("C:/demos/match1.dem", clip.DemoFilePath);
        Assert.Equal(15000, clip.StartTick);
        Assert.Equal(16500, clip.EndTick);
        Assert.Equal("PlayerName", clip.PlayerNameToSpectate);
        Assert.Equal(Cs2ClipOptions.Default, clip.ClipOptions);
    }

    #endregion

    #region ClipOptions enum JSON representation

    /// <summary>
    ///     ClipOptions is serialized as a string (JsonStringEnumConverter on the property): named values write their
    ///     name, unnamed flag combinations write a comma-separated list.
    /// </summary>
    [Theory]
    [InlineData(Cs2ClipOptions.None, "None")]
    [InlineData(Cs2ClipOptions.Default, "Default")]
    [InlineData(Cs2ClipOptions.XrayEnabled | Cs2ClipOptions.HudEnabled, "XrayEnabled, HudEnabled")]
    public void ClipOptions_SerializesAsString(Cs2ClipOptions options, string expected)
    {
        Cs2CompilationClip clip = MakeClip(options);

        string json = JsonSerializer.Serialize(clip, CliJsonOptions);
        JsonNode node = JsonNode.Parse(json)!;

        Assert.Equal(expected, node["ClipOptions"]!.GetValue<string>());
    }

    /// <summary>
    ///     ClipOptions accepts single names, comma-separated flag lists, case-insensitive names, and raw numeric
    ///     values when deserializing.
    /// </summary>
    [Theory]
    [InlineData("\"None\"", Cs2ClipOptions.None)]
    [InlineData("\"Default\"", Cs2ClipOptions.Default)]
    [InlineData("\"XrayEnabled, TrueViewEnabled\"", Cs2ClipOptions.XrayEnabled | Cs2ClipOptions.TrueViewEnabled)]
    [InlineData("\"hudenabled\"", Cs2ClipOptions.HudEnabled)] // enum name matching is case-insensitive
    [InlineData("3", Cs2ClipOptions.XrayEnabled | Cs2ClipOptions.TrueViewEnabled)] // numbers are still accepted
    public void ClipOptions_DeserializesFromStringOrNumber(string jsonValue, Cs2ClipOptions expected)
    {
        string json = $$"""
            {
              "playerSteamId": 1,
              "matchChecksum": "abc",
              "demoFilePath": "match.dem",
              "startTick": 0,
              "endTick": 10,
              "playerNameToSpectate": "p",
              "clipOptions": {{jsonValue}}
            }
            """;

        Cs2CompilationClip clip = JsonSerializer.Deserialize<Cs2CompilationClip>(json, CliJsonOptions)!;

        Assert.Equal(expected, clip.ClipOptions);
    }

    [Fact]
    public void ClipOptions_UnknownName_ThrowsJsonException()
    {
        const string json = """
            {
              "playerSteamId": 1,
              "matchChecksum": "abc",
              "demoFilePath": "match.dem",
              "startTick": 0,
              "endTick": 10,
              "playerNameToSpectate": "p",
              "clipOptions": "NotARealOption"
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Cs2CompilationClip>(json, CliJsonOptions));
    }

    #endregion

    #region Validate

    [Fact]
    public void Validate_ValidCompilation_ReturnsNoIssues()
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = MakeCompilation(MakeClip(demoFilePath: demo));

            IReadOnlyList<string> issues = compilation.Validate();

            Assert.Empty(issues);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    [Fact]
    public void Validate_EmptyClips_ReportsIssue()
    {
        Cs2Compilation compilation = MakeCompilation();

        IReadOnlyList<string> issues = compilation.Validate();

        Assert.Contains("At least one clip is required", issues);
    }

    [Fact]
    public void Validate_NonexistentDemoFile_ReportsNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "missing.dem");
        Cs2Compilation compilation = MakeCompilation(MakeClip(demoFilePath: missing));

        IReadOnlyList<string> issues = compilation.Validate();

        string issue = Assert.Single(issues);
        Assert.Equal($"Clip 1: Demo file not found: \"{missing}\"", issue);
    }

    /// <summary>Blank path: reported as required, and the existence check is skipped.</summary>
    [Fact]
    public void Validate_BlankDemoFilePath_ReportsRequired()
    {
        Cs2Compilation compilation = MakeCompilation(MakeClip(demoFilePath: "   "));

        IReadOnlyList<string> issues = compilation.Validate();

        string issue = Assert.Single(issues);
        Assert.Equal("Clip 1: DemoFilePath is required", issue);
    }

    /// <summary>
    ///     Bad tick ranges are reported: EndTick must be strictly greater than StartTick, and StartTick must be
    ///     non-negative. Note: a negative EndTick is only caught indirectly via the EndTick &lt;= StartTick rule; when
    ///     StartTick is negative and EndTick is greater, only the StartTick issue fires.
    /// </summary>
    [Theory]
    [InlineData(100, 100, new[] { "Clip 1: EndTick must be greater than StartTick (StartTick=100, EndTick=100)" })]
    [InlineData(100, 50, new[] { "Clip 1: EndTick must be greater than StartTick (StartTick=100, EndTick=50)" })]
    [InlineData(0, 0, new[] { "Clip 1: EndTick must be greater than StartTick (StartTick=0, EndTick=0)" })]
    [InlineData(-5, 10, new[] { "Clip 1: StartTick must be non-negative (got -5)" })]
    [InlineData(-5, -10, new[]
    {
        "Clip 1: StartTick must be non-negative (got -5)",
        "Clip 1: EndTick must be greater than StartTick (StartTick=-5, EndTick=-10)"
    })]
    public void Validate_BadTickRanges_ReportsIssues(int startTick, int endTick, string[] expectedIssues)
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation =
                MakeCompilation(MakeClip(demoFilePath: demo, startTick: startTick, endTick: endTick));

            IReadOnlyList<string> issues = compilation.Validate();

            Assert.Equal(expectedIssues, issues);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    [Fact]
    public void Validate_InvalidSettings_ReportsAllIssues()
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = new()
            {
                Settings = new Cs2CompilationSettings
                {
                    OutputDirectory = " ", BaseFileName = "", FrameRate = 0
                },
                Clips = new[] { MakeClip(demoFilePath: demo) }
            };

            IReadOnlyList<string> issues = compilation.Validate();

            Assert.Equal(new[]
            {
                "OutputDirectory is required",
                "BaseFileName is required",
                "FrameRate must be greater than 0 (got 0)"
            }, issues);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    /// <summary>Issue messages number clips from 1, not from 0.</summary>
    [Fact]
    public void Validate_BlankPlayerName_ReportsIssueWithOneBasedClipNumber()
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = MakeCompilation(
                MakeClip(demoFilePath: demo),
                MakeClip(demoFilePath: demo, playerNameToSpectate: " "));

            IReadOnlyList<string> issues = compilation.Validate();

            string issue = Assert.Single(issues);
            Assert.Equal("Clip 2: PlayerNameToSpectate must not be empty", issue);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    /// <summary>
    ///     Documents current validation gaps (not aspirational): an empty MatchChecksum, a non-positive PlayerSteamId,
    ///     a blank ContainerFormat, and non-positive Width/Height all pass Validate() today. If Validate() is tightened
    ///     later, update this test alongside it.
    /// </summary>
    [Fact]
    public void Validate_CurrentBehavior_DoesNotFlagChecksumSteamIdContainerOrDimensions()
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = new()
            {
                Settings = new Cs2CompilationSettings
                {
                    OutputDirectory = "out",
                    BaseFileName = "clip",
                    ContainerFormat = "",
                    Width = 0,
                    Height = -1080
                },
                Clips = new[]
                {
                    MakeClip(demoFilePath: demo, playerSteamId: -1, matchChecksum: "")
                }
            };

            IReadOnlyList<string> issues = compilation.Validate();

            Assert.Empty(issues);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    #endregion

    #region Encoding block

    /// <summary>
    ///     videoBitrate deserializes as an FFmpeg-notation string (mutually exclusive with crf,
    ///     which is enforced by Validate(), not by the serializer).
    /// </summary>
    [Fact]
    public void Encoding_VideoBitrate_Deserializes()
    {
        const string json = """
            {
              "settings": {
                "outputDirectory": "out",
                "baseFileName": "clip",
                "encoding": { "videoCodec": "libx265", "videoBitrate": "8M" }
              },
              "clips": [
                {
                  "playerSteamId": 1,
                  "matchChecksum": "abc",
                  "demoFilePath": "match.dem",
                  "startTick": 0,
                  "endTick": 10,
                  "playerNameToSpectate": "p"
                }
              ]
            }
            """;

        Cs2Compilation compilation = JsonSerializer.Deserialize<Cs2Compilation>(json, CliJsonOptions)!;

        Assert.NotNull(compilation.Settings.Encoding);
        Assert.Equal("libx265", compilation.Settings.Encoding.VideoCodec);
        Assert.Equal("8M", compilation.Settings.Encoding.VideoBitrate);
        Assert.Null(compilation.Settings.Encoding.Crf);
    }

    /// <summary>
    ///     Re-encode is required only when a video or audio setting is present; the extra-args
    ///     escape hatches alone keep stream-copy mode. Pins the semantics Cs2CompilationRunner relies
    ///     on when deciding whether a single clip can be plain-copied.
    /// </summary>
    [Fact]
    public void Encoding_RequiresReEncode_TracksVideoAndAudioSettingsOnly()
    {
        Assert.False(new Cs2EncodingSettings().RequiresReEncode);
        Assert.False(new Cs2EncodingSettings
        {
            ExtraInputArgs = "-hwaccel auto", ExtraOutputArgs = "-movflags +faststart"
        }.RequiresReEncode);

        Assert.True(new Cs2EncodingSettings { VideoCodec = "libx264" }.RequiresReEncode);
        Assert.True(new Cs2EncodingSettings { Crf = 23 }.RequiresReEncode);
        Assert.True(new Cs2EncodingSettings { VideoBitrate = "8M" }.RequiresReEncode);
        Assert.True(new Cs2EncodingSettings { AudioCodec = "aac" }.RequiresReEncode);
        Assert.True(new Cs2EncodingSettings { AudioBitrate = "192k" }.RequiresReEncode);
    }

    /// <summary>The crf bounds 0 and 51 are both inclusive.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(51)]
    public void Validate_ValidEncoding_ReturnsNoIssues(int? crf)
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = MakeCompilationWithEncoding(
                new Cs2EncodingSettings { VideoCodec = "libx264", Crf = crf, AudioCodec = "aac" }, demo);

            Assert.Empty(compilation.Validate());
        }
        finally
        {
            File.Delete(demo);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(52)]
    public void Validate_CrfOutOfRange_ReportsIssue(int crf)
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = MakeCompilationWithEncoding(new Cs2EncodingSettings { Crf = crf }, demo);

            string issue = Assert.Single(compilation.Validate());
            Assert.Equal($"Encoding: Crf must be between 0 and 51 (got {crf})", issue);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    [Fact]
    public void Validate_CrfAndVideoBitrate_ReportsMutuallyExclusive()
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = MakeCompilationWithEncoding(
                new Cs2EncodingSettings { Crf = 23, VideoBitrate = "8M" }, demo);

            string issue = Assert.Single(compilation.Validate());
            Assert.Equal("Encoding: Crf and VideoBitrate are mutually exclusive; set only one", issue);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    /// <summary>An encoding string that is present must be non-empty; each blank one is reported.</summary>
    [Fact]
    public void Validate_BlankEncodingStrings_ReportsEachIssue()
    {
        string demo = CreateTempDemoFile();
        try
        {
            Cs2Compilation compilation = MakeCompilationWithEncoding(new Cs2EncodingSettings
            {
                VideoCodec = "",
                VideoBitrate = " ",
                AudioCodec = "",
                AudioBitrate = " ",
                ExtraInputArgs = "",
                ExtraOutputArgs = " "
            }, demo);

            IReadOnlyList<string> issues = compilation.Validate();

            Assert.Equal(new[]
            {
                "Encoding: VideoCodec must not be empty when specified",
                "Encoding: VideoBitrate must not be empty when specified",
                "Encoding: AudioCodec must not be empty when specified",
                "Encoding: AudioBitrate must not be empty when specified",
                "Encoding: ExtraInputArgs must not be empty when specified",
                "Encoding: ExtraOutputArgs must not be empty when specified"
            }, issues);
        }
        finally
        {
            File.Delete(demo);
        }
    }

    #endregion

    #region Helpers

    private static Cs2Compilation MakeCompilation(params Cs2CompilationClip[] clips) =>
        new()
        {
            Settings = new Cs2CompilationSettings { OutputDirectory = "out", BaseFileName = "clip" },
            Clips = clips
        };

    private static Cs2Compilation MakeCompilationWithEncoding(Cs2EncodingSettings encoding, string demoFilePath) =>
        new()
        {
            Settings = new Cs2CompilationSettings
            {
                OutputDirectory = "out", BaseFileName = "clip", Encoding = encoding
            },
            Clips = new[] { MakeClip(demoFilePath: demoFilePath) }
        };

    private static Cs2CompilationClip MakeClip(
        Cs2ClipOptions? clipOptions = null,
        string demoFilePath = "match.dem",
        int startTick = 100,
        int endTick = 200,
        string playerNameToSpectate = "PlayerName",
        long playerSteamId = 76561198000000001,
        string matchChecksum = "abc123") =>
        new()
        {
            PlayerSteamId = playerSteamId,
            MatchChecksum = matchChecksum,
            DemoFilePath = demoFilePath,
            StartTick = startTick,
            EndTick = endTick,
            PlayerNameToSpectate = playerNameToSpectate,
            ClipOptions = clipOptions ?? Cs2ClipOptions.Default
        };

    private static string CreateTempDemoFile()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dem");
        File.WriteAllText(path, "demo");
        return path;
    }

    /// <summary>
    ///     Removes a property from a JSON document by dot-separated path (numeric segments index into arrays).
    /// </summary>
    private static string RemoveProperty(string json, string propertyPath)
    {
        JsonNode root = JsonNode.Parse(json)!;
        string[] segments = propertyPath.Split('.');

        JsonNode current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = int.TryParse(segments[i], out int index)
                ? current[index]!
                : current[segments[i]]!;
        }

        ((JsonObject)current).Remove(segments[^1]);
        return root.ToJsonString();
    }

    #endregion
}
