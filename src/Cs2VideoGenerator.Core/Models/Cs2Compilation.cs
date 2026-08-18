namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     A multi-clip compilation: the clips to capture and the settings the output is built with.
/// </summary>
public class Cs2Compilation
{
    /// <summary>
    ///     Output settings for the compilation (directory, filename pattern, video settings)
    /// </summary>
    public required Cs2CompilationSettings Settings { get; init; }

    /// <summary>
    ///     Ordered list of clips to capture. Processed sequentially.
    /// </summary>
    public required IReadOnlyList<Cs2CompilationClip> Clips { get; init; }

    /// <summary>
    ///     Validates the compilation and returns any issues found.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> issues = new();

        if (string.IsNullOrWhiteSpace(Settings.OutputDirectory))
        {
            issues.Add("OutputDirectory is required");
        }

        if (string.IsNullOrWhiteSpace(Settings.BaseFileName))
        {
            issues.Add("BaseFileName is required");
        }

        if (Settings.FrameRate <= 0)
        {
            issues.Add($"FrameRate must be greater than 0 (got {Settings.FrameRate})");
        }

        // the encoding block is optional; absent means stream copy
        if (Settings.Encoding is { } encoding)
        {
            if (encoding.Crf is { } crf && crf is < 0 or > 51)
            {
                issues.Add($"Encoding: Crf must be between 0 and 51 (got {crf})");
            }

            if (encoding.Crf.HasValue && encoding.VideoBitrate is not null)
            {
                issues.Add("Encoding: Crf and VideoBitrate are mutually exclusive; set only one");
            }

            AddIssueIfEmpty(encoding.VideoCodec, "VideoCodec");
            AddIssueIfEmpty(encoding.VideoBitrate, "VideoBitrate");
            AddIssueIfEmpty(encoding.AudioCodec, "AudioCodec");
            AddIssueIfEmpty(encoding.AudioBitrate, "AudioBitrate");
            AddIssueIfEmpty(encoding.ExtraInputArgs, "ExtraInputArgs");
            AddIssueIfEmpty(encoding.ExtraOutputArgs, "ExtraOutputArgs");

            void AddIssueIfEmpty(string? value, string name)
            {
                if (value is not null && string.IsNullOrWhiteSpace(value))
                {
                    issues.Add($"Encoding: {name} must not be empty when specified");
                }
            }
        }

        if (Clips.Count == 0)
        {
            issues.Add("At least one clip is required");
        }

        for (int i = 0; i < Clips.Count; i++)
        {
            Cs2CompilationClip clip = Clips[i];
            int clipNumber = i + 1;

            if (string.IsNullOrWhiteSpace(clip.DemoFilePath))
            {
                issues.Add($"Clip {clipNumber}: DemoFilePath is required");
            }
            else if (!File.Exists(clip.DemoFilePath))
            {
                issues.Add($"Clip {clipNumber}: Demo file not found: \"{clip.DemoFilePath}\"");
            }

            if (clip.StartTick < 0)
            {
                issues.Add($"Clip {clipNumber}: StartTick must be non-negative (got {clip.StartTick})");
            }

            if (clip.EndTick <= clip.StartTick)
            {
                issues.Add(
                    $"Clip {clipNumber}: EndTick must be greater than StartTick (StartTick={clip.StartTick}, EndTick={clip.EndTick})");
            }

            if (string.IsNullOrWhiteSpace(clip.PlayerNameToSpectate))
            {
                issues.Add($"Clip {clipNumber}: PlayerNameToSpectate must not be empty");
            }

            if (clip.PlaybackSpeed <= 0 || !double.IsFinite(clip.PlaybackSpeed))
            {
                issues.Add(
                    $"Clip {clipNumber}: PlaybackSpeed must be a positive number (got {clip.PlaybackSpeed}); " +
                    "1 is real time, 0.5 is 2x slow motion.");
            }
        }

        return issues;
    }
}
