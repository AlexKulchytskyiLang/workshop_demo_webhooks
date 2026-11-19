using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Http;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Tenancy.ApiKeys;

/// <summary>The format and the hashing of platform API keys. <c>ihk_{tenant}_{key id}_{secret}</c>: the tenant and key id are not secret, the secret is 256 random bits.</summary>
internal static class ApiKeyFormat
{
    public sealed record Parsed(Guid TenantId, Guid KeyId, string Secret);

    public static (string Key, string Prefix, byte[] Hash) Create(Guid tenantId, Guid keyId)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        return ($"{ApiKeyAuth.Prefix}{tenantId:N}_{keyId:N}_{secret}", $"{ApiKeyAuth.Prefix}{keyId.ToString("N")[..8]}", Hash(secret));
    }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    public static Parsed? TryParse(string token)
    {
        if (!token.StartsWith(ApiKeyAuth.Prefix, StringComparison.Ordinal)) return null;
        var parts = token[ApiKeyAuth.Prefix.Length..].Split('_', 3); // the secret is base64url and may itself contain '_'
        if (parts.Length != 3 || parts[2].Length is < 20 or > 100)
            return null;

        return Guid.TryParseExact(parts[0], "N", out var tenant) && Guid.TryParseExact(parts[1], "N", out var key) ?
               new Parsed(tenant, key, parts[2]) : null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Authenticates <c>Authorization: Bearer ihk_...</c>. The tenant comes out of the key itself and is set BEFORE the lookup, so the lookup is subject to that tenant's
/// row-level security like everything else; a key can therefore never match a row of another tenant, even with a forged tenant part.
/// The same message is returned for every failure, so the response does not reveal whether a key id exists.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
                                                  ILoggerFactory logger,
                                                  UrlEncoder encoder,
                                                  TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + ApiKeyAuth.Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var parsed = ApiKeyFormat.TryParse(header["Bearer ".Length..].Trim());
        if (parsed is null)
            return AuthenticateResult.Fail("invalid api key");

        var services = Context.RequestServices;
        var directory = services.GetRequiredService<ITenantDirectory>();
        var tenant = await directory.FindByIdAsync(parsed.TenantId, Context.RequestAborted);
        if (tenant is null || tenant.Status != TenantStatus.Active)
            return AuthenticateResult.Fail("invalid api key");

        services.GetRequiredService<ITenantScope>().SetTenant(parsed.TenantId);
        var db = services.GetRequiredService<TenancyDbContext>();
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == parsed.KeyId, Context.RequestAborted);
        var now = clock.GetUtcNow().UtcDateTime;
        if (key is null || key.RevokedAt is not null || key.ExpiresAt is { } expires && expires <= now ||
            !CryptographicOperations.FixedTimeEquals(key.KeyHash, ApiKeyFormat.Hash(parsed.Secret)))
            return AuthenticateResult.Fail("invalid api key");

        // "last used" is worth knowing, not worth a write per request: at most one a minute
        if (key.LastUsedAt is null || key.LastUsedAt < now.AddMinutes(-1))
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), Context.RequestAborted);

        var claims = new List<Claim>
        {
            new(Auth.TenantClaim, parsed.TenantId.ToString()),
            new(ApiKeyAuth.KeyIdClaim, key.Id.ToString()),
            new("preferred_username", $"api-key:{key.Prefix}"),
        };
        claims.AddRange(key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => new Claim(ApiKeyAuth.ScopeClaim, s)));
        Context.Items[TenantResolutionMiddleware.TenantItemKey] = tenant;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}
