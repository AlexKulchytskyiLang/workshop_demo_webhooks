using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Api;

/// <summary>Baseline response headers for an API: no sniffing, no framing, no referrer, and nothing cached unless an endpoint says so.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";

            if (!h.ContainsKey("Cache-Control") && context.Request.Path.StartsWithSegments("/api"))
                h.CacheControl = "no-store";

            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>Ready only when the API login can reach the database.</summary>
public sealed class SqlHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(Db.ConnectionString(configuration, DbRole.Api, "InvoiceHub.Api.health"));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("SQL Server is not reachable with the API login.", e);
        }
    }
}

/// <summary>Ready only when the realm's discovery document answers (otherwise no token can be validated).</summary>
public sealed class KeycloakHealthCheck(IOptions<KeycloakOptions> options, IHttpClientFactory factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await factory.CreateClient("health").GetAsync($"{options.Value.Authority.TrimEnd('/')}/.well-known/openid-configuration", cts.Token);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"Keycloak answered {(int)response.StatusCode}.");
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("Keycloak is not reachable.", e);
        }
    }
}
