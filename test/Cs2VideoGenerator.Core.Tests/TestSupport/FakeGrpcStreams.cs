#region

using System.Threading.Channels;
using Cs2VideoGenerator.Core.Proto;
using Grpc.Core;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Shared test double: in-memory <see cref="IAsyncStreamReader{T}" /> backed by a
///     channel; completing the channel simulates the client (plugin) disconnecting.
/// </summary>
internal sealed class FakeStreamReader : IAsyncStreamReader<CsProcessEvent>
{
    private readonly Channel<CsProcessEvent> _channel = Channel.CreateUnbounded<CsProcessEvent>();

    public CsProcessEvent Current { get; private set; } = null!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_channel.Reader.TryRead(out CsProcessEvent? item))
            {
                Current = item;
                return true;
            }
        }

        return false;
    }

    public void Write(CsProcessEvent processEvent) => _channel.Writer.TryWrite(processEvent);

    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>
///     Shared test double: in-memory <see cref="IServerStreamWriter{T}" /> that records
///     written commands and can be configured to fail.
/// </summary>
internal sealed class FakeStreamWriter : IServerStreamWriter<CsControlRequest>
{
    private readonly List<CsControlRequest> _written = [];

    public Exception? ThrowOnWrite { get; set; }

    public IReadOnlyList<CsControlRequest> Written
    {
        get
        {
            lock (_written)
            {
                return _written.ToList();
            }
        }
    }

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(CsControlRequest message)
    {
        if (ThrowOnWrite != null)
        {
            return Task.FromException(ThrowOnWrite);
        }

        lock (_written)
        {
            _written.Add(message);
        }

        return Task.CompletedTask;
    }
}
