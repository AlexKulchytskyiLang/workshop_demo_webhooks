using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Messaging;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Webhooks.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Delivery;

/// <summary>
/// The safety net under the broker. SQL says what is due; the broker is only a fast path. The sweeper therefore:
/// (1) reclaims attempts whose worker died (the lease ran out), (2) closes deliveries whose retry window or hold period is over,
/// (3) queues everything that is due but has no live message: retries longer than the broker's 30 min timer horizon, timers lost when the broker
/// restarted (the emulator keeps nothing), and anything whose enqueue failed. Several replicas run it at once: <c>READPAST</c> keeps them apart.
/// </summary>
internal sealed class RetrySweeper(IServiceScopeFactory scopes, IDeliveryQueue queue, IOptions<WebhooksOptions> options, ILogger<RetrySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queued = await SweepOnceAsync(stoppingToken);
                failures = 0;
                if (queued == 0) await Task.Delay(options.Value.Sweeper.PollMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                failures++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 500 * Math.Pow(2, Math.Min(failures, 5))));
                logger.LogWarning(e, "Retry sweeper failed ({Failures} in a row); retrying in {Delay}", failures, delay);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var o = options.Value.Sweeper;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantScope>().SetSystem(); // cross-tenant job: only the worker SQL login may do this
        var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();

        // 1. a worker died mid-attempt: its lease ran out, so the delivery goes back to the due list (the attempt may or may not have reached the receiver:
        //    at-least-once, and the receiver deduplicates on webhook-id)
        var reclaimed = await db.ExecuteAsync("""
            UPDATE webhooks.delivery
            SET status = 'retry_scheduled', next_attempt_at = SYSUTCDATETIME(), claimed_by = NULL, claimed_until = NULL, dispatch_until = NULL, updated_at = SYSUTCDATETIME()
            WHERE status = 'in_flight' AND claimed_until < SYSUTCDATETIME()
            """, ct);

        // 2. windows that ran out
        var dead = await db.ExecuteAsync("""
            UPDATE webhooks.delivery
            SET status = 'dead', held_reason = CASE WHEN status = 'held' THEN 'hold_expired' ELSE 'window_exceeded' END,
                completed_at = SYSUTCDATETIME(), next_attempt_at = NULL, dispatch_until = NULL, updated_at = SYSUTCDATETIME()
            WHERE (status IN ('pending', 'retry_scheduled') AND deadline_at < SYSUTCDATETIME())
               OR (status = 'held' AND held_until < SYSUTCDATETIME())
            """, ct);

        // 3. due work with no live message: claim it for dispatch (lease), then queue it
        var due = await db.QueryAsync("""
            ;WITH due AS (
              SELECT TOP (@batch) id, tenant_id, attempt_count, dispatch_until
              FROM webhooks.delivery WITH (UPDLOCK, READPAST, ROWLOCK)
              WHERE status IN ('pending', 'retry_scheduled') AND next_attempt_at <= SYSUTCDATETIME()
                AND (dispatch_until IS NULL OR dispatch_until < SYSUTCDATETIME())
              ORDER BY next_attempt_at)
            UPDATE due SET dispatch_until = DATEADD(SECOND, @lease, SYSUTCDATETIME())
            OUTPUT inserted.id, inserted.tenant_id, inserted.attempt_count
            """,
            r => new DueDelivery(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2)), ct,
            ("@batch", o.BatchSize), ("@lease", o.DispatchLeaseSeconds));

        if (due.Count > 0)
        {
            await queue.EnqueueAsync(due.Select(d => new DeliveryMessage(d.Id, d.TenantId, d.AttemptCount)).ToList(), null, ct);
        }

        if (reclaimed + dead + due.Count > 0)
        {
            WebhooksTelemetry.Swept.Add(reclaimed + due.Count);
            logger.LogInformation("Sweeper: reclaimed {Reclaimed} abandoned attempts, closed {Dead} expired deliveries, re-queued {Queued} due deliveries", reclaimed, dead, due.Count);
        }
        return due.Count;
    }
}
