using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Delivery;
using InvoiceHub.Webhooks.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Api;

/// <summary>
/// The delivery log (UX-SPEC D8): one row per event and endpoint with its attempts nested, searchable by endpoint, state, type, time and ids.
/// Keyset pagination keeps page 1000 as cheap as page 1. What a person may SEE of a payload follows their role and the event's PII class.
/// </summary>
internal static class DeliveriesApi
{
    public const int MaxPageSize = 100;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly string[] AllStatuses = [DeliveryStatus.Pending, DeliveryStatus.InFlight, DeliveryStatus.Succeeded, DeliveryStatus.RetryScheduled, DeliveryStatus.Held, DeliveryStatus.Dead];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/deliveries")
                       .WithTags("Deliveries");
        group.MapGet("/", List).RequireAuthorization(Permissions.EndpointsRead);
        group.MapGet("/{id:guid}", Get).RequireAuthorization(Permissions.EndpointsRead);
        group.MapPost("/{id:guid}/replay", ReplayApi.ReplaySingle).RequireAuthorization(Permissions.ReplaySingle);
    }

    private static async Task<IResult> List(WebhooksDbContext db, Guid? endpointId, string? status,
                                            string? eventType, string? q, DateTime? from, DateTime? to,
                                            bool? replayed, int? limit, string? cursor, CancellationToken ct)
    {
        from = from.AsUtc();
        to = to.AsUtc();
        var take = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        var statuses = (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        if (statuses.Any(s => !AllStatuses.Contains(s)))
            return Problems.Validation("invalid_status", $"The status must be one of: {string.Join(", ", AllStatuses)}.");
        if (from is not null && to is not null && from >= to)
            return Problems.Validation("invalid_range", "The start of the time range must be before its end.");

        // every filter except the status: the status counts above the table are computed on this set, so they show what each status chip would give
        var baseQuery = db.Deliveries.AsNoTracking().Where(d => !d.IsTest);
        if (endpointId is { } ep)
            baseQuery = baseQuery.Where(d => d.EndpointId == ep);
        if (!string.IsNullOrWhiteSpace(eventType))
            baseQuery = baseQuery.Where(d => d.EventType == eventType);
        if (from is { } f)
            baseQuery = baseQuery.Where(d => d.CreatedAt >= f);
        if (to is { } t)
            baseQuery = baseQuery.Where(d => d.CreatedAt < t);
        if (replayed == true)
            baseQuery = baseQuery.Where(d => d.ReplayCount > 0);
        if (!string.IsNullOrWhiteSpace(q))
            baseQuery = ApplySearch(baseQuery, q.Trim());

        var query = statuses.Length > 0 ? baseQuery.Where(d => statuses.Contains(d.Status)) : baseQuery;
        if (cursor is not null)
        {
            if (!TryParseCursor(cursor, out var at, out var id)) return Problems.Validation("invalid_cursor", "The cursor is not valid.");
            query = query.Where(d => d.CreatedAt < at || (d.CreatedAt == at && d.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var ids = page.Select(d => d.EndpointId).Distinct().ToList();
        var names = await db.Endpoints.IgnoreQueryFilters().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name, ct);

        // the counts are only needed once per search, not for every page
        Dictionary<string, int>? counts = null;
        if (cursor is null)
            counts = await baseQuery.GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return Results.Ok(new
        {
            items = page.Select(d => new
            {
                d.Id,
                d.EventId,
                webhookId = PayloadBuilder.EventId(d.EventId, d.IsTest),
                d.EndpointId,
                endpointName = names.GetValueOrDefault(d.EndpointId),
                d.EventType,
                d.AggregateId,
                d.Status,
                d.AttemptCount,
                d.LastStatusCode,
                d.LastError,
                d.HeldReason,
                replayed = d.ReplayCount > 0,
                nextAttemptAt = Utc(d.NextAttemptAt),
                createdAt = Utc(d.CreatedAt),
                completedAt = Utc(d.CompletedAt),
            }),
            counts = counts is null ? null : new
            {
                succeeded = counts.GetValueOrDefault(DeliveryStatus.Succeeded),
                retrying = counts.GetValueOrDefault(DeliveryStatus.RetryScheduled),
                pending = counts.GetValueOrDefault(DeliveryStatus.Pending) + counts.GetValueOrDefault(DeliveryStatus.InFlight),
                held = counts.GetValueOrDefault(DeliveryStatus.Held),
                dead = counts.GetValueOrDefault(DeliveryStatus.Dead),
                total = counts.Values.Sum(),
            },
            nextCursor = rows.Count > take ? MakeCursor(page[^1]) : null,
        });
    }

    /// <summary>Free text is limited to ids and names on purpose (UX-SPEC D8): indexing payload content would store personal data a second time.</summary>
    private static IQueryable<DeliveryEntity> ApplySearch(IQueryable<DeliveryEntity> query, string q)
    {
        var raw = q.StartsWith("evt_test_", StringComparison.OrdinalIgnoreCase) ? q[9..] : q.StartsWith("evt_", StringComparison.OrdinalIgnoreCase) ? q[4..] : q;
        if (Guid.TryParse(raw, out var guid)) return query.Where(d => d.Id == guid || d.EventId == guid);
        var lowered = q.ToLowerInvariant();
        return query.Where(d => (d.AggregateId != null && d.AggregateId.StartsWith(q)) || d.EventType.Contains(lowered));
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal user, WebhooksDbContext db, IEventReader events, IEventCatalog catalog, IOptions<WebhooksOptions> options, CancellationToken ct)
    {
        var d = await db.Deliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Problems.NotFound("delivery_not_found", "No such delivery.");

        var attempts = await db.Attempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.AttemptNo).ThenBy(a => a.Id).ToListAsync(ct);
        var endpoint = await db.Endpoints.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(e => e.Id == d.EndpointId, ct);
        var ev = await events.GetAsync(d.EventId, ct);
        var info = ev is null ? null : catalog.Find(ev.Type);

        var mayViewPayload = user.HasPermission(Permissions.PayloadsView);
        var last = attempts.LastOrDefault();

        // the body as it was sent: rebuilt from the event with the style the last attempt used (the same builder, so the same bytes)
        string? body = null;
        string? bodyNote = null;
        if (ev is not null && endpoint is not null)
        {
            var style = last?.PayloadStyle ?? (info?.ContainsPii == true || endpoint.PayloadStyle == PayloadStyle.Thin ? PayloadStyle.Thin : info?.Payload == "claim" ? PayloadStyle.ClaimCheck : PayloadStyle.Fat);
            if (!mayViewPayload) bodyNote = "Your role cannot view payloads.";
            else
            {
                var data = VersionProjection.Project(d.ApiVersion, ev.Type, ev.DataJson);
                if (style == PayloadStyle.ClaimCheck) data = ClaimCheckEnvelope.Redact(data, TimeSpan.FromMinutes(options.Value.Delivery.ClaimLinkMinutes));
                var bytes = PayloadBuilder.Build(style, ev.Id, ev.IsTest, ev.Type, d.ApiVersion, ev.OccurredAt, ev.AggregateId, ev.Sequence, data, info?.ReferenceKeys ?? []);
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                // Support sees the shape of a personal-data event, not its values (UX-SPEC section 9)
                if (info?.ContainsPii == true && !user.TenantRoles().Any(r => r is Roles.Owner or Roles.Admin or Roles.Developer))
                {
                    text = RedactData(text);
                    bodyNote = "Values are hidden for your role because this event carries personal data.";
                }
                body = Pretty(text);
            }
        }

        var headers = last?.RequestHeadersJson is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) : null;
        return Results.Ok(new
        {
            delivery = new
            {
                d.Id, d.EventId, webhookId = PayloadBuilder.EventId(d.EventId, d.IsTest), d.EndpointId, d.EventType, d.AggregateId, d.Status, d.AttemptCount, d.LastStatusCode, d.LastError, d.HeldReason,
                replayCount = d.ReplayCount, replayedAt = Utc(d.ReplayedAt), d.ReplayJobId,
                apiVersion = d.ApiVersion, deadlineAt = Utc(d.DeadlineAt), nextAttemptAt = Utc(d.NextAttemptAt), heldUntil = Utc(d.HeldUntil),
                createdAt = Utc(d.CreatedAt), completedAt = Utc(d.CompletedAt),
            },
            endpoint = endpoint is null ? null : new { endpoint.Id, endpoint.Name, endpoint.Url, endpoint.Status },
            @event = ev is null ? null : new { ev.Id, ev.Type, ev.AggregateId, ev.Sequence, occurredAt = ev.OccurredAt, containsPii = info?.ContainsPii ?? false, description = info?.Description },
            request = endpoint is null || last is null ? null : new { method = "POST", url = endpoint.Url, headers, body, bodyNote },
            canViewPayload = mayViewPayload,
            attempts = attempts.Select(a => new
            {
                a.AttemptNo, startedAt = Utc(a.StartedAt), a.DurationMs, a.Outcome, a.StatusCode, a.Error, a.TraceId, a.ResponseSize,
                responseSnippet = mayViewPayload ? a.ResponseSnippet : null,
            }),
        });
    }

    private static string RedactData(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        if (node["data"] is JsonObject data)
            foreach (var key in data.Select(p => p.Key).ToList()) data[key] = "[redacted]";
        return node.ToJsonString();
    }

    private static string Pretty(string json)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Indented); }
        catch (JsonException) { return json; }
    }

    internal static DateTimeOffset? Utc(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));

    private static string MakeCursor(DeliveryEntity d) => $"{d.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture)}_{d.Id:N}";

    private static bool TryParseCursor(string cursor, out DateTime at, out Guid id)
    {
        at = default;
        id = default;
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out id)) return false;
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
        at = new DateTime(ticks, DateTimeKind.Unspecified);
        return true;
    }
}
