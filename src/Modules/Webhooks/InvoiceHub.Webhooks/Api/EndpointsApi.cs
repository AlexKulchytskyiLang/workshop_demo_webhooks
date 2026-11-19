using System.Security.Claims;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Delivery;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Api;

/// <summary>
/// The tenant portal's endpoint management API. Every handler runs with the tenant from the signed token (the tenant middleware already set it)
/// and every query goes through row-level security a second time (P6), so an id belonging to another tenant is simply "not found".
/// </summary>
internal static class EndpointsApi
{
    public const int MaxEndpointsPerTenant = 50;
    public const int MaxSubscriptionsPerEndpoint = 60;
    private static readonly string[] Styles = [PayloadStyle.Fat, PayloadStyle.Thin, "auto"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/portal/endpoints").WithTags("Endpoints");
        g.MapGet("/", List).RequireAuthorization(Permissions.EndpointsRead);
        g.MapPost("/", Create).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapGet("/{id:guid}", Get).RequireAuthorization(Permissions.EndpointsRead);
        g.MapPut("/{id:guid}", Update).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Permissions.EndpointsDelete);
        g.MapPut("/{id:guid}/subscriptions", SetSubscriptions).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapPost("/{id:guid}/pause", Pause).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapPost("/{id:guid}/resume", Resume).RequireAuthorization(Permissions.EndpointsWrite);

        app.MapGet("/api/portal/egress-policy", (IOptions<WebhooksOptions> options, IHostEnvironment env) => Results.Ok(new
        {
            strict = !env.IsDevelopment() || options.Value.Egress.DevAllow.Count == 0,
            devAllow = env.IsDevelopment() ? options.Value.Egress.DevAllow : [],
            notice = env.IsDevelopment() && options.Value.Egress.DevAllow.Count > 0
                ? "Demo egress policy: the local receivers listed here are reachable. Everything else follows the production rules: HTTPS only, no private or metadata addresses."
                : null,
        })).RequireAuthorization(Permissions.EndpointsRead).WithTags("Endpoints");

