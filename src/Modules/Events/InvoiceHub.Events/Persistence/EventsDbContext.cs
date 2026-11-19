using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Events.Persistence;

internal sealed class EventEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Type { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public long Sequence { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public bool IsTest { get; set; }
    public string? TraceParent { get; set; }
}

internal sealed class OutboxEntity
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedUntil { get; set; }
    public DateTime? FannedOutAt { get; set; }
}

/// <summary>Result row of the claim statement (UPDATE ... OUTPUT).</summary>
internal sealed record ClaimedRow(long Id, Guid EventId, Guid TenantId);

internal sealed class EventsDbContext(DbContextOptions<EventsDbContext> options) : DbContext(options)
{
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<OutboxEntity> Outbox => Set<OutboxEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("events");

        b.Entity<EventEntity>(e =>
        {
            e.ToTable("event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.DataJson).HasColumnName("data_json");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.IsTest).HasColumnName("is_test");
            e.Property(x => x.TraceParent).HasColumnName("trace_parent").HasMaxLength(60).IsUnicode(false);
        });

        b.Entity<OutboxEntity>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(60).IsUnicode(false);
            e.Property(x => x.ClaimedUntil).HasColumnName("claimed_until");
            e.Property(x => x.FannedOutAt).HasColumnName("fanned_out_at");
        });
    }
}
