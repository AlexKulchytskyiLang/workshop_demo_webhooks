using Microsoft.Extensions.Hosting;

namespace InvoiceHub.BuildingBlocks.Modules;

/// <summary>
/// A module that also runs background work (hosted services). Only worker hosts call <see cref="ConfigureWorkers"/>;
/// the API host never does, so API replicas do not compete with workers.
/// </summary>
public interface IWorkerModule : IModule
{
    void ConfigureWorkers(IHostApplicationBuilder builder);
}
