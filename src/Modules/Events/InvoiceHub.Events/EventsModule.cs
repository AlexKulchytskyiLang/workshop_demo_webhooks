using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Events.Catalog;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Events.Persistence;
using InvoiceHub.Events.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceHub.Events;

/// <summary>The canonical event store, the outbox, the catalogue and the relay that moves committed events to the broker.</summary>
public sealed class EventsModule : IWorkerModule
{
    public const string ModuleName = "events";

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<EventsDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        services.AddSingleton<IEventCatalog>(_ => EventCatalog.LoadEmbedded());
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<IEventReader, EventReader>();
        services.AddScoped<IFanOutTracker, FanOutTracker>();
    }

    public void ConfigureWorkers(IHostApplicationBuilder builder)
    {
        builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.Section));
        builder.Services.AddSingleton<OutboxRelay>();
        builder.Services.AddHostedService<OutboxRelayService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portal/events/catalog", (IEventCatalog catalog) =>
        Results.Ok(new
        {
            categories = catalog.Categories.Select(c => new
            {
                c.Name,
                c.Pii,
                events = c.Events.Select(e => new { e.Type, e.Description, e.Payload, e.ContainsPii, e.ReferenceKeys }),
            }),
            bundles = catalog.Bundles,
            templates = catalog.Templates,
        })).RequireAuthorization(Permissions.EndpointsRead)
           .WithTags("Events");

        PullApi.Map(app);
    }
}
