using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Webhooks.Persistence;

internal sealed class EndpointEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "pending";
    public string? DisabledReason { get; set; }
    public string PayloadStyle { get; set; } = "fat";
    public string ApiVersion { get; set; } = "2026-11-01";
    public string Signing { get; set; } = "hmac";
    public string RetryPreset { get; set; } = "standard";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxInflight { get; set; } = 5;
    public string? HeadersJson { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? FailingSince { get; set; }
    public DateTime? CircuitOpenUntil { get; set; }
    public int CircuitProbeStep { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string Environment { get; set; } = "production";
    public string? RetryJson { get; set; }
    public int CircuitFailures { get; set; } = 5;
    public int AutodisableFailures { get; set; } = 100;
    public int AutodisableHours { get; set; } = 24;
    public int AutodisableDays { get; set; } = 5;
    public int HoldDays { get; set; } = 7;
    public string AuthType { get; set; } = "none";
    public string? AuthHeaderName { get; set; }
    public byte[]? AuthValueEnc { get; set; }
    public DateTime? LastTestPassedAt { get; set; }
}

internal sealed class ReplayJobEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EndpointId { get; set; }
    public string Kind { get; set; } = "replay";
    public string Mode { get; set; } = "replay";
    public string States { get; set; } = "";
    public string? EventType { get; set; }
    public DateTime? FromAt { get; set; }
    public DateTime? ToAt { get; set; }
    public int Total { get; set; }
    public int Discarded { get; set; }
    public int RatePerSecond { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
}

internal sealed class EndpointSecretEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EndpointId { get; set; }
    public byte[] SecretEnc { get; set; } = [];
    public string KeyId { get; set; } = "";
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Algorithm { get; set; } = "hmac";
}

/// <summary>The public half of an asymmetric key: published as a JWKS, readable without a tenant context.</summary>
internal sealed class PublicKeyEntity
{
    public Guid EndpointId { get; set; }
    public string Kid { get; set; } = "";
    public string JwkJson { get; set; } = "";
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
}

internal sealed class SubscriptionEntity
{
    public Guid TenantId { get; set; }
    public Guid EndpointId { get; set; }
    public string EventType { get; set; } = "";
    public string? FilterJson { get; set; }
}

internal sealed class DeliveryEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid EndpointId { get; set; }
    public string EventType { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime DeadlineAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public bool IsTest { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedUntil { get; set; }
    public DateTime? DispatchUntil { get; set; }
    public string? HeldReason { get; set; }
    public DateTime? HeldUntil { get; set; }
    public DateTime? FirstAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? AggregateId { get; set; }
    public Guid? ReplayJobId { get; set; }
    public string? ReplayPriorStatus { get; set; }
    public int ReplayCount { get; set; }
    public DateTime? ReplayedAt { get; set; }
    public int AttemptFloor { get; set; }
    public string ApiVersion { get; set; } = "2026-11-01";
}

internal sealed class DeliveryAttemptEntity
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeliveryId { get; set; }
    public int AttemptNo { get; set; }
    public DateTime StartedAt { get; set; }
    public int DurationMs { get; set; }
    public string Outcome { get; set; } = "";
    public int? StatusCode { get; set; }
    public string? ResponseSnippet { get; set; }
    public string? Error { get; set; }
    public string? TraceId { get; set; }
    public string? RequestHeadersJson { get; set; }
    public string? PayloadStyle { get; set; }
    public int? ResponseSize { get; set; }
}

internal sealed class EndpointSlotEntity
{
    public Guid EndpointId { get; set; }
    public int SlotNo { get; set; }
    public Guid TenantId { get; set; }
    public Guid? DeliveryId { get; set; }
    public DateTime? LeasedUntil { get; set; }
}

/// <summary>Row returned by the slot / sweeper statements (UPDATE ... OUTPUT).</summary>
internal sealed record DueDelivery(Guid Id, Guid TenantId, int AttemptCount);

