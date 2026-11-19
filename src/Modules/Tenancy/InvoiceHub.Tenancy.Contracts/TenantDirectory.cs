namespace InvoiceHub.Tenancy.Contracts;

public static class TenantStatus
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Suspended = "suspended";
}

public sealed record TenantInfo(Guid Id, string Slug, string Name, string Status);

/// <summary>What the public tenant picker may show: no ids, no status.</summary>
public sealed record TenantSummary(string Slug, string Name);

/// <summary>Read access to the tenant directory for other modules (the directory is global metadata, not tenant data).</summary>
public interface ITenantDirectory
{
    Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken ct = default);

    Task<IReadOnlyList<TenantSummary>> ListActiveAsync(CancellationToken ct = default);
}

/// <summary>A member of a tenant's team, as other modules may see them (who to notify).</summary>
public sealed record TeamMember(Guid UserId, Guid KeycloakSub, string Email, string Name, string Role);

/// <summary>The active members of the CURRENT tenant (the tenant context decides, row-level security enforces it).</summary>
public interface ITeamDirectory
{
    Task<IReadOnlyList<TeamMember>> ListActiveAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default);
}
