using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace InvoiceHub.BuildingBlocks.Modules;

/// <summary>
/// One module of the modular monolith. A module owns a SQL schema, exposes only its <c>*.Contracts</c> assembly to others
/// and is composed into a host (Api, Delivery, Simulator) by the host's composition root.
/// </summary>
public interface IModule
{
    string Name { get; }

    /// <summary>Registers the module's services (DbContext, handlers, background services this host should run).</summary>
    void ConfigureServices(IHostApplicationBuilder builder);

    /// <summary>Maps the module's HTTP endpoints. Hosts without HTTP surface (workers) never call this.</summary>
    void MapEndpoints(IEndpointRouteBuilder app);
}
