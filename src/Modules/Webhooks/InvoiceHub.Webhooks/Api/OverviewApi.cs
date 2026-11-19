using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Webhooks.Api;

/// <summary>
/// The numbers and the "needs attention" banner of the Overview screen. Everything is for the signed-in tenant (row-level security does the filtering),
/// excludes test traffic, and covers the last 24 hours against the 24 hours before for the trend.
/// </summary>
internal static class OverviewApi
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/portal/overview", Get).RequireAuthorization(Permissions.EndpointsRead).WithTags("Overview");

    private static async Task<IResult> Get(WebhooksDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var day = now.AddHours(-24);
        var previous = now.AddHours(-48);

        // deliveries by the day they were created, and how they ended
        var counts = await db.QueryAsync("""
            SELECT
              SUM(CASE WHEN created_at >= @day THEN 1 ELSE 0 END),
              SUM(CASE WHEN created_at < @day THEN 1 ELSE 0 END),
              SUM(CASE WHEN created_at >= @day AND status = 'succeeded' THEN 1 ELSE 0 END),
              SUM(CASE WHEN created_at >= @day AND status = 'succeeded' AND attempt_count = 1 THEN 1 ELSE 0 END),
              SUM(CASE WHEN created_at >= @day AND status = 'dead' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status = 'retry_scheduled' THEN 1 ELSE 0 END),
              SUM(CASE WHEN status = 'held' THEN 1 ELSE 0 END),
              MIN(CASE WHEN status = 'held' THEN created_at END)
            FROM webhooks.delivery WHERE is_test = 0 AND created_at >= @previous
            """, r => new
        {
            Created = Int(r, 0), CreatedBefore = Int(r, 1), Succeeded = Int(r, 2), FirstAttempt = Int(r, 3), Dead = Int(r, 4), Retrying = Int(r, 5), Held = Int(r, 6),
            OldestHeld = r.IsDBNull(7) ? (DateTime?)null : r.GetDateTime(7),
        }, ct, ("@day", day), ("@previous", previous));
        var c = counts[0];

        // how long receivers take to answer (attempts that got an answer)
        var latency = await db.QueryAsync("""
            SELECT AVG(CAST(duration_ms AS float)), MAX(p95) FROM (
              SELECT a.duration_ms, PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY a.duration_ms) OVER () AS p95
              FROM webhooks.delivery_attempt a JOIN webhooks.delivery d ON d.id = a.delivery_id
              WHERE a.started_at >= @day AND d.is_test = 0 AND a.status_code IS NOT NULL) x
            """, r => (Avg: r.IsDBNull(0) ? (double?)null : r.GetDouble(0), P95: r.IsDBNull(1) ? (double?)null : r.GetDouble(1)), ct, ("@day", day));

        var finished = c.Succeeded + c.Dead;
        var endpoints = await db.Endpoints.AsNoTracking().ToListAsync(ct);
        var backlog = await db.Deliveries.AsNoTracking().Where(d => d.Status == DeliveryStatus.Held).GroupBy(d => d.EndpointId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // what a person should look at first (UX-SPEC screen 1: only auto-disabled, circuit open for long, expiring keys)
        var attention = new List<object>();
        foreach (var e in endpoints.Where(e => e.Status == EndpointStatus.Disabled && e.DisabledReason is "auto_disabled" or "gone"))
            attention.Add(new { kind = e.DisabledReason == "gone" ? "gone" : "auto_disabled", endpointId = e.Id, name = e.Name, held = backlog.GetValueOrDefault(e.Id), lastStatus = (int?)null });
        foreach (var e in endpoints.Where(e => e.Status == EndpointStatus.Active && e.CircuitOpenUntil > now && e.FailingSince < now.AddHours(-1)))
            attention.Add(new { kind = "circuit_open", endpointId = e.Id, name = e.Name, held = 0, lastStatus = (int?)null });
        var expiring = await db.Secrets.AsNoTracking().Where(s => s.ValidUntil != null && s.ValidUntil > now && s.ValidUntil < now.AddHours(48)).Select(s => s.EndpointId).Distinct().ToListAsync(ct);
        foreach (var e in endpoints.Where(e => expiring.Contains(e.Id)))
            attention.Add(new { kind = "key_expiring", endpointId = e.Id, name = e.Name, held = 0, lastStatus = (int?)null });

        return Results.Ok(new
        {
            kpis = new
            {
                deliveries24h = c.Created,
                deliveriesPrevious24h = c.CreatedBefore,
                succeeded = c.Succeeded,
                successRate = finished == 0 ? (double?)null : Math.Round(100.0 * c.Succeeded / finished, 1),
                firstAttemptRate = c.Succeeded == 0 ? (double?)null : Math.Round(100.0 * c.FirstAttempt / c.Succeeded, 1),
                avgLatencyMs = latency[0].Avg is { } a ? (int)Math.Round(a) : (int?)null,
                p95LatencyMs = latency[0].P95 is { } p ? (int)Math.Round(p) : (int?)null,
                retrying = c.Retrying,
                dead = c.Dead,
                held = c.Held,
                oldestHeldSeconds = c.OldestHeld is { } o ? (int)(now - o).TotalSeconds : (int?)null,
            },
            attention,
            endpointCounts = new
            {
                total = endpoints.Count,
                healthy = endpoints.Count(e => EndpointViews.HealthOf(e, now) == "healthy"),
                needsAttention = attention.Count,
            },
        });
    }

    private static int Int(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
}