internal sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options) : DbContext(options)
{
    public DbSet<EndpointEntity> Endpoints => Set<EndpointEntity>();
    public DbSet<EndpointSecretEntity> Secrets => Set<EndpointSecretEntity>();
    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<DeliveryEntity> Deliveries => Set<DeliveryEntity>();
    public DbSet<DeliveryAttemptEntity> Attempts => Set<DeliveryAttemptEntity>();
    public DbSet<EndpointSlotEntity> Slots => Set<EndpointSlotEntity>();
    public DbSet<PublicKeyEntity> PublicKeys => Set<PublicKeyEntity>();
    public DbSet<ReplayJobEntity> ReplayJobs => Set<ReplayJobEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("webhooks");

        b.Entity<EndpointEntity>(e =>
        {
            e.ToTable("endpoint");
            e.HasKey(x => x.Id);
            e.HasQueryFilter(x => x.DeletedAt == null); // soft delete: deleted endpoints vanish from every normal query
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
            e.Property(x => x.Url).HasColumnName("url").HasMaxLength(2000);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(400);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.DisabledReason).HasColumnName("disabled_reason").HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.PayloadStyle).HasColumnName("payload_style").HasMaxLength(8).IsUnicode(false);
            e.Property(x => x.ApiVersion).HasColumnName("api_version").HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Signing).HasColumnName("signing").HasMaxLength(8).IsUnicode(false);
            e.Property(x => x.RetryPreset).HasColumnName("retry_preset").HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds");
            e.Property(x => x.MaxInflight).HasColumnName("max_inflight");
            e.Property(x => x.HeadersJson).HasColumnName("headers_json");
            e.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            e.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
            e.Property(x => x.FailingSince).HasColumnName("failing_since");
            e.Property(x => x.CircuitOpenUntil).HasColumnName("circuit_open_until");
            e.Property(x => x.CircuitProbeStep).HasColumnName("circuit_probe_step");
            e.Property(x => x.LastSuccessAt).HasColumnName("last_success_at");
            e.Property(x => x.LastFailureAt).HasColumnName("last_failure_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.Property(x => x.Environment).HasColumnName("environment").HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.RetryJson).HasColumnName("retry_json");
            e.Property(x => x.CircuitFailures).HasColumnName("circuit_failures");
            e.Property(x => x.AutodisableFailures).HasColumnName("autodisable_failures");
            e.Property(x => x.AutodisableHours).HasColumnName("autodisable_hours");
            e.Property(x => x.AutodisableDays).HasColumnName("autodisable_days");
            e.Property(x => x.HoldDays).HasColumnName("hold_days");
            e.Property(x => x.AuthType).HasColumnName("auth_type").HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.AuthHeaderName).HasColumnName("auth_header_name").HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.AuthValueEnc).HasColumnName("auth_value_enc");
            e.Property(x => x.LastTestPassedAt).HasColumnName("last_test_passed_at");
        });

        b.Entity<ReplayJobEntity>(e =>
        {
            e.ToTable("replay_job");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.States).HasColumnName("states").HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.FromAt).HasColumnName("from_at");
            e.Property(x => x.ToAt).HasColumnName("to_at");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.Discarded).HasColumnName("discarded");
            e.Property(x => x.RatePerSecond).HasColumnName("rate_per_second");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        });

        b.Entity<EndpointSecretEntity>(e =>
        {
            e.ToTable("endpoint_secret");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.SecretEnc).HasColumnName("secret_enc");
            e.Property(x => x.KeyId).HasColumnName("key_id").HasMaxLength(24).IsUnicode(false);
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidUntil).HasColumnName("valid_until");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Algorithm).HasColumnName("algorithm").HasMaxLength(8).IsUnicode(false);
        });

        b.Entity<PublicKeyEntity>(e =>
        {
            e.ToTable("public_key");
            e.HasKey(x => new { x.EndpointId, x.Kid });
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.Kid).HasColumnName("kid").HasMaxLength(24).IsUnicode(false);
            e.Property(x => x.JwkJson).HasColumnName("jwk_json");
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidUntil).HasColumnName("valid_until");
        });

        b.Entity<SubscriptionEntity>(e =>
        {
            e.ToTable("subscription");
            e.HasKey(x => new { x.EndpointId, x.EventType });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.FilterJson).HasColumnName("filter_json");
        });

        b.Entity<DeliveryEntity>(e =>
        {
            e.ToTable("delivery");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(80).IsUnicode(false);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.DeadlineAt).HasColumnName("deadline_at");
            e.Property(x => x.LastStatusCode).HasColumnName("last_status_code");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(400);
            e.Property(x => x.IsTest).HasColumnName("is_test");
            e.Property(x => x.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(60).IsUnicode(false);
            e.Property(x => x.ClaimedUntil).HasColumnName("claimed_until");
            e.Property(x => x.DispatchUntil).HasColumnName("dispatch_until");
            e.Property(x => x.HeldReason).HasColumnName("held_reason").HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.HeldUntil).HasColumnName("held_until");
            e.Property(x => x.FirstAttemptAt).HasColumnName("first_attempt_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.ReplayJobId).HasColumnName("replay_job_id");
            e.Property(x => x.ReplayPriorStatus).HasColumnName("replay_prior_status").HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.ReplayCount).HasColumnName("replay_count");
            e.Property(x => x.ReplayedAt).HasColumnName("replayed_at");
            e.Property(x => x.AttemptFloor).HasColumnName("attempt_floor");
            e.Property(x => x.ApiVersion).HasColumnName("api_version").HasMaxLength(10).IsUnicode(false);
        });

        b.Entity<EndpointSlotEntity>(e =>
        {
            e.ToTable("endpoint_slot");
            e.HasKey(x => new { x.EndpointId, x.SlotNo });
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.SlotNo).HasColumnName("slot_no");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.DeliveryId).HasColumnName("delivery_id");
            e.Property(x => x.LeasedUntil).HasColumnName("leased_until");
        });

        b.Entity<DeliveryAttemptEntity>(e =>
        {
            e.ToTable("delivery_attempt");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.DeliveryId).HasColumnName("delivery_id");
            e.Property(x => x.AttemptNo).HasColumnName("attempt_no");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(24).IsUnicode(false);
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.ResponseSnippet).HasColumnName("response_snippet").HasMaxLength(1000);
            e.Property(x => x.Error).HasColumnName("error").HasMaxLength(400);
            e.Property(x => x.TraceId).HasColumnName("trace_id").HasMaxLength(32).IsUnicode(false);
            e.Property(x => x.RequestHeadersJson).HasColumnName("request_headers_json");
            e.Property(x => x.PayloadStyle).HasColumnName("payload_style").HasMaxLength(8).IsUnicode(false);
            e.Property(x => x.ResponseSize).HasColumnName("response_size");
        });
    }
}
