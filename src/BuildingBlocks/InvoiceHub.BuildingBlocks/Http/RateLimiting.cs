using System.Threading.RateLimiting;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.BuildingBlocks.Http;

public static class RateLimiting
{
    public const string Public = "public";
    public const string Registration = "registration";

    /// <summary>Test sends from the portal (they call other people's servers, so they are limited like an outbound action should be).</summary>
    public const string Tests = "tests";

    /// <summary>Per-client-address limits for the anonymous endpoints. Authenticated, per-tenant quotas arrive with the webhook API.</summary>
    public static IServiceCollection AddInvoiceHubRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Public, ctx => PerClient(ctx, 120));
            options.AddPolicy(Registration, ctx => PerClient(ctx, 5));
            options.AddPolicy(Tests, ctx => PerClient(ctx, 90));
            options.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await Problems.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests",
                    "Slow down and try again shortly.");
            };
        });
        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext ctx, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
