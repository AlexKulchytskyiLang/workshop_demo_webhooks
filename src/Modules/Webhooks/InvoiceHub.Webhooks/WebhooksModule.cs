using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Messaging;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Webhooks.Api;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Delivery;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;
using InvoiceHub.Webhooks.Sending;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks;

/// <summary>Endpoints, subscriptions, secrets, the delivery state machine, fan-out, delivery attempts and the sweeper.</summary>
public sealed class WebhooksModule : IWorkerModule
{
    public const string ModuleName = "webhooks";

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<WebhooksOptions>().Bind(builder.Configuration.GetSection(WebhooksOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSecretProtector(builder.Configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddModuleDbContext<WebhooksDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        services.AddScoped<EndpointSecrets>();
        services.AddScoped<HeaderStore>();
        services.AddScoped<RequestFactory>();

        // the SSRF guard is a singleton; the dev allow-list exists only in Development and is ignored everywhere else
        var isDevelopment = builder.Environment.IsDevelopment();
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton(sp => new SsrfGuard(sp.GetRequiredService<IDnsResolver>(), BuildPolicy(sp.GetRequiredService<IOptions<WebhooksOptions>>().Value, isDevelopment)));
        services.AddSingleton<IWebhookSender, WebhookSender>();
    }

    public void ConfigureWorkers(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddScoped<IEventMessageHandler, FanOutHandler>();
        services.AddScoped<IDeliveryMessageHandler, DeliveryHandler>();
        services.AddHostedService<RetrySweeper>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        EndpointsApi.Map(app);
        EndpointSettingsApi.Map(app);
        EndpointTestingApi.Map(app);
        OverviewApi.Map(app);
        DeliveriesApi.Map(app);
        ReplayApi.Map(app);
    }

    internal static SsrfPolicy BuildPolicy(WebhooksOptions options, bool isDevelopment)
    {
        if (!isDevelopment) return SsrfPolicy.Strict;
        var allowed = new List<EgressAllowance>();
        foreach (var entry in options.Egress.DevAllow)
        {
            var index = entry.LastIndexOf(':');
            if (index > 0 && int.TryParse(entry[(index + 1)..], out var port)) allowed.Add(new EgressAllowance(entry[..index], port));
        }
        return new SsrfPolicy(allowed);
    }
}
