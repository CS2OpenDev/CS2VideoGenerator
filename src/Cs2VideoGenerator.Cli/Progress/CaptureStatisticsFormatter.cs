#region

using System.Globalization;
using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Cli.Progress;

/// <summary>
///     Turns <see cref="CaptureResult.Statistics" /> into the one line a user wants after a capture:
///     how much was rendered, how long it really took, and what encoded it.
/// </summary>
/// <remarks>
///     <para>
///         Only the offline provider fills the dictionary in - a screen recorder has no wall-clock
///         story to tell, because its wall clock and its output length are the same number - so every
///         formatter here returns null rather than a placeholder when the numbers are absent. A caller
///         that prints null prints nothing, which is what an OBS capture should print.
///     </para>
///     <para>
///         Reads the dictionary by key rather than taking a typed record: <c>Statistics</c> is
///         deliberately provider-specific, and a provider that reports fewer keys should degrade to a
///         shorter line rather than fail to build one.
///     </para>
/// </remarks>
public static class CaptureStatisticsFormatter
{
    /// <summary>
    ///     The full sentence, for example
    ///     <c>Rendered 564 frames in 8.3 s wall (68 fps, 1.13x realtime), encoder h264_nvenc p7</c>.
    ///     Null when the statistics carry no frame count, which is the marker of a provider that does
    ///     not measure its own render.
    /// </summary>
    public static string? Describe(IReadOnlyDictionary<string, object>? statistics)
    {
        if (statistics is null || Number(statistics, "framesConsumed") is not { } frames)
        {
            return null;
        }

        string line = $"Rendered {frames.ToString("0", CultureInfo.InvariantCulture)} frames";

        if (Number(statistics, "wallClockSeconds") is { } wallClock and > 0)
        {
            line += $" in {wallClock.ToString("0.0", CultureInfo.InvariantCulture)} s wall";

            List<string> rates = [];
            if (Number(statistics, "captureFps") is { } fps)
            {
                rates.Add($"{fps.ToString("0", CultureInfo.InvariantCulture)} fps");
            }

            if (Number(statistics, "realtimeFactor") is { } realtime)
            {
                rates.Add($"{realtime.ToString("0.00", CultureInfo.InvariantCulture)}x realtime");
            }

            if (rates.Count > 0)
            {
                line += $" ({string.Join(", ", rates)})";
            }
        }

        return Text(statistics, "encoder") is { } encoder
            ? line + $", encoder {encoder}{PresetSuffix(statistics)}"
            : line;
    }

    /// <summary>
    ///     The rates alone - <c>68 fps, 1.13x realtime</c> - for a display that already says how many
    ///     frames it took and how long they run for. Null when neither rate was measured.
    /// </summary>
    public static string? DescribeRates(IReadOnlyDictionary<string, object>? statistics)
    {
        if (statistics is null)
        {
            return null;
        }

        List<string> rates = [];
        if (Number(statistics, "captureFps") is { } fps)
        {
            rates.Add($"{fps.ToString("0", CultureInfo.InvariantCulture)} fps");
        }

        if (Number(statistics, "realtimeFactor") is { } realtime)
        {
            rates.Add($"{realtime.ToString("0.00", CultureInfo.InvariantCulture)}x realtime");
        }

        return rates.Count == 0 ? null : string.Join(", ", rates);
    }

    private static string PresetSuffix(IReadOnlyDictionary<string, object> statistics) =>
        Text(statistics, "encoderPreset") is { } preset ? " " + preset : "";

    /// <summary>
    ///     A numeric statistic, whatever numeric type the provider happened to box into the dictionary.
    /// </summary>
    private static double? Number(IReadOnlyDictionary<string, object> statistics, string key) =>
        statistics.TryGetValue(key, out object? value) && value is IConvertible convertible &&
        value is not string and not bool
            ? convertible.ToDouble(CultureInfo.InvariantCulture)
            : null;

    private static string? Text(IReadOnlyDictionary<string, object> statistics, string key) =>
        statistics.TryGetValue(key, out object? value) && value is string { Length: > 0 } text ? text : null;
}
