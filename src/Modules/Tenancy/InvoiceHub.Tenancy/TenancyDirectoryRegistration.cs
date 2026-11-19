using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Directory;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.Tenancy;

public static class TenancyDirectoryRegistration
{
    /// <summary>
    /// Just the read-only directories (tenants and team), for hosts that need to know who to notify but do not serve the portal API
    /// (the Delivery host): no Keycloak client, no endpoints.
    /// </summary>
    public static IServiceCollection AddTenancyDirectories(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        services.AddMemoryCache();
        services.AddModuleDbContext<TenancyDbContext>(configuration, applicationName);
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<ITeamDirectory, TeamDirectory>();
        return services;
    }
}
