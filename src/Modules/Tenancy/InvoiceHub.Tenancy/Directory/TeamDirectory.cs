using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.Tenancy.Directory;

internal sealed class TeamDirectory(TenancyDbContext db) : ITeamDirectory
{
    public async Task<IReadOnlyList<TeamMember>> ListActiveAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default) =>
        await db.UserProfiles.AsNoTracking()
            .Where(u => u.DisabledAt == null && roles.Contains(u.Role))
            .OrderBy(u => u.Email)
            .Select(u => new TeamMember(u.Id, u.KeycloakSub, u.Email, u.DisplayName, u.Role))
            .ToListAsync(ct);
}
