using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Tenancy.Persistence;

internal sealed class TenantEntity
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public int DeliveryQuotaPerMin { get; set; } = 600;
    public int MaxInflightPerEndpoint { get; set; } = 5;
    public DateTime CreatedAt { get; set; }
}

internal sealed class UserProfileEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid KeycloakSub { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? DisabledAt { get; set; }
}

internal sealed class ApiKeyEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public string Scopes { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

internal sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options) : DbContext(options)
{
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("tenancy");

        b.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenant");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.DeliveryQuotaPerMin).HasColumnName("delivery_quota_per_min");
            e.Property(x => x.MaxInflightPerEndpoint).HasColumnName("max_inflight_per_endpoint");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<UserProfileEntity>(e =>
        {
            e.ToTable("user_profile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.KeycloakSub).HasColumnName("keycloak_sub");
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(254);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(120);
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.DisabledAt).HasColumnName("disabled_at");
        });

        b.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_key");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(80);
            e.Property(x => x.Prefix).HasColumnName("prefix").HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.KeyHash).HasColumnName("key_hash").HasMaxLength(32).IsFixedLength();
            e.Property(x => x.Scopes).HasColumnName("scopes").HasMaxLength(200).IsUnicode(false);
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        });
    }
}
