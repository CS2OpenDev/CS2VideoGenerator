#region

using System.Buffers.Binary;
using System.IO.Pipes;
using Cs2VideoGenerator.Core.Capture;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The host side of the present-hook wire protocol, driven by a synthetic plugin: a pipe client
///     writing CFRS/CFRM/CFRE records exactly as present_capture.cpp does. The protocol constants
///     are pinned here because both ends compile them independently - a drift is the bug.
/// </summary>
public class PresentHookFrameConsumerTests
{
    private const int Width = 8;
    private const int Height = 4;
    private const int FrameBytes = Width * Height * 4;

    /// <summary>
    ///     How long the synthetic plugin waits for the consumer's server pipe to appear. See the
    ///     connect call in <see cref="RunAsync" /> for why this is generous rather than tight.
    /// </summary>
    private const int ConnectTimeoutMs = 30_000;

    private static int _pipeCounter;

    /// <summary>A unique pipe name per test, so parallel tests cannot cross streams.</summary>
    private static string NewPipeName() =>
        $"csvg-frames-test-{Environment.ProcessId}-{Interlocked.Increment(ref _pipeCounter)}";

    private static byte[] Record(uint magic, uint a, uint b, uint c = 0)
    {
        byte[] record = new byte[PresentHookFrameConsumer.RecordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(record, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), a);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), b);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), c);
        return record;
    }

    private static byte[] Header(uint width = Width, uint height = Height, uint format = 28) =>
        Record(PresentHookFrameConsumer.StreamHeaderMagic, width, height, format);

    private static byte[] Frame(uint sequence, byte fill)
    {
        byte[] payload = new byte[FrameBytes];
        Array.Fill(payload, fill);
        return [.. Record(PresentHookFrameConsumer.FrameMagic, sequence, FrameBytes), .. payload];
    }

    private static byte[] Trailer(uint frameCount) =>
        Record(PresentHookFrameConsumer.TrailerMagic, frameCount, 0);

    private static async Task<(FrameSequenceConsumeResult Result, byte[] SinkBytes)> RunAsync(
        Func<NamedPipeClientStream, Task> plugin,
        TimeSpan? drainTimeout = null)
    {
        string pipeName = NewPipeName();
        MemoryStream sink = new();
        await using PresentHookFrameConsumer consumer = new(
            pipeName, Width, Height, sink, drainTimeout ?? TimeSpan.FromSeconds(5));

        consumer.Start(TestContext.Current.CancellationToken);

        await using (NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out))
        {
            // Start() only queues the loop that creates the server pipe (Task.Run, fire and forget),
            // so the pipe does not exist yet when we get here and ConnectAsync polls until it does.
            // The budget is deliberately far longer than the pipe can plausibly take to appear: it
            // bounds nothing the test asserts, and a tight one just turns a loaded CI runner's
            // thread-pool scheduling delay into a spurious TimeoutException.
            await client.ConnectAsync(ConnectTimeoutMs, TestContext.Current.CancellationToken);
            await plugin(client);
        }

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        return (result, sink.ToArray());
    }

    [Fact]
    public async Task AWellFormedStream_LandsEveryPayloadByteInTheSink()
    {
        (FrameSequenceConsumeResult result, byte[] sinkBytes) = await RunAsync(async client =>
        {
            await client.WriteAsync(Header());
            await client.WriteAsync(Frame(0, 0x11));
            await client.WriteAsync(Frame(1, 0x22));
            await client.WriteAsync(Frame(2, 0x33));
            await client.WriteAsync(Trailer(3));
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.FramesConsumed);
        Assert.Equal(3L * FrameBytes, result.BytesConsumed);
        Assert.Equal(0, result.DroppedFrames);
        Assert.NotNull(result.FirstFrameConsumedUtc);

        // The sink carries payload only - records never leak into the encoder.
        Assert.Equal(3 * FrameBytes, sinkBytes.Length);
        Assert.All(sinkBytes.Take(FrameBytes), b => Assert.Equal(0x11, b));
        Assert.All(sinkBytes.Skip(2 * FrameBytes), b => Assert.Equal(0x33, b));
    }

    [Fact]
    public async Task AGeometryMismatch_FailsNamingBothResolutions()
    {
        (FrameSequenceConsumeResult result, _) = await RunAsync(async client =>
        {
            await client.WriteAsync(Header(1920, 1080));
        });

        Assert.False(result.Success);
        Assert.Contains("1920x1080", result.ErrorMessage);
        Assert.Contains($"{Width}x{Height}", result.ErrorMessage);
    }

    [Fact]
    public async Task ASequenceGap_FailsTheClip()
    {
        (FrameSequenceConsumeResult result, _) = await RunAsync(async client =>
        {
            await client.WriteAsync(Header());
            await client.WriteAsync(Frame(0, 0x11));
            await client.WriteAsync(Frame(2, 0x22));
        });

        Assert.False(result.Success);
        Assert.Contains("expected frame 1", result.ErrorMessage);
    }

    [Fact]
    public async Task ATrailerDisagreeingWithTheFrameCount_Fails()
    {
        (FrameSequenceConsumeResult result, _) = await RunAsync(async client =>
        {
            await client.WriteAsync(Header());
            await client.WriteAsync(Frame(0, 0x11));
            await client.WriteAsync(Trailer(5));
        });

        Assert.False(result.Success);
        Assert.Contains("declares 5 frames but 1 arrived", result.ErrorMessage);
    }

    [Fact]
    public async Task APipeClosedMidStream_FailsRatherThanTruncatingSilently()
    {
        (FrameSequenceConsumeResult result, _) = await RunAsync(async client =>
        {
            await client.WriteAsync(Header());
            await client.WriteAsync(Frame(0, 0x11));
            // no trailer - the client disposes and the pipe breaks
        });

        Assert.False(result.Success);
        Assert.Contains("closed it early", result.ErrorMessage);
        Assert.Equal(1, result.FramesConsumed);
    }

    [Fact]
    public async Task NoConnectionByTheDrainDeadline_FailsNamingTheLikelyCauses()
    {
        MemoryStream sink = new();
        await using PresentHookFrameConsumer consumer = new(
            NewPipeName(), Width, Height, sink, TimeSpan.FromMilliseconds(200));

        consumer.Start(TestContext.Current.CancellationToken);

        FrameSequenceConsumeResult result = await consumer.StopAsync().WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("never connected", result.ErrorMessage);
        Assert.Contains("present-capture", result.ErrorMessage);
    }

    [Fact]
    public async Task AFrameDeclaringTheWrongPayloadSize_Fails()
    {
        (FrameSequenceConsumeResult result, _) = await RunAsync(async client =>
        {
            await client.WriteAsync(Header());
            await client.WriteAsync(Record(PresentHookFrameConsumer.FrameMagic, 0, FrameBytes + 4));
        });

        Assert.False(result.Success);
        Assert.Contains("changed shape", result.ErrorMessage);
    }

    /// <summary>
    ///     The sink is closed whichever way the stream ends - it is what tells ffmpeg the stream is
    ///     over, and a failure path that left it open would hang the encoder wait instead.
    /// </summary>
    [Fact]
    public async Task TheSink_IsClosedOnBothOutcomes()
    {
        MemoryStream happySink = new();
        string pipeName = NewPipeName();
        await using (PresentHookFrameConsumer consumer = new(
                         pipeName, Width, Height, happySink, TimeSpan.FromSeconds(5)))
        {
            consumer.Start(TestContext.Current.CancellationToken);
            await using (NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out))
            {
                await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
                await client.WriteAsync(Header());
                await client.WriteAsync(Trailer(0));
            }

            await consumer.StopAsync();
        }

        Assert.False(happySink.CanWrite);

        MemoryStream failSink = new();
        await using (PresentHookFrameConsumer consumer = new(
                         NewPipeName(), Width, Height, failSink, TimeSpan.FromMilliseconds(100)))
        {
            consumer.Start(TestContext.Current.CancellationToken);
            await consumer.StopAsync();
        }

        Assert.False(failSink.CanWrite);
    }
}
