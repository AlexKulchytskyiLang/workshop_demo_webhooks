using InvoiceHub.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace InvoiceHub.Tenancy.Http;

public interface IDisabledUsers
{
    Task<bool> IsDisabledAsync(Guid keycloakSub, CancellationToken ct);
}

/// <summary>Remembers for a few seconds who is disabled, so the check costs one query per user per 15 s instead of one per request.</summary>
internal sealed class DisabledUsers(TenancyDbContext db, IMemoryCache cache) : IDisabledUsers
{
    private static readonly TimeSpan Remember = TimeSpan.FromSeconds(15);

    public async Task<bool> IsDisabledAsync(Guid keycloakSub, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"disabled-user:{keycloakSub}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Remember;
            return await db.UserProfiles.AsNoTracking().AnyAsync(u => u.KeycloakSub == keycloakSub && u.DisabledAt != null, ct);
        });
}
