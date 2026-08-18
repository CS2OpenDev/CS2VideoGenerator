#region

using System.Net;
using System.Net.Sockets;

#endregion

namespace Cs2VideoGenerator.Core.Grpc;

/// <summary>
///     Probes whether the orchestrator's gRPC endpoint (the server the CS2 plugin and the
///     mock server connect back to) is actually listening on the configured port.
///     Used by <see cref="CsvgVideoSession.StartAsync(CancellationToken)" /> to fail fast with an
///     actionable error instead of a long connect timeout when the host never mapped
///     <see cref="Cs2GameService" />.
/// </summary>
public interface IGrpcEndpointProbe
{
    /// <summary>
    ///     Returns <see langword="true" /> when something is accepting TCP connections on the
    ///     loopback interface at <paramref name="port" />.
    /// </summary>
    /// <param name="port">The TCP port to probe on the loopback interface.</param>
    /// <param name="cancellationToken">Cancellation token to abort the probe.</param>
    Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="IGrpcEndpointProbe" />: attempts a plain TCP connect to both the IPv4
///     (127.0.0.1) and IPv6 (::1) loopback addresses so a server bound to either family is
///     detected. Retries briefly (total budget about one second) so a gRPC server that is still
///     binding during host startup is not reported as missing.
/// </summary>
public sealed class TcpGrpcEndpointProbe : IGrpcEndpointProbe
{
    /// <summary>Total time budget for the probe, including retries.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public async Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken = default)
    {
        long deadline = Environment.TickCount64 + (long)ProbeBudget.TotalMilliseconds;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Probe both loopback families concurrently: Kestrel's ListenLocalhost binds both,
            // but a consumer's server may bind only 127.0.0.1 or only [::1].
            bool[] results = await Task.WhenAll(
                TryConnectAsync(IPAddress.Loopback, port, cancellationToken),
                TryConnectAsync(IPAddress.IPv6Loopback, port, cancellationToken)).ConfigureAwait(false);

            if (results[0] || results[1])
            {
                return true;
            }

            if (Environment.TickCount64 + (long)RetryDelay.TotalMilliseconds >= deadline)
            {
                return false;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Attempts a single TCP connect to <paramref name="address" />:<paramref name="port" />.
    ///     Returns <see langword="false" /> on refusal, unsupported address family, or attempt timeout;
    ///     rethrows only caller-requested cancellation.
    /// </summary>
    private static async Task<bool> TryConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(AttemptTimeout);
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
