using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Messaging;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Events.Relay;

public sealed class RelayOptions
{
    public const string Section = "Events:Relay";

    public int BatchSize { get; set; } = 100;

    /// <summary>How long a claimed row stays reserved for this replica. If the replica dies, another one takes the rows after this.</summary>
    public int LeaseSeconds { get; set; } = 30;

    public int PollMilliseconds { get; set; } = 200;

    /// <summary>
    /// An event that was published but whose fan-out never reported back within this time is published again (broker lost it, or fan-out kept failing).
    /// Keep it above the topic's duplicate-detection window in production so the republish is not swallowed by the original's message id.
    /// </summary>
    public int StalledAfterSeconds { get; set; } = 120;

    public int StalledCheckSeconds { get; set; } = 5;
}

/// <summary>
/// Moves committed events from the outbox table to the broker. Several replicas can run at once: each claims a batch with
/// <c>UPDLOCK, READPAST</c> (rows locked by another replica are skipped, not waited for) and a lease. At-least-once by design:
/// a crash after publishing and before marking republishes the batch, which the topic's duplicate detection (MessageId = event id)
/// and the fan-out's unique key absorb.
/// </summary>
internal sealed class OutboxRelay(IServiceScopeFactory scopes, IEventTransport transport, IOptions<RelayOptions> options, ILogger<OutboxRelay> logger)
{
    private readonly string _me = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantScope>().SetSystem(); // cross-tenant job: only the worker SQL login may do this
        var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

        var claimed = await db.QueryAsync("""
            ;WITH next AS (
              SELECT TOP (@batch) id, event_id, tenant_id, claimed_by, claimed_until
              FROM events.outbox WITH (UPDLOCK, READPAST, ROWLOCK)
              WHERE published_at IS NULL AND (claimed_until IS NULL OR claimed_until < SYSUTCDATETIME())
              ORDER BY id)
            UPDATE next SET claimed_by = @me, claimed_until = DATEADD(SECOND, @lease, SYSUTCDATETIME())
            OUTPUT inserted.id, inserted.event_id, inserted.tenant_id
            """,
            r => new ClaimedRow(r.GetInt64(0), r.GetGuid(1), r.GetGuid(2)), ct,
            ("@batch", o.BatchSize), ("@me", _me), ("@lease", o.LeaseSeconds));
        if (claimed.Count == 0) return 0;
        var marked = await PublishAsync(db, claimed, markPublishedBy: _me, ct);
        logger.LogDebug("Relayed {Count} events to the broker", marked);
        return marked;
    }

    /// <summary>
    /// The safety net: events published a while ago that fan-out never confirmed are published again. Claiming is the UPDATE itself (it moves
    /// <c>published_at</c> to now, which restarts the stall timer), so replicas never pick the same rows.
    /// </summary>
    public async Task<int> RepublishStalledAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantScope>().SetSystem();
        var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

        var stalled = await db.QueryAsync("""
            ;WITH s AS (
              SELECT TOP (@batch) id, event_id, tenant_id, published_at
              FROM events.outbox WITH (UPDLOCK, READPAST, ROWLOCK)
              WHERE published_at IS NOT NULL AND fanned_out_at IS NULL AND published_at < DATEADD(SECOND, -@stalled, SYSUTCDATETIME())
              ORDER BY id)
            UPDATE s SET published_at = SYSUTCDATETIME()
            OUTPUT inserted.id, inserted.event_id, inserted.tenant_id
            """,
            r => new ClaimedRow(r.GetInt64(0), r.GetGuid(1), r.GetGuid(2)), ct,
            ("@batch", o.BatchSize), ("@stalled", o.StalledAfterSeconds));
        if (stalled.Count == 0) return 0;

        await PublishAsync(db, stalled, markPublishedBy: null, ct);
        EventsTelemetry.Republished.Add(stalled.Count);
        logger.LogWarning("Republished {Count} events that were published but never fanned out (broker loss or repeated fan-out failure)", stalled.Count);
        return stalled.Count;
    }

    private async Task<int> PublishAsync(EventsDbContext db, List<ClaimedRow> rows, string? markPublishedBy, CancellationToken ct)
    {
        var ids = rows.Select(c => c.EventId).ToList();
        var events = await db.Events.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.TenantId, e.Type, e.OccurredAt, e.TraceParent }).ToListAsync(ct);

        var batch = events.Select(e => new EventEnvelope(e.Id, e.TenantId, e.Type, new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc)), e.TraceParent)).ToList();
        await transport.PublishAsync(batch, ct);

        if (markPublishedBy is null) return rows.Count;
        var rowIds = rows.Select(c => c.Id).ToList();
        var marked = await db.Outbox.Where(r => rowIds.Contains(r.Id) && r.ClaimedBy == markPublishedBy)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishedAt, DateTime.UtcNow), ct);
        EventsTelemetry.Relayed.Add(marked);
        return marked;
    }
}

internal sealed class OutboxRelayService(OutboxRelay relay, IOptions<RelayOptions> options, ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        var nextStalledCheck = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var moved = await relay.RelayOnceAsync(stoppingToken);
                if (DateTime.UtcNow >= nextStalledCheck)
                {
                    await relay.RepublishStalledAsync(stoppingToken);
                    nextStalledCheck = DateTime.UtcNow.AddSeconds(options.Value.StalledCheckSeconds);
                }
                failures = 0;
                if (moved == 0) await Task.Delay(options.Value.PollMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // the broker or the database is unavailable: back off (up to 10 s) instead of spinning, keep the rows in the outbox
                failures++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 250 * Math.Pow(2, Math.Min(failures, 6))));
                logger.LogWarning(e, "Outbox relay failed ({Failures} in a row); retrying in {Delay}", failures, delay);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