        VersionsApi.Map(app);
    }

    // ---- reads ----

    private static async Task<IResult> List(HttpRequest request, WebhooksDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var endpoints = await db.Endpoints.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var subscriptions = (await db.Subscriptions.AsNoTracking().OrderBy(s => s.EventType).Select(s => new { s.EndpointId, s.EventType }).ToListAsync(ct))
            .GroupBy(s => s.EndpointId).ToDictionary(g => g.Key, g => g.Select(x => x.EventType).ToList());
        var backlog = await db.Deliveries.AsNoTracking()
            .Where(d => d.Status != DeliveryStatus.Succeeded && d.Status != DeliveryStatus.Dead)
            .GroupBy(d => new { d.EndpointId, d.Status }).Select(g => new { g.Key.EndpointId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);

        // 24 h health: success share of attempts in 12 two-hour buckets (the sparkline of the prototype)
        var from = now.AddHours(-24);
        var buckets = await db.QueryAsync("""
            SELECT d.endpoint_id, b.bucket, COUNT(*) AS total, SUM(CASE WHEN a.outcome = 'success' THEN 1 ELSE 0 END) AS ok
            FROM webhooks.delivery_attempt a JOIN webhooks.delivery d ON d.id = a.delivery_id
            CROSS APPLY (SELECT CASE WHEN DATEDIFF(MINUTE, @from, a.started_at) / 120 > 11 THEN 11 ELSE DATEDIFF(MINUTE, @from, a.started_at) / 120 END AS bucket) b
            WHERE a.started_at >= @from AND d.is_test = 0
            GROUP BY d.endpoint_id, b.bucket
            """, r => (Endpoint: r.GetGuid(0), Bucket: r.GetInt32(1), Total: r.GetInt32(2), Ok: r.GetInt32(3)), ct, ("@from", from));

        return Results.Ok(new
        {
            items = endpoints.Select(e =>
            {
                var mine = buckets.Where(b => b.Endpoint == e.Id).ToList();
                var total = mine.Sum(b => b.Total);
                var series = Enumerable.Range(0, 12).Select(i => mine.FirstOrDefault(b => b.Bucket == i) is { Total: > 0 } b ? Math.Round(100.0 * b.Ok / b.Total, 1) : (double?)null).ToList();
                var types = subscriptions.GetValueOrDefault(e.Id) ?? [];
                var summary = EndpointViews.Summary(e, now, types.Count, backlog.Where(b => b.EndpointId == e.Id).ToDictionary(b => b.Status, b => b.Count), EndpointViews.JwksUrl(request, e.Id));
                return new
                {
                    summary,
                    events = types,
                    successRate24h = total == 0 ? (double?)null : Math.Round(100.0 * mine.Sum(b => b.Ok) / total, 1),
                    attempts24h = total,
                    series,
                };
            }),
        });
    }

    private static async Task<IResult> Get(Guid id, HttpRequest request, WebhooksDbContext db, HeaderStore headerStore, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        var subs = await db.Subscriptions.AsNoTracking().Where(s => s.EndpointId == id).OrderBy(s => s.EventType).ToListAsync(ct);
        var backlog = await db.Deliveries.AsNoTracking().Where(d => d.EndpointId == id && d.Status != DeliveryStatus.Succeeded && d.Status != DeliveryStatus.Dead)
            .GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var keys = await db.Secrets.AsNoTracking().Where(s => s.EndpointId == id && (s.ValidUntil == null || s.ValidUntil > now.AddDays(-1))).ToListAsync(ct); // key ids only: secrets need a step-up reveal

        return Results.Ok(new
        {
            endpoint = EndpointViews.Summary(e, now, subs.Count, backlog, EndpointViews.JwksUrl(request, e.Id)),
            subscriptions = subs.Select(s => new { s.EventType, filter = s.FilterJson is null ? (JsonElement?)null : JsonDocument.Parse(s.FilterJson).RootElement }),
            headers = HeaderStore.ToDto(e.HeadersJson),
            auth = EndpointViews.Auth(e),
            delivery = EndpointViews.Delivery(e),
            secrets = EndpointViews.Keys(keys, now),
        });
    }

    // ---- writes ----

    private static async Task<IResult> Create(
        EndpointInput input, HttpRequest request, ClaimsPrincipal user, ITenantContext tenant, WebhooksDbContext db, SsrfGuard guard, IEventCatalog catalog,
        EndpointSecrets secrets, IAuditLog audit, TimeProvider clock, ILogger<EndpointInput> logger, CancellationToken ct)
    {
        var tenantId = tenant.TenantId!.Value;
        if (await db.Endpoints.CountAsync(ct) >= MaxEndpointsPerTenant)
            return Problems.Conflict("endpoint_limit", $"A tenant can have at most {MaxEndpointsPerTenant} endpoints.");

        var invalid = await Validate(input, requireEvents: true, guard, catalog, ct);
        if (invalid is not null) return invalid;

        var now = clock.GetUtcNow().UtcDateTime;
        var signing = input.Signing ?? "hmac";
        var endpoint = new EndpointEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = input.Name!.Trim(),
            Url = input.Url!.Trim(),
            Description = input.Description?.Trim(),
            Environment = input.Environment ?? "production",
            Status = EndpointStatus.Pending, // nothing real is sent until a test passes (UX-SPEC D3)
            PayloadStyle = input.PayloadStyle ?? PayloadStyle.Fat,
            ApiVersion = input.ApiVersion ?? ApiVersions.Current,
            Signing = signing,
            RetryPreset = input.RetryPreset ?? "standard",
            TimeoutSeconds = input.TimeoutSeconds ?? 10,
            MaxInflight = input.MaxInflight ?? 5,
            CreatedBy = EndpointViews.Actor(user),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Endpoints.Add(endpoint);
        if (signing is "hmac" or "both") db.Secrets.Add(secrets.CreateHmac(tenantId, endpoint.Id, now));
        if (signing is "es256" or "both")
        {
            var (secret, publicKey) = secrets.CreateEs256(tenantId, endpoint.Id, now);
            db.Secrets.Add(secret);
            db.PublicKeys.Add(publicKey);
        }
        for (var slot = 1; slot <= 10; slot++) db.Slots.Add(new EndpointSlotEntity { EndpointId = endpoint.Id, SlotNo = slot, TenantId = tenantId });
        var types = input.Events!.Distinct().ToList();
        foreach (var type in types) db.Subscriptions.Add(new SubscriptionEntity { TenantId = tenantId, EndpointId = endpoint.Id, EventType = type });

        await db.SaveAsync(audit, new AuditEntry("endpoint.created", "endpoint", endpoint.Id.ToString(), endpoint.Name,
            new { urlHost = HostOf(endpoint.Url), events = types, payloadStyle = endpoint.PayloadStyle, signing, environment = endpoint.Environment }), ct);
        logger.LogInformation("Endpoint {EndpointId} created by {Actor}", endpoint.Id, endpoint.CreatedBy);
        return Results.Created($"/api/portal/endpoints/{endpoint.Id}", EndpointViews.Summary(endpoint, now, types.Count, new Dictionary<string, int>(), EndpointViews.JwksUrl(request, endpoint.Id)));
    }

    private static async Task<IResult> Update(
        Guid id, EndpointInput input, HttpRequest request, WebhooksDbContext db, SsrfGuard guard, IEventCatalog catalog, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        var before = (e.Name, e.Description, e.Environment, e.PayloadStyle, e.ApiVersion, e.Url);

        var merged = input with
        {
            Name = input.Name ?? e.Name,
            Url = input.Url ?? e.Url,
            Events = [],
            PayloadStyle = input.PayloadStyle ?? e.PayloadStyle,
            ApiVersion = input.ApiVersion ?? e.ApiVersion,
            RetryPreset = null,
            TimeoutSeconds = null,
            MaxInflight = null,
            Environment = input.Environment ?? e.Environment,
        };
        var invalid = await Validate(merged, requireEvents: false, guard, catalog, ct);
        if (invalid is not null) return invalid;

        // a thin/fat switch must stay consistent with what the endpoint is subscribed to
        if (merged.PayloadStyle == PayloadStyle.Fat)
        {
            var types = await db.Subscriptions.Where(s => s.EndpointId == id).Select(s => s.EventType).ToListAsync(ct);
            var pii = types.FirstOrDefault(t => catalog.Find(t)?.ContainsPii == true);
            if (pii is not null) return Problems.Validation("pii_requires_thin", $"'{pii}' carries personal data, so this endpoint must use the thin payload style.");
        }

        var urlChanged = !string.Equals(e.Url, merged.Url!.Trim(), StringComparison.Ordinal);
        e.Name = merged.Name!.Trim();
        e.Description = input.Description?.Trim() ?? e.Description;
        e.Environment = merged.Environment!;
        e.PayloadStyle = merged.PayloadStyle!;
        e.ApiVersion = merged.ApiVersion!;
        if (urlChanged)
        {
            // a new address must prove itself again before real events go there (validation before save, ASR-21)
            e.Url = merged.Url!.Trim();
            e.VerifiedAt = null;
            e.Status = EndpointStatus.Pending;
        }
        e.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        var changed = new List<string>();
        if (before.Name != e.Name) changed.Add("name");
        if (before.Description != e.Description) changed.Add("description");
        if (before.Environment != e.Environment) changed.Add("environment");
        if (before.PayloadStyle != e.PayloadStyle) changed.Add("payloadStyle");
        if (before.ApiVersion != e.ApiVersion) changed.Add("apiVersion");
        if (urlChanged) changed.Add("url");
        await db.SaveAsync(audit, new AuditEntry("endpoint.updated", "endpoint", id.ToString(), e.Name,
            new { changed, payloadStyle = e.PayloadStyle, apiVersion = e.ApiVersion, urlHost = urlChanged ? HostOf(e.Url) : null, mustReverify = urlChanged }), ct);
        var count = await db.Subscriptions.CountAsync(s => s.EndpointId == id, ct);
        return Results.Ok(EndpointViews.Summary(e, e.UpdatedAt, count, new Dictionary<string, int>(), EndpointViews.JwksUrl(request, e.Id)));
    }

    private static async Task<IResult> SetSubscriptions(Guid id, SubscriptionsInput input, ITenantContext tenant, WebhooksDbContext db, IEventCatalog catalog, IAuditLog audit, CancellationToken ct)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        var wanted = input.Subscriptions ?? [];
        if (wanted.Count > MaxSubscriptionsPerEndpoint) return Problems.Validation("too_many_subscriptions", $"At most {MaxSubscriptionsPerEndpoint} event types per endpoint.");
        if (wanted.Count == 0) return Problems.Validation("no_events", "Choose at least one event type. To stop deliveries, pause the endpoint instead.");

        var rows = new List<SubscriptionEntity>();
        foreach (var s in wanted)
        {
            if (string.IsNullOrWhiteSpace(s.EventType) || !catalog.Exists(s.EventType)) return Problems.Validation("unknown_event", $"Unknown event type '{s.EventType}'.");
            if (e.PayloadStyle == PayloadStyle.Fat && catalog.Find(s.EventType)?.ContainsPii == true)
                return Problems.Validation("pii_requires_thin", $"'{s.EventType}' carries personal data, so this endpoint must use the thin payload style.");

            string? filterJson = null;
            if (s.Filter is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } filter)
            {
                filterJson = filter.GetRawText();
                var (parsed, error) = EventFilter.Parse(filterJson);
                if (parsed is null) return Problems.Validation("invalid_filter", $"{s.EventType}: {error}");
                if (parsed.IsEmpty) filterJson = null;
            }
            if (rows.Any(r => r.EventType == s.EventType)) return Problems.Validation("duplicate_event", $"'{s.EventType}' is listed twice.");
            rows.Add(new SubscriptionEntity { TenantId = tenant.TenantId!.Value, EndpointId = id, EventType = s.EventType, FilterJson = filterJson });
        }

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Subscriptions.Where(s => s.EndpointId == id).ExecuteDeleteAsync(ct);
            db.Subscriptions.AddRange(rows);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(tx.GetDbTransaction(), new AuditEntry("endpoint.events_changed", "endpoint", id.ToString(), e.Name,
                new { count = rows.Count, events = rows.Select(r => r.EventType), filtered = rows.Where(r => r.FilterJson is not null).Select(r => r.EventType) }), ct);
            await tx.CommitAsync(ct);
        });
        return Results.Ok(new { count = rows.Count });
    }

    private static async Task<IResult> Pause(Guid id, WebhooksDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (e.Status is not (EndpointStatus.Active or EndpointStatus.Pending)) return Problems.Conflict("not_pausable", $"The endpoint is {e.Status}.");
        e.Status = EndpointStatus.Paused; // events that arrive meanwhile are HELD, never dropped
        e.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveAsync(audit, new AuditEntry("endpoint.paused", "endpoint", id.ToString(), e.Name), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Resume(Guid id, WebhooksDbContext db, IOptions<WebhooksOptions> options, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (e.Status is not (EndpointStatus.Paused or EndpointStatus.Disabled)) return Problems.Conflict("not_resumable", $"The endpoint is {e.Status}.");

        var now = clock.GetUtcNow().UtcDateTime;
        e.Status = e.VerifiedAt is null ? EndpointStatus.Pending : EndpointStatus.Active;
        e.DisabledReason = null;
        Health.Apply(e, EndpointHealth.Healthy);
        e.UpdatedAt = now;

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(tx.GetDbTransaction(), new AuditEntry("endpoint.resumed", "endpoint", id.ToString(), e.Name, new { status = e.Status }), ct);
            if (e.Status == EndpointStatus.Active)
            {
                // held events are released at a steady pace (UX-SPEC D3 "rate-limited"), not as one burst at a receiver that just came back
                await db.ExecuteAsync("""
                    ;WITH h AS (SELECT id, ROW_NUMBER() OVER (ORDER BY created_at) AS rn FROM webhooks.delivery WHERE endpoint_id = @e AND status = 'held')
                    UPDATE d SET status = 'pending', held_reason = NULL, held_until = NULL, dispatch_until = NULL, updated_at = SYSUTCDATETIME(),
                                 next_attempt_at = DATEADD(MILLISECOND, h.rn * @spacing, SYSUTCDATETIME())
                    FROM webhooks.delivery d JOIN h ON h.id = d.id
                    """, ct, ("@e", id), ("@spacing", options.Value.Delivery.ResumeSpacingMilliseconds));
            }
            await tx.CommitAsync(ct);
        });
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(Guid id, WebhooksDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        e.DeletedAt = clock.GetUtcNow().UtcDateTime; // soft delete: history stays, queued deliveries end as Dead("endpoint_deleted") when picked up
        e.Status = EndpointStatus.Disabled;
        e.UpdatedAt = e.DeletedAt.Value;
        await db.SaveAsync(audit, new AuditEntry("endpoint.deleted", "endpoint", id.ToString(), e.Name, new { urlHost = HostOf(e.Url) }), ct);
        return Results.NoContent();
    }

    // ---- shared validation ----

    internal static async Task<IResult?> Validate(EndpointInput i, bool requireEvents, SsrfGuard guard, IEventCatalog catalog, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 120) return Problems.Validation("invalid_name", "A name of 1 to 120 characters is required.");
        if (i.Description is { Length: > 400 }) return Problems.Validation("invalid_description", "The description can be at most 400 characters.");
        if (string.IsNullOrWhiteSpace(i.Url)) return Problems.Validation("invalid_url", "A URL is required.");

        var style = i.PayloadStyle ?? PayloadStyle.Fat;
        if (!Styles.Contains(style)) return Problems.Validation("invalid_payload_style", "The payload style must be 'fat', 'thin' or 'auto'.");
        if (!ApiVersions.IsSupported(i.ApiVersion ?? ApiVersions.Current)) return Problems.Validation("invalid_api_version", $"Supported versions: {string.Join(", ", ApiVersions.Supported)}.");
        if (i.RetryPreset is not (null or "standard" or "aggressive" or "demo")) return Problems.Validation("invalid_retry_preset", "The retry preset must be 'standard', 'aggressive' or 'demo'. Custom schedules are set on the Delivery tab.");
        if (i.TimeoutSeconds is < 1 or > 30) return Problems.Validation("invalid_timeout", "The timeout must be between 1 and 30 seconds.");
        if (i.MaxInflight is < 1 or > 10) return Problems.Validation("invalid_concurrency", "Concurrent requests must be between 1 and 10.");
        if (i.Environment is not null && !EndpointViews.Environments.Contains(i.Environment)) return Problems.Validation("invalid_environment", "The environment must be production, staging or development.");
        if (i.Signing is not (null or "hmac" or "es256" or "both")) return Problems.Validation("invalid_signing", "Signing must be hmac, es256 or both.");

        var decision = await guard.EvaluateAsync(i.Url.Trim(), ct);
        if (!decision.Allowed) return Problems.Validation(decision.Code, decision.Message);

        var events = i.Events ?? [];
        if (requireEvents && events.Length == 0) return Problems.Validation("no_events", "Choose at least one event type.");
        if (events.Length > MaxSubscriptionsPerEndpoint) return Problems.Validation("too_many_subscriptions", $"At most {MaxSubscriptionsPerEndpoint} event types per endpoint.");
        foreach (var type in events)
        {
            if (!catalog.Exists(type)) return Problems.Validation("unknown_event", $"Unknown event type '{type}'.");
            if (style == PayloadStyle.Fat && catalog.Find(type)?.ContainsPii == true)
                return Problems.Validation("pii_requires_thin", $"'{type}' carries personal data, so this endpoint must use the thin payload style.");
        }
        return null;
    }

    internal static IResult NotFound() => Problems.NotFound("endpoint_not_found", "No such endpoint.");

    /// <summary>Only the host goes into the audit log: a URL path or query can carry a secret token.</summary>
    internal static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : null;
}
