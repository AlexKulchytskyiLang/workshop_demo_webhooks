using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace InvoiceHub.Tenancy.Directory;

/// <summary>
/// Tenant directory reads. Every authenticated request resolves its tenant here, so lookups are cached for a short time;
/// a suspended tenant is therefore locked out within <see cref="CacheFor"/>, which is an explicit, documented trade-off.
/// </summary>
internal sealed class TenantDirectory(TenancyDbContext db, IMemoryCache cache) : ITenantDirectory
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        cache.GetOrCreateAsync($"tenant:id:{id}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return await db.Tenants.AsNoTracking().Where(t => t.Id == id).Select(Map).FirstOrDefaultAsync(ct);
        });

    public Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var key = slug.Trim().ToLowerInvariant();
        return db.Tenants.AsNoTracking().Where(t => t.Slug == key).Select(Map).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TenantSummary>> ListActiveAsync(CancellationToken ct = default) =>
        await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummary(t.Slug, t.Name))
            .ToListAsync(ct);

    private static readonly System.Linq.Expressions.Expression<Func<TenantEntity, TenantInfo>> Map =
        t => new TenantInfo(t.Id, t.Slug, t.Name, t.Status);
}
