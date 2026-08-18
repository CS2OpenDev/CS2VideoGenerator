namespace Cs2VideoGenerator.Cli.Tasks;

/// <summary>
///     Implemented by CLI option types that can register services/tasks into the host builder.
/// </summary>
public interface IRegisterTaskAndOptions
{
    void RegisterTaskAndOptions(IHostApplicationBuilder hostBuilder);
}
