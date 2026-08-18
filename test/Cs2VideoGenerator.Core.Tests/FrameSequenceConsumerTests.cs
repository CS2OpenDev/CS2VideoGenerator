#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Capture;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Tests for <see cref="FrameSequenceConsumer" /> over temp directories of synthetic
///     frames. The naming facts these exercise - digit width, numbering base - are unverified
///     against a real CS2 <c>startmovie</c>, so the point of several of these cases is that
///     the consumer never depends on a particular answer: the same class handles 0-based and 1-based
///     sequences of any width because those are values it is given, not assumptions it makes.
/// </summary>
public class FrameSequenceConsumerTests : IDisposable
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TempDir _tempDir = new("csvg-frame-consumer-tests-");

    public void Dispose()
    {
        _tempDir.Dispose();
        GC.SuppressFinalize(this);
    }

    private static FrameSequencePattern Pattern(int digits = 4, int firstFrame = 1, string extension = "tga") =>
        new() { Prefix = "frame", DigitCount = digits, Extension = extension, FirstFrameNumber = firstFrame };

    private string NewStagingDirectory(string name)
    {
        string path = _tempDir.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    ///     Writes a frame whose content identifies it: <paramref name="number" /> repeated
    ///     <paramref name="length" /> times, so the piped bytes can be checked for order.
    /// </summary>
    private static void WriteFrame(string directory, FrameSequencePattern pattern, int number, int length = 16)
    {
        byte[] content = new byte[length];
        Array.Fill(content, (byte)number);
        File.WriteAllBytes(Path.Combine(directory, pattern.FileName(number)), content);
    }

    private static byte[] ExpectedBytes(IEnumerable<int> frameNumbers, int length = 16)
    {
        List<byte> expected = [];
        foreach (int number in frameNumbers)
        {
            expected.AddRange(Enumerable.Repeat((byte)number, length));
        }

        return [.. expected];
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < Patience)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {because}");
    }

    #region Ordering, completeness and unlinking

    [Fact]
    public async Task Consumes_FramesInSequenceOrder()
    {
        string staging = NewStagingDirectory("ordered");
        FrameSequencePattern pattern = Pattern();

        // Written newest-first so the consumer cannot pass by accident of file-system ordering.
        foreach (int number in (int[]) [5, 3, 1, 4, 2])
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5, result.FramesConsumed);
        Assert.Equal(ExpectedBytes([1, 2, 3, 4, 5]), sink.ToArray());
    }

    [Fact]
    public async Task DoesNotConsume_AFrameWithNoSuccessor()
    {
        string staging = NewStagingDirectory("lone-frame");
        FrameSequencePattern pattern = Pattern();
        WriteFrame(staging, pattern, 1);

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        // Several poll intervals: the engine may still be writing this frame, and nothing proves
        // otherwise until a higher-numbered one appears.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.Equal(0, consumer.FramesConsumed);
        Assert.Equal(1, consumer.Backlog);
        Assert.True(File.Exists(Path.Combine(staging, pattern.FileName(1))));
        Assert.Empty(sink.ToArray());
    }

    [Fact]
    public async Task Unlinks_EachFrameOnceItsSuccessorProvesItComplete()
    {
        string staging = NewStagingDirectory("unlink");
        FrameSequencePattern pattern = Pattern();
        for (int number = 1; number <= 5; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        await WaitForAsync(() => consumer.FramesConsumed == 4, "the first four frames to be consumed");

        // Frames 1-4 are gone; frame 5 stays until something proves it finished.
        for (int number = 1; number <= 4; number++)
        {
            Assert.False(File.Exists(Path.Combine(staging, pattern.FileName(number))),
                $"frame {number} should have been unlinked after being piped");
        }

        Assert.True(File.Exists(Path.Combine(staging, pattern.FileName(5))),
            "the newest frame has no successor and must not be unlinked");
    }

    [Fact]
    public async Task Consumes_FramesThatArriveWhileRunning()
    {
        string staging = NewStagingDirectory("growing");
        FrameSequencePattern pattern = Pattern();

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        for (int number = 1; number <= 6; number++)
        {
            WriteFrame(staging, pattern, number);
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(6, result.FramesConsumed);
        Assert.Equal(6 * 16, result.BytesConsumed);
        Assert.Equal(ExpectedBytes([1, 2, 3, 4, 5, 6]), sink.ToArray());
    }

    [Fact]
    public async Task Ignores_FilesThatAreNotThisSequence()
    {
        string staging = NewStagingDirectory("mixed");
        FrameSequencePattern pattern = Pattern();
        WriteFrame(staging, pattern, 1);
        WriteFrame(staging, pattern, 2);

        await File.WriteAllTextAsync(Path.Combine(staging, "notes.txt"), "not a frame",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "frame01.tga"), "wrong digit width",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "frame0003.png"), "wrong extension",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staging, "other0004.tga"), "wrong prefix",
            TestContext.Current.CancellationToken);

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FramesConsumed);
        Assert.Equal(ExpectedBytes([1, 2]), sink.ToArray());
        Assert.True(File.Exists(Path.Combine(staging, "notes.txt")), "unrelated files must be left alone");
        Assert.True(File.Exists(Path.Combine(staging, "frame01.tga")));
        Assert.True(File.Exists(Path.Combine(staging, "frame0003.png")));
        Assert.True(File.Exists(Path.Combine(staging, "other0004.tga")));
    }

    #endregion

    #region Tail drain

    [Fact]
    public async Task Drain_PipesTheFinalFrameAndClosesTheSink()
    {
        string staging = NewStagingDirectory("drain");
        FrameSequencePattern pattern = Pattern();
        for (int number = 1; number <= 3; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.FramesConsumed);
        Assert.Equal(0, consumer.Backlog);
        Assert.Empty(Directory.GetFiles(staging));
        Assert.Equal(ExpectedBytes([1, 2, 3]), sink.ToArray());

        // Closing the sink is what tells an encoder there are no more frames.
        Assert.False(sink.CanWrite, "the sink should be closed once the sequence ends");
    }

    [Fact]
    public async Task Drain_KeepsGoingForFramesThatLandAfterTheStopSignal()
    {
        string staging = NewStagingDirectory("late-frame");
        FrameSequencePattern pattern = Pattern();
        WriteFrame(staging, pattern, 1);
        WriteFrame(staging, pattern, 2);

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        await WaitForAsync(() => consumer.FramesConsumed == 1, "the first frame to be consumed");

        Task<FrameSequenceConsumeResult> completion = consumer.StopAsync();

        // The drain waits for the last frame to settle before trusting it; a frame written during
        // that wait means the sequence was not over, and must not be lost.
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        WriteFrame(staging, pattern, 3);

        FrameSequenceConsumeResult result = await completion.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.FramesConsumed);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(ExpectedBytes([1, 2, 3]), sink.ToArray());
    }

    [Fact]
    public async Task Drain_EmptyStagingDirectory_SucceedsWithNothingConsumed()
    {
        string staging = NewStagingDirectory("empty");
        MemoryStream sink = new();

        await using FrameSequenceConsumer consumer = new(staging, Pattern(), sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.FramesConsumed);
        Assert.Equal(0, result.BytesConsumed);
        Assert.Empty(sink.ToArray());
    }

    [Fact]
    public async Task Drain_StagingDirectoryThatNeverAppears_SucceedsWithNothingConsumed()
    {
        // The engine creates the directory when it starts writing; a clip that produced nothing
        // must not turn into an error here.
        string staging = _tempDir.Combine("never-created");
        MemoryStream sink = new();

        await using FrameSequenceConsumer consumer = new(staging, Pattern(), sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.FramesConsumed);
    }

    #endregion

    #region Gaps and unwritten frames

    [Fact]
    public async Task Gap_IsCountedAsDroppedAndConsumptionContinues()
    {
        string staging = NewStagingDirectory("gap");
        FrameSequencePattern pattern = Pattern();
        foreach (int number in (int[]) [1, 2, 4, 5])
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.FramesConsumed);
        Assert.Equal(1, result.DroppedFrames);
        Assert.Equal(ExpectedBytes([1, 2, 4, 5]), sink.ToArray());
    }

    [Fact]
    public async Task Gap_OfSeveralFrames_IsCountedInFull()
    {
        string staging = NewStagingDirectory("wide-gap");
        FrameSequencePattern pattern = Pattern();
        foreach (int number in (int[]) [1, 7, 8])
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.FramesConsumed);
        Assert.Equal(5, result.DroppedFrames);
    }

    [Fact]
    public async Task EmptyFrame_IsDroppedRatherThanPiped()
    {
        string staging = NewStagingDirectory("empty-frame");
        FrameSequencePattern pattern = Pattern();

        // A frame with a successor is finished by definition, so a zero-byte one was never written.
        File.WriteAllBytes(Path.Combine(staging, pattern.FileName(1)), []);
        WriteFrame(staging, pattern, 2);
        WriteFrame(staging, pattern, 3);

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FramesConsumed);
        Assert.Equal(1, result.DroppedFrames);
        Assert.Equal(ExpectedBytes([2, 3]), sink.ToArray());
        Assert.Empty(Directory.GetFiles(staging));
    }

    #endregion

    #region Timestamped subdirectories (the layout CS2 was observed writing)

    /// <summary>
    ///     The real sequence of events: the consumer starts polling before <c>startmovie</c> fires,
    ///     and only then does the engine create <c>movie/&lt;timestamp&gt;/</c> and write into it.
    ///     The subdirectory must therefore be discovered mid-run, not resolved at construction.
    /// </summary>
    [Fact]
    public async Task FramesInATimestampedSubdirectoryCreatedAfterStart_AreDiscoveredAndConsumed()
    {
        string staging = NewStagingDirectory("ts-discovery");
        FrameSequencePattern pattern = Pattern();

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll,
            scanTimestampedSubdirectories: true);
        consumer.Start(TestContext.Current.CancellationToken);

        // The engine's directory appears only once recording starts.
        string movieDirectory = Path.Combine(staging, "2026_07_30_15_44_29");
        Directory.CreateDirectory(movieDirectory);
        for (int i = 1; i <= 4; i++)
        {
            WriteFrame(movieDirectory, pattern, i);
        }

        FrameSequenceConsumeResult result =
            await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.FramesConsumed);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(ExpectedBytes([1, 2, 3, 4]), sink.ToArray());
        Assert.Empty(Directory.GetFiles(movieDirectory));
    }

    /// <summary>
    ///     Only directories with exactly the engine's timestamp shape are scanned: a hand-made folder
    ///     that happens to contain matching file names must not leak frames into the sequence.
    /// </summary>
    [Fact]
    public async Task DirectoriesWithoutTheTimestampShape_AreNotScanned()
    {
        string staging = NewStagingDirectory("ts-shape");
        FrameSequencePattern pattern = Pattern();

        string wrongShape = Path.Combine(staging, "not_a_timestamp");
        Directory.CreateDirectory(wrongShape);
        WriteFrame(wrongShape, pattern, 1);

        string rightShape = Path.Combine(staging, "2026_07_30_15_44_29");
        Directory.CreateDirectory(rightShape);
        WriteFrame(rightShape, pattern, 1);
        WriteFrame(rightShape, pattern, 2);

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll,
            scanTimestampedSubdirectories: true);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result =
            await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FramesConsumed);
        Assert.Equal(ExpectedBytes([1, 2]), sink.ToArray());
        Assert.True(File.Exists(Path.Combine(wrongShape, pattern.FileName(1))),
            "the non-timestamp directory's file must be left alone");
    }

    /// <summary>
    ///     The flat layout keeps working with scanning enabled - the staging root is always scanned -
    ///     so a configuration correction for an engine that stops using subdirectories is just the
    ///     toggle, not a different consumer.
    /// </summary>
    [Fact]
    public async Task FlatFrames_AreStillConsumedWhenSubdirectoryScanningIsEnabled()
    {
        string staging = NewStagingDirectory("ts-flat");
        FrameSequencePattern pattern = Pattern();
        WriteFrame(staging, pattern, 1);
        WriteFrame(staging, pattern, 2);

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll,
            scanTimestampedSubdirectories: true);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result =
            await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FramesConsumed);
        Assert.Equal(ExpectedBytes([1, 2]), sink.ToArray());
    }

    #endregion

    #region Naming that has not been verified against the engine

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ToleratesEitherNumberingBase(int firstFrameNumber)
    {
        string staging = NewStagingDirectory($"base-{firstFrameNumber}");
        FrameSequencePattern pattern = Pattern(firstFrame: firstFrameNumber);
        for (int i = 0; i < 5; i++)
        {
            WriteFrame(staging, pattern, firstFrameNumber + i);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5, result.FramesConsumed);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(ExpectedBytes(Enumerable.Range(firstFrameNumber, 5)), sink.ToArray());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task ConfiguredBaseDisagreeingWithTheEngine_IsAdoptedNotReportedAsDrops(
        int configuredFirst,
        int actualFirst)
    {
        // Which base startmovie uses has not been observed yet. Whichever way the guess goes, the
        // sequence the engine actually wrote wins, and no frame is reported missing for it.
        string staging = NewStagingDirectory($"base-mismatch-{configuredFirst}");
        FrameSequencePattern pattern = Pattern(firstFrame: configuredFirst);
        for (int i = 0; i < 4; i++)
        {
            WriteFrame(staging, pattern, actualFirst + i);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.FramesConsumed);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(ExpectedBytes(Enumerable.Range(actualFirst, 4)), sink.ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ToleratesAnyDigitWidth(int digitCount)
    {
        string staging = NewStagingDirectory($"digits-{digitCount}");
        FrameSequencePattern pattern = Pattern(digitCount);
        for (int number = 1; number <= 4; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.FramesConsumed);
        Assert.Equal(ExpectedBytes([1, 2, 3, 4]), sink.ToArray());
    }

    [Theory]
    [InlineData("tga")]
    [InlineData("jpg")]
    [InlineData("png")]
    public async Task ToleratesAnyImageExtension(string extension)
    {
        string staging = NewStagingDirectory($"ext-{extension}");
        FrameSequencePattern pattern = Pattern(extension: extension);
        for (int number = 1; number <= 3; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.FramesConsumed);
    }

    #endregion

    #region Backpressure, cancellation and failure

    [Fact]
    public async Task Backlog_ReportsFramesWaitingWhileTheSinkIsBusy()
    {
        string staging = NewStagingDirectory("backlog");
        FrameSequencePattern pattern = Pattern();
        for (int number = 1; number <= 5; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        BlockingStream sink = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        await WaitForAsync(() => consumer.Backlog == 5, "the backlog to show every staged frame");
        Assert.Equal(0, consumer.FramesConsumed);

        // That pile on disk is the buffer: nothing is lost while the encoder catches up.
        sink.Release();

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5, result.FramesConsumed);
        Assert.Equal(0, consumer.Backlog);
    }

    [Fact]
    public async Task Cancellation_StopsPromptlyAndLeavesNoOpenHandles()
    {
        string staging = NewStagingDirectory("cancel");
        FrameSequencePattern pattern = Pattern();
        for (int number = 1; number <= 5; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        BlockingStream sink = new();
        using CancellationTokenSource cts = new();
        await using FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(cts.Token);

        await WaitForAsync(() => consumer.Backlog == 5, "the consumer to reach the blocked sink");

        Stopwatch sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        FrameSequenceConsumeResult result = await consumer.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation should be prompt (took {sw.Elapsed})");

        // Nothing is left holding a frame open: an exclusive open of each remaining file succeeds,
        // and the whole staging directory can be removed.
        foreach (string remaining in Directory.GetFiles(staging))
        {
            using FileStream _ = new(remaining, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        Directory.Delete(staging, true);
    }

    [Fact]
    public async Task SinkFailure_IsReportedAsAFailedResult()
    {
        string staging = NewStagingDirectory("sink-failure");
        FrameSequencePattern pattern = Pattern();
        for (int number = 1; number <= 3; number++)
        {
            WriteFrame(staging, pattern, number);
        }

        await using FrameSequenceConsumer consumer = new(staging, pattern, new ThrowingStream(), FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("encoder went away", result.ErrorMessage);
        Assert.Equal(0, result.FramesConsumed);

        // The frame was not consumed, so it was not unlinked either.
        Assert.True(File.Exists(Path.Combine(staging, pattern.FileName(1))));
    }

    [Fact]
    public async Task Dispose_BeforeStart_ClosesTheSink()
    {
        MemoryStream sink = new();
        FrameSequenceConsumer consumer = new(NewStagingDirectory("never-started"), Pattern(), sink, FastPoll);

        await consumer.DisposeAsync();

        Assert.False(sink.CanWrite);
    }

    [Fact]
    public async Task Dispose_WhileRunning_StopsTheLoop()
    {
        string staging = NewStagingDirectory("dispose-running");
        FrameSequencePattern pattern = Pattern();
        WriteFrame(staging, pattern, 1);
        WriteFrame(staging, pattern, 2);

        MemoryStream sink = new();
        FrameSequenceConsumer consumer = new(staging, pattern, sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        await consumer.DisposeAsync();

        Assert.True(consumer.Completion.IsCompleted);
        Assert.False((await consumer.Completion).Success);
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        MemoryStream sink = new();
        await using FrameSequenceConsumer consumer = new(NewStagingDirectory("double-start"), Pattern(), sink, FastPoll);
        consumer.Start(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => consumer.Start(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_RejectsAPatternWithNoDigits()
    {
        MemoryStream sink = new();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FrameSequenceConsumer(_tempDir.Path, Pattern(0), sink, FastPoll));
    }

    [Fact]
    public void Constructor_RejectsAnUnwritableSink()
    {
        using MemoryStream readOnly = new([1, 2, 3], writable: false);

        Assert.Throws<ArgumentException>(() =>
            new FrameSequenceConsumer(_tempDir.Path, Pattern(), readOnly, FastPoll));
    }

    #endregion

    #region Pattern

    [Fact]
    public void Pattern_FormatsZeroPaddedNames()
    {
        Assert.Equal("frame0001.tga", Pattern().FileName(1));
        Assert.Equal("frame1234.tga", Pattern().FileName(1234));
        Assert.Equal("frame000042.jpg", Pattern(6, extension: "jpg").FileName(42));
        Assert.Equal("frame000.tga", Pattern(3, 0).FileName(0));
    }

    [Fact]
    public void Pattern_AcceptsALeadingDotOnTheExtension() =>
        Assert.Equal("frame0007.png", (Pattern() with { Extension = ".png" }).FileName(7));

    [Fact]
    public void Pattern_SearchPatternMatchesTheDigitWidthExactly()
    {
        Assert.Equal("frame????.tga", Pattern().SearchPattern);
        Assert.Equal("frame??????.tga", Pattern(6).SearchPattern);
    }

    [Theory]
    [InlineData("frame0001.tga", true, 1)]
    [InlineData("frame0000.tga", true, 0)]
    [InlineData("frame9999.tga", true, 9999)]
    [InlineData("FRAME0042.TGA", true, 42)]
    [InlineData("frame001.tga", false, 0)]
    [InlineData("frame00001.tga", false, 0)]
    [InlineData("frame0001.png", false, 0)]
    [InlineData("other0001.tga", false, 0)]
    [InlineData("frameabcd.tga", false, 0)]
    [InlineData("frame 001.tga", false, 0)]
    [InlineData("frame+001.tga", false, 0)]
    public void Pattern_ReadsOnlyItsOwnFrameNumbers(string fileName, bool expectedMatch, int expectedNumber)
    {
        bool matched = Pattern().TryGetFrameNumber(fileName, out int number);

        Assert.Equal(expectedMatch, matched);
        Assert.Equal(expectedNumber, number);
    }

    #endregion

    #region Sink doubles

    /// <summary>
    ///     A sink that blocks every write until released, standing in for an encoder that has fallen
    ///     behind the render.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _released.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public override void Write(byte[] buffer, int offset, int count) =>
            _released.Task.GetAwaiter().GetResult();

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    ///     A sink that fails every write, standing in for an encoder that has died mid-clip.
    /// </summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new IOException("the encoder went away");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("the encoder went away");

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    #endregion
}
