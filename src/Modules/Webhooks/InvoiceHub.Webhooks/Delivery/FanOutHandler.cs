using System.Diagnostics;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Messaging;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Delivery;

/// <summary>
/// Fan-out (ASR-05): one committed event becomes one delivery row per matching endpoint, then one queue message per delivery.
/// Idempotent: the unique key (event, endpoint) and <c>WHERE NOT EXISTS</c> make a redelivered event message harmless, and the final
/// "queue everything that is still pending for this event" step repairs a crash between the insert and the enqueue.
/// </summary>
internal sealed class FanOutHandler(WebhooksDbContext db, IEventReader events, IFanOutTracker tracker,
                                    ITenantScope tenantScope, IDeliveryQueue queue, IOptions<WebhooksOptions> options,
                                    TimeProvider clock, ILogger<FanOutHandler> logger) : IEventMessageHandler
{
    public async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        tenantScope.SetTenant(message.TenantId); // per-message tenant scope: RLS now filters everything below to this tenant

        var ev = await events.GetAsync(message.EventId, ct) ?? throw new PoisonMessageException("event_not_found", $"Event {message.EventId} is not visible for tenant {message.TenantId}.");
        if (ev.IsTest)
        {
            await tracker.MarkFannedOutAsync(ev.Id, ct);
            return;
        } // test events are sent straight to one endpoint by the playground, never fanned out

        var candidates = await (
            from s in db.Subscriptions.AsNoTracking()
            join e in db.Endpoints.AsNoTracking() on s.EndpointId equals e.Id
            where s.EventType == ev.Type && e.Status != EndpointStatus.Pending
            select new { s.EndpointId, s.FilterJson, e.Status, e.RetryPreset, e.RetryJson, e.HoldDays, e.ApiVersion }).ToListAsync(ct);

        var canonical = JsonDocument.Parse(PayloadBuilder.CanonicalForFilter(ev.Type, ev.AggregateId, ev.Sequence, ev.DataJson));
        using (canonical)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var o = options.Value;
            foreach (var c in candidates)
            {
                if (!Matches(c.FilterJson, canonical.RootElement, c.EndpointId)) continue;

                var active = c.Status == EndpointStatus.Active;
                var window = Policies.Retry(o, c.RetryPreset, c.RetryJson).Window;
                await InsertIfMissingAsync(ev, c.EndpointId, c.ApiVersion, active, now, ev.OccurredAt.UtcDateTime + window, now.AddDays(c.HoldDays), now.AddSeconds(o.Sweeper.DispatchLeaseSeconds), ct);
            }
        }

        var pending = await db.Deliveries.AsNoTracking()
                                         .Where(d => d.EventId == ev.Id && d.Status == DeliveryStatus.Pending)
                                         .Select(d => new DeliveryMessage(d.Id, d.TenantId, d.AttemptCount, Activity.Current != null ? Activity.Current.Id : null))
                                         .ToListAsync(ct);
        if (pending.Count > 0) await queue.EnqueueAsync(pending, null, ct);
        await tracker.MarkFannedOutAsync(ev.Id, ct); // only now may the relay stop worrying about this event
        logger.LogDebug("Event {EventId} ({Type}) fanned out to {Count} deliveries", ev.Id, ev.Type, pending.Count);
    }

    private bool Matches(string? filterJson, JsonElement canonical, Guid endpointId)
    {
        var (filter, error) = EventFilter.Parse(filterJson);
        if (filter is null)
        {
            // a stored filter was validated on save; if it is unreadable now, deliver rather than silently lose events
            logger.LogWarning("Endpoint {EndpointId} has an unreadable filter ({Error}); delivering without it", endpointId, error);
            return true;
        }
        return filter.Matches(canonical);
    }

    private async Task InsertIfMissingAsync(StoredEvent ev, Guid endpointId, string apiVersion, bool active, DateTime now, DateTime deadline, DateTime heldUntil, DateTime dispatchUntil, CancellationToken ct)
    {
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT webhooks.delivery (id, tenant_id, event_id, endpoint_id, event_type, aggregate_id, api_version, status, next_attempt_at, deadline_at, is_test, dispatch_until, held_reason, held_until, created_at, updated_at)
                SELECT {Guid.CreateVersion7()}, {ev.TenantId}, {ev.Id}, {endpointId}, {ev.Type}, {ev.AggregateId}, {apiVersion},
                       {(active ? DeliveryStatus.Pending : DeliveryStatus.Held)}, {now}, {deadline}, 0,
                       {(active ? dispatchUntil : (DateTime?)null)}, {(active ? null : "endpoint_not_active")}, {(active ? (DateTime?)null : heldUntil)}, {now}, {now}
                WHERE NOT EXISTS (SELECT 1 FROM webhooks.delivery WHERE event_id = {ev.Id} AND endpoint_id = {endpointId})
                """, ct);
            if (inserted > 0) WebhooksTelemetry.DeliveriesCreated.Add(1, new KeyValuePair<string, object?>("type", ev.Type));
        }
        catch (Exception e) when (IsDuplicateKey(e))
        {
            // another replica handled the same event at the same moment: exactly what the unique key is for
        }
    }

    private static bool IsDuplicateKey(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
                return true;
        }

        return false;
    }
}
