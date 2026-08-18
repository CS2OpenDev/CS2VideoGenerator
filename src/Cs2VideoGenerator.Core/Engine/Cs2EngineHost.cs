#region

using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.ProcessManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     Default <see cref="ICs2EngineHost" /> implementation: constructs
///     <see cref="Cs2EngineSession" /> instances and tracks the most recently created one
///     (last writer wins). The host is the DI-facing owner of the singleton dependencies every
///     session needs - the CS2 process manager and the optional gRPC endpoint probe - so each
///     session can bring its own CS2 process up (<see cref="Cs2EngineSession.ConnectAsync" />)
///     and down (<see cref="Cs2EngineSession.StopAsync" />) without reaching outside the engine
///     layer.
/// </summary>
/// <param name="logger">Logger passed to the sessions this host creates.</param>
/// <param name="options">Options passed to the sessions this host creates.</param>
/// <param name="processManager">Process manager the sessions launch and stop CS2 through.</param>
/// <param name="endpointProbe">
///     Optional loopback probe used to verify the host's gRPC server is listening before CS2 is
///     launched; when null, sessions skip the endpoint verification.
/// </param>
public sealed class Cs2EngineHost(
    ILogger logger,
    IOptions<Cs2VideoGeneratorOptions> options,
    ICs2ProcessManager processManager,
    IGrpcEndpointProbe? endpointProbe = null) : ICs2EngineHost
{
    private readonly Lock _gate = new();
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IOptions<Cs2VideoGeneratorOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly ICs2ProcessManager _processManager =
        processManager ?? throw new ArgumentNullException(nameof(processManager));

    private readonly IGrpcEndpointProbe? _endpointProbe = endpointProbe;

    private ICs2EngineSession? _current;

    /// <inheritdoc />
    public ICs2EngineSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public ICs2EngineSession CreateSession(EngineSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Cs2EngineSession session = new(_logger, _options, _processManager, _endpointProbe, options);
        lock (_gate)
        {
            _current = session;
        }

        return session;
    }
}
