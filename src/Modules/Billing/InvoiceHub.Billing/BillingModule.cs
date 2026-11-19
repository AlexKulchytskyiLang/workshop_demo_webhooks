using InvoiceHub.Billing.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceHub.Billing;

/// <summary>The simulated invoicing domain. It exists to produce realistic business events through the outbox.</summary>
public sealed class BillingModule : IModule
{
    public const string ModuleName = "billing";

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<BillingDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IInvoiceCommands, InvoiceCommands>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // No HTTP surface: the simulator host drives the commands in process.
    }
}
