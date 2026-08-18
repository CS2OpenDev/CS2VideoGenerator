#region

using System.Diagnostics;
using System.Globalization;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Drives the real ffmpeg/ffprobe binaries for the tests that need them: the availability gate
///     the ffmpeg-dependent cases skip on, plus generation of test media and inspection of results.
///     Shared so the gate exists once rather than once per test class.
/// </summary>
internal static class FfmpegTools
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process == null)
            {
                return false;
            }

            // Drain before waiting, for the reason spelled out in Run below: `ffmpeg -version`
            // prints its whole configure line, which on a full build is already most of the pipe
            // buffer, and an undrained pipe would hang this probe rather than answer it.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            process.WaitForExit(10_000);
            if (!process.HasExited)
            {
                return false;
            }

            stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    ///     Whether ffmpeg is on PATH and runnable.
    /// </summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>
    ///     Skips the calling test when ffmpeg is not installed.
    /// </summary>
    public static void SkipUnlessAvailable() =>
        Assert.SkipUnless(IsAvailable, "ffmpeg not found on PATH");

    /// <summary>
    ///     Runs a tool to completion and fails the test if it does not exit cleanly.
    /// </summary>
    public static string Run(string tool, IReadOnlyList<string> arguments, int timeoutMs = 120_000)
    {
        ProcessStartInfo psi = new()
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(psi);
        Assert.NotNull(process);

        // Both reads start before the wait, and neither is optional: ffmpeg writes its banner and
        // encode progress to stderr, and once a redirected pipe's buffer fills the tool blocks on
        // the write and never exits. The symptom is this wait timing out, which reads like a slow
        // encode rather than a deadlock the caller created.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{tool} timed out after {timeoutMs} ms");
        }

        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"{tool} exited with {process.ExitCode}: {error}");
        return output;
    }

    /// <summary>
    ///     Writes a numbered sequence of frame images of a test pattern into
    ///     <paramref name="directory" />, named <c>frame0001.ext</c> upward.
    ///     Targa is written uncompressed: run-length encoding would make the frames vary in size,
    ///     and a Targa pipe is only splittable at a fixed record length.
    /// </summary>
    public static void GenerateFrameSequence(
        string directory,
        string extension,
        int frameCount,
        int frameRate = 24,
        string size = "160x120")
    {
        Directory.CreateDirectory(directory);

        List<string> arguments =
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"testsrc=duration={(frameCount / (double)frameRate).ToString("0.####", CultureInfo.InvariantCulture)}:size={size}:rate={frameRate}"
        ];

        if (extension == "tga")
        {
            arguments.AddRange(["-vcodec", "targa", "-rle", "0"]);
        }

        arguments.AddRange(["-f", "image2", Path.Combine(directory, $"frame%04d.{extension}")]);
        Run("ffmpeg", arguments);
    }

    /// <summary>
    ///     Generates a silent wav of the requested length.
    /// </summary>
    public static void GenerateSilentWav(string path, double seconds)
    {
        Run("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", seconds.ToString("0.####", CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le", path
        ]);
    }

    /// <summary>
    ///     Generates a video-only test clip of the requested length.
    /// </summary>
    public static void GenerateSilentVideo(string path, double seconds, int frameRate = 30)
    {
        Run("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"testsrc=duration={seconds.ToString("0.####", CultureInfo.InvariantCulture)}:size=160x120:rate={frameRate}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path
        ]);
    }

    /// <summary>
    ///     Counts the video frames actually decodable from a file.
    /// </summary>
    public static int CountVideoFrames(string path) =>
        int.Parse(
            Run("ffprobe",
            [
                "-v", "error", "-select_streams", "v:0", "-count_frames",
                "-show_entries", "stream=nb_read_frames", "-of", "default=nw=1:nk=1", path
            ]).Trim(),
            CultureInfo.InvariantCulture);

    /// <summary>
    ///     The video stream's nominal frame rate, as ffprobe's rational string (for example "24/1").
    /// </summary>
    public static string VideoFrameRate(string path) =>
        Run("ffprobe",
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=r_frame_rate", "-of", "default=nw=1:nk=1", path
        ]).Trim();

    /// <summary>
    ///     Duration of one stream in seconds, selected as ffprobe selects streams ("v:0", "a:0").
    /// </summary>
    public static double StreamDuration(string path, string stream) =>
        double.Parse(
            Run("ffprobe",
            [
                "-v", "error", "-select_streams", stream,
                "-show_entries", "stream=duration", "-of", "default=nw=1:nk=1", path
            ]).Trim(),
            CultureInfo.InvariantCulture);

    /// <summary>
    ///     Number of streams of the given type ("v" or "a") in a file.
    /// </summary>
    public static int StreamCount(string path, string type) =>
        Run("ffprobe",
        [
            "-v", "error", "-select_streams", type,
            "-show_entries", "stream=index", "-of", "default=nw=1:nk=1", path
        ]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
}
