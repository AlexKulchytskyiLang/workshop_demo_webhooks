using System.Security.Claims;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Tenancy.ApiKeys;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvoiceHub.Tenancy.Http;

internal sealed record CreateApiKeyInput(string? Name, string[]? Scopes, int? ExpiresInDays);

/// <summary>
/// API keys and the platform's static outbound addresses (Settings, "API keys &amp; IPs"). A key is shown exactly once, at creation, after a fresh sign-in;
/// afterwards only its name, scopes and last use are visible, and only its hash is stored.
/// </summary>
internal static class ApiKeysApi
{
    private static readonly int[] ExpiryChoices = [30, 90, 365];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/api-keys")
                   .WithTags("API keys");

        group.MapGet("/", List).RequireAuthorization(Permissions.ApiKeysManage);
        group.MapPost("/", Create).RequireAuthorization(Permissions.ApiKeysManage);
        group.MapDelete("/{id:guid}", Revoke).RequireAuthorization(Permissions.ApiKeysManage);

        app.MapGet("/api/portal/outbound-ips", OutboundIps)
           .RequireAuthorization(Permissions.EndpointsRead)
           .WithTags("API keys");
    }

    private static async Task<IResult> List(TenancyDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var keys = await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return Results.Ok(new
        {
            items = keys.Select(k => new
            {
                k.Id, k.Name, k.Prefix, scopes = k.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries), k.CreatedBy,
                createdAt = Utc(k.CreatedAt), expiresAt = Utc(k.ExpiresAt), lastUsedAt = Utc(k.LastUsedAt), revokedAt = Utc(k.RevokedAt),
                status = k.RevokedAt is not null ? "revoked" : k.ExpiresAt is { } e && e <= now ? "expired" : "active",
            }),
            scopes = ApiScopes.All.Select(s => new { scope = s, description = ApiScopes.Describe(s) }),
            expiryChoices = ExpiryChoices,
        });
    }

    private static async Task<IResult> Create(CreateApiKeyInput input, ClaimsPrincipal user, ITenantContext tenant, TenancyDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!user.IsFresh(now))
            return Problems.Create(StatusCodes.Status403Forbidden, "step_up_required", "Sign in again to continue", "Creating a key needs a fresh sign-in (within the last 5 minutes).");

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80)
            return Problems.Validation("invalid_name", "Give the key a name of 1 to 80 characters (what it is used for).");

        var scopes = (input.Scopes ?? []).Distinct().ToArray();
        if (scopes.Length == 0)
            return Problems.Validation("no_scopes", "Choose at least one permission for the key.");

        if (scopes.Any(s => !ApiScopes.All.Contains(s)))
            return Problems.Validation("unknown_scope", $"Known permissions: {string.Join(", ", ApiScopes.All)}.");

        if (input.ExpiresInDays is { } days && !ExpiryChoices.Contains(days))
            return Problems.Validation("invalid_expiry", $"A key lasts {string.Join(", ", ExpiryChoices)} days, or does not expire.");

        if (await db.ApiKeys.CountAsync(k => k.RevokedAt == null, ct) >= 20)
            return Problems.Conflict("key_limit", "At most 20 active keys. Revoke one you no longer use.");

        var tenantId = tenant.TenantId!.Value;
        var id = Guid.CreateVersion7();
        var (key, prefix, hash) = ApiKeyFormat.Create(tenantId, id);
        var entity = new ApiKeyEntity
        {
            Id = id, TenantId = tenantId, Name = name, Prefix = prefix, KeyHash = hash, Scopes = string.Join(',', scopes),
            CreatedBy = user.FindFirstValue("preferred_username") ?? "unknown", CreatedAt = now.UtcDateTime,
            ExpiresAt = input.ExpiresInDays is { } d ? now.UtcDateTime.AddDays(d) : null,
        };
        db.ApiKeys.Add(entity);
        await TenancyAudit.SaveAsync(db, audit, new AuditEntry("apikey.created", "api_key", id.ToString(), name, new { scopes, prefix, expiresAt = entity.ExpiresAt }), ct);
        return Results.Created($"/api/portal/api-keys/{id}", new { id, name, prefix, scopes, expiresAt = Utc(entity.ExpiresAt), key, note = "Copy the key now. It is not shown again." });
    }

    private static async Task<IResult> Revoke(Guid id, TenancyDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
            return Problems.NotFound("key_not_found", "No such API key.");
        if (key.RevokedAt is not null)
            return Results.NoContent();

        key.RevokedAt = clock.GetUtcNow().UtcDateTime;
        await TenancyAudit.SaveAsync(db, audit, new AuditEntry("apikey.revoked", "api_key", id.ToString(), key.Name, new { prefix = key.Prefix }), ct);
        return Results.NoContent();
    }

    /// <summary>The fixed addresses our deliveries come from, for the receiver's firewall allow-list.</summary>
    private static IResult OutboundIps(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Platform:OutboundIps").Get<string[]>();
        var addresses = configured is { Length: > 0 } ? configured : ["203.0.113.10", "203.0.113.11", "2001:db8:100::/64"];
        return Results.Ok(new
        {
            addresses,
            demo = configured is not { Length: > 0 },
            note = configured is { Length: > 0 } ?
            "Allow these addresses on your firewall. They change only with 30 days notice." :
            "These are documentation addresses (RFC 5737 and RFC 3849), shown here because the demo has no public gateway. In production they are the static addresses of the platform's NAT gateway.",
        });
    }

    private static DateTimeOffset? Utc(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));
}
