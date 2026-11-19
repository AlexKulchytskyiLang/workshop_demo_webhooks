using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Tenancy.ApiKeys;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Directory;
using InvoiceHub.Tenancy.Http;
using InvoiceHub.Tenancy.Keycloak;
using InvoiceHub.Tenancy.Persistence;
using InvoiceHub.Tenancy.Registration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceHub.Tenancy;

/// <summary>Tenants, registration, the Keycloak facade and the signed-in user's view of the platform (/api/me).</summary>
public sealed class TenancyModule : IModule
{
    public const string ModuleName = "tenancy";

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddMemoryCache();
        services.AddModuleDbContext<TenancyDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<ITeamDirectory, TeamDirectory>();
        services.AddScoped<RegistrationService>();
        services.AddScoped<IDisabledUsers, DisabledUsers>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuth.Scheme, _ => { });

        // The Keycloak admin client must not retry unsafe methods: a retried POST could create the user twice.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is marked experimental by the library
        services.AddHttpClient(KeycloakAdminClient.ClientName)
            .RemoveAllResilienceHandlers()
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.DisableForUnsafeHttpMethods();
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });
#pragma warning restore EXTEXP0001
        services.AddSingleton<IKeycloakAdmin, KeycloakAdminClient>();
        services.AddSingleton(TimeProvider.System);
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        TenancyEndpoints.Map(app);
        ApiKeysApi.Map(app);
        TeamApi.Map(app);
    }
}
