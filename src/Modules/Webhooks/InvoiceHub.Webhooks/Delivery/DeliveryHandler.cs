using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Messaging;
using InvoiceHub.BuildingBlocks.Storage;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;
using InvoiceHub.Webhooks.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Delivery;

/// <summary>
/// One delivery attempt, start to finish. The message only says "look at delivery X"; SQL decides what to do (principle P3), so a duplicate,
/// early or stale message is harmless. Guard rails in order: state check, endpoint health (paused / disabled / circuit open), per-endpoint slot
/// (max 5 in flight, ASR-07), an exclusive in-flight claim, then sign, send, classify and record the result atomically.
/// Nothing here waits on a busy or sick receiver: a delivery that cannot go now is put back with a delay, so a slow endpoint never holds a worker.
/// </summary>
internal sealed class DeliveryHandler(
    WebhooksDbContext db,
    IEventReader events,
    IEventCatalog catalog,
    RequestFactory requests,
    IWebhookSender sender,
    IDeliveryQueue queue,
    IEventPublisher publisher,
    IAuditLog audit,
    ITenantScope tenantScope,
    IOptions<WebhooksOptions> options,
    TimeProvider clock,
    ILogger<DeliveryHandler> logger,
    IClaimCheckStore? claims = null) : IDeliveryMessageHandler
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static readonly string Me = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Extra time a worker is given on top of the endpoint timeout before the sweeper may take its delivery back.</summary>
    private static readonly TimeSpan ClaimGrace = TimeSpan.FromSeconds(30);

    public async Task HandleAsync(DeliveryMessage message, CancellationToken ct)
    {
        tenantScope.SetTenant(message.TenantId); // per-delivery tenant scope (P6): everything below sees this tenant only
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        var delivery = await db.Deliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == message.DeliveryId, ct)
            ?? throw new PoisonMessageException("delivery_not_found", $"Delivery {message.DeliveryId} does not exist for tenant {message.TenantId}.");

        // finished, held or already running elsewhere: a duplicate message, nothing to do
        if (delivery.Status is not (DeliveryStatus.Pending or DeliveryStatus.RetryScheduled)) return;
        // an early duplicate: the scheduled message (or the sweeper) will arrive when it is really due
        if (delivery.NextAttemptAt is { } due && due > now.AddSeconds(1)) return;

        var endpoint = await db.Endpoints.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, ct);
        if (endpoint is null || endpoint.DeletedAt is not null) { await EndAsync(delivery.Id, DeliveryStatus.Dead, "endpoint_deleted", now, ct); return; }
        if (endpoint.Status != EndpointStatus.Active)
        {
            await EndAsync(delivery.Id, DeliveryStatus.Held, endpoint.Status == EndpointStatus.Paused ? "endpoint_paused" : "endpoint_not_active", now, ct, endpoint.HoldDays);
            return;
        }
        if (now >= delivery.DeadlineAt) { await EndAsync(delivery.Id, DeliveryStatus.Dead, "window_exceeded", now, ct); return; }

        // circuit open (ASR-07): do not call a sick receiver; come back when the probe is due. No attempt is spent.
        if (endpoint.CircuitOpenUntil is { } open && open > now)
        {
            // the wait is kept in SQL (no broker message): the sweeper brings the line back in batches, spread over time, when the probe is due.
            // A sick receiver's backlog must never become broker traffic that slows everybody else (ASR-06, ASR-07)
            var waitingBehindCircuit = await BacklogAsync(endpoint.Id, ct);
            await DeferAsync(delivery, open.AddMilliseconds(Random.Shared.Next(0, 2000 + Math.Min(waitingBehindCircuit, 500) * 40)), "circuit_open", now, ct, viaBroker: false);
            return;
        }

        // the slot comes before anything else is read: a delivery that cannot run now must cost the shared database and broker as little as possible
        var slot = await TryAcquireSlotAsync(endpoint, delivery.Id, now, ct);
        if (slot is null)
        {
            // every one of the endpoint's slots is busy: do not queue behind the receiver. How long to wait depends on how long its attempts have already been running:
            // a receiver that is merely full answers in a moment (a short broker timer), one that is slow or hung is left alone and the wait is kept in SQL (the sweeper re-queues in batches)
            var busy = await BusyStateAsync(endpoint, now, ct);
            var spread = Math.Clamp(1 + busy.Waiting / 100.0, 1, 3); // a long line knocks less often, and not all at the same moment
            var delay = (o.Delivery.BusyRetryMilliseconds * (0.25 + Random.Shared.NextDouble() * 0.75) + busy.HeldFor.TotalMilliseconds * (0.5 + Random.Shared.NextDouble() * 0.5)) * spread;
            await DeferAsync(delivery, now.AddMilliseconds(Math.Min(delay, 30_000)), "endpoint_busy", now, ct, viaBroker: busy.HeldFor < TimeSpan.FromSeconds(1));
            return;
        }

        try
        {
            var ev = await events.GetAsync(delivery.EventId, ct);
            if (ev is null) { await EndAsync(delivery.Id, DeliveryStatus.Dead, "event_missing", now, ct); return; }

            var lease = now.AddSeconds(endpoint.TimeoutSeconds) + ClaimGrace;
            var claimed = await db.Deliveries
                .Where(d => d.Id == delivery.Id && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.RetryScheduled))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DeliveryStatus.InFlight)
                    .SetProperty(d => d.ClaimedBy, Me)
                    .SetProperty(d => d.ClaimedUntil, (DateTime?)lease)
                    .SetProperty(d => d.DispatchUntil, (DateTime?)null)
                    .SetProperty(d => d.UpdatedAt, now), ct);
            if (claimed == 0) return; // another replica got it first

            await AttemptAsync(delivery, endpoint, ev, ct);
        }
        finally
        {
            await ReleaseSlotAsync(endpoint.Id, slot.Value, delivery.Id);
        }
    }

    private async Task AttemptAsync(DeliveryEntity delivery, EndpointEntity endpoint, StoredEvent ev, CancellationToken ct)
    {
        var o = options.Value;
        var info = catalog.Find(ev.Type);

        // PII classes are always thin, whatever the endpoint says (ASR-12, defence in depth behind the API validation); "auto" means the event's own default
        // a big-result class (export, import report) is sent as a claim-check envelope unless the endpoint asked for ids only
        var claim = info?.Payload == "claim";
        var style = info?.ContainsPii == true ? PayloadStyle.Thin : endpoint.PayloadStyle == PayloadStyle.Thin ? PayloadStyle.Thin : claim ? PayloadStyle.ClaimCheck : PayloadStyle.Fat;

        // the version the delivery was created with (not the endpoint's current pin), and a download link minted for THIS attempt
        var data = VersionProjection.Project(delivery.ApiVersion, ev.Type, ev.DataJson);
        if (style == PayloadStyle.ClaimCheck) data = ClaimCheckEnvelope.Apply(data, claims, TimeSpan.FromMinutes(o.Delivery.ClaimLinkMinutes));
        var body = PayloadBuilder.Build(style, ev.Id, ev.IsTest, ev.Type, delivery.ApiVersion, ev.OccurredAt, ev.AggregateId, ev.Sequence, data, info?.ReferenceKeys ?? []);

        var startedAt = clock.GetUtcNow();
        var request = await requests.BuildAsync(endpoint, PayloadBuilder.EventId(ev.Id, ev.IsTest), startedAt, body, ct);

        SendResult result;
        Classification classification;
        if (request is null)
        {
            result = new SendResult(null, false, false, "The endpoint has no valid signing key.", null, null, TimeSpan.Zero);
            classification = new Classification(AttemptOutcome.Retry, false, false, "no_secret");
        }
        else
        {
            try
            {
                result = await sender.SendAsync(new SendRequest(new Uri(endpoint.Url), body, request.Headers, TimeSpan.FromSeconds(endpoint.TimeoutSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                // the host is shutting down mid-request: give the delivery back at once instead of leaving it for the lease to expire
                await ReleaseInFlightAsync(delivery.Id);
                throw;
            }
            classification = result.Blocked is not null
                ? new Classification(AttemptOutcome.Retry, false, false, "egress_blocked")
                : ResponseClassifier.Classify(result.StatusCode, result.TimedOut, result.NetworkError, result.RetryAfter, clock.GetUtcNow());
        }

        await RecordAsync(delivery, endpoint, ev, startedAt, result, classification, o, request is null ? null : HeaderMasking.Apply(endpoint, request.Headers), style);
    }

    // ---- recording the outcome: attempt row + delivery state + endpoint health (+ meta-event), one transaction ----

    private async Task RecordAsync(DeliveryEntity delivery, EndpointEntity endpoint, StoredEvent ev, DateTimeOffset startedAt, SendResult result, Classification c, WebhooksOptions o,
        Dictionary<string, string>? requestHeaders, string style)
    {
        // the HTTP call already happened: finish the bookkeeping even if the host is stopping
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = budget.Token;
        (DeliveryMessage Message, DateTimeOffset At)? followUp = null;
        var outcomeLabel = "";

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            followUp = null;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;

            // A failure is a read-modify-write of the endpoint's health, so it takes the endpoint's row lock first (up to 5 attempts finish at once): a no-op UPDATE holds it until commit.
            // A success needs no read: it resets the health to a constant. So it takes the lock only for its last statement, a few milliseconds, and one endpoint can run
            // at the speed of its receiver instead of at the speed of this bookkeeping.
            var success = c.Outcome == AttemptOutcome.Success;
            var resetHealth = false;
            EndpointEntity? loaded = null;
            if (!success)
            {
                await db.ExecuteAsync("UPDATE webhooks.endpoint SET updated_at = updated_at WHERE id = @id", ct, ("@id", endpoint.Id));
                loaded = await db.Endpoints.IgnoreQueryFilters().SingleAsync(e => e.Id == endpoint.Id, ct);
            }
            var d = await db.Deliveries.SingleAsync(x => x.Id == delivery.Id, ct);

            var attemptNo = d.AttemptCount + 1;
            var ours = d.Status == DeliveryStatus.InFlight && d.ClaimedBy == Me; // false if the sweeper reclaimed a very slow attempt
            outcomeLabel = c.Outcome == AttemptOutcome.Success ? "success" : c.Reason;

            db.Attempts.Add(new DeliveryAttemptEntity
            {
                TenantId = d.TenantId,
                DeliveryId = d.Id,
                AttemptNo = attemptNo,
                StartedAt = startedAt.UtcDateTime,
                DurationMs = (int)Math.Min(result.Duration.TotalMilliseconds, int.MaxValue),
                Outcome = outcomeLabel,
                StatusCode = result.StatusCode,
                ResponseSnippet = result.ResponseSnippet,
                Error = Trim(result.Error ?? (c.Outcome == AttemptOutcome.Success ? null : $"HTTP {result.StatusCode}"), 400),
                TraceId = Activity.Current?.TraceId.ToString(),
                RequestHeadersJson = requestHeaders is null ? null : JsonSerializer.Serialize(requestHeaders, WebJson),
                PayloadStyle = style,
                ResponseSize = result.ResponseSnippet?.Length,
            });

            if (ours)
            {
                d.AttemptCount = attemptNo;
                d.LastStatusCode = result.StatusCode;
                d.LastError = Trim(result.Error ?? (c.Outcome == AttemptOutcome.Success ? null : $"HTTP {result.StatusCode}"), 400);
                d.ClaimedBy = null;
                d.ClaimedUntil = null;
                d.UpdatedAt = now;
                d.FirstAttemptAt ??= startedAt.UtcDateTime;

                if (success)
                {
                    d.Status = DeliveryStatus.Succeeded;
                    d.CompletedAt = now;
                    d.NextAttemptAt = null;
                    resetHealth = !d.IsTest; // applied as one statement after the rows above are written (CircuitBreaker.OnSuccess is a constant: healthy)
                }
            }

            if (ours && !success)
            {
                var ep = loaded!;
                var health = Health.Of(ep);
                switch (c.Outcome)
                {
                    case AttemptOutcome.Gone:
                        // 410: the receiver says this endpoint is gone. Keep the delivery (held), stop sending to it, tell the tenant (once: a second attempt that was
                        // already on the wire when the first one switched the endpoint off finds it disabled and only holds its own delivery).
                        Hold(d, "endpoint_gone", now, ep.HoldDays);
                        ep.LastFailureAt = now;
                        if (ep.Status == EndpointStatus.Active)
                        {
                            ep.Status = EndpointStatus.Disabled;
                            ep.DisabledReason = "gone";
                            await PublishDisabledAsync(tx, ep, "gone", health, now, ct);
                        }
                        break;

                    default:
                        ep.LastFailureAt = now;
                        if (ep.Status != EndpointStatus.Active)
                        {
                            // another attempt switched the endpoint off (or someone paused it) while this one was on the wire: this delivery waits with the others,
                            // and the owner is not told a second time
                            Hold(d, ep.Status == EndpointStatus.Paused ? "endpoint_paused" : "endpoint_not_active", now, ep.HoldDays);
                            break;
                        }
                        var next = CircuitBreaker.OnFailure(health, c.CountsTowardBreaker, now.ToUtc(), o.Delivery.ProbeScale, ep.CircuitFailures);
                        if (!d.IsTest) Health.Apply(ep, next);

                        var decision = c.CountsTowardAutoDisable ? AutoDisable.Evaluate(Policies.AutoDisable(ep), next, now.ToUtc()) : AutoDisableDecision.None;
                        if (decision == AutoDisableDecision.Disable)
                        {
                            Hold(d, "endpoint_auto_disabled", now, ep.HoldDays);
                            ep.Status = EndpointStatus.Disabled;
                            ep.DisabledReason = "auto_disabled";
                            await PublishDisabledAsync(tx, ep, "auto_disabled", next, now, ct);
                            break;
                        }
                        if (decision is AutoDisableDecision.Warn25 or AutoDisableDecision.Warn50 or AutoDisableDecision.Warn75)
                        {
                            WebhooksTelemetry.Warnings.Add(1, new KeyValuePair<string, object?>("level", decision.ToString()));
                            logger.LogWarning("Endpoint {EndpointId} reached {Level} of its auto-disable threshold ({Failures} consecutive failures)", ep.Id, decision, next.ConsecutiveFailures);
                            await PublishDegradedAsync(tx, ep, decision, next, now, ct);
                        }

                        var policy = Policies.Retry(o, ep.RetryPreset, ep.RetryJson);
                        var sinceReplay = attemptNo - d.AttemptFloor; // a replay starts the retry schedule over; the log keeps counting
                        if (!policy.HasMoreAttempts(sinceReplay))
                        {
                            // the endpoint's own limit ("give up after N attempts") is reached: dead, visible and replayable
                            d.Status = DeliveryStatus.Dead;
                            d.CompletedAt = now;
                            d.NextAttemptAt = null;
                            d.HeldReason = "attempts_exhausted";
                            WebhooksTelemetry.Terminal.Add(1, new KeyValuePair<string, object?>("state", "dead"));
                            break;
                        }

                        var nextAt = now + policy.NextDelay(sinceReplay, c.RetryAfter, Random.Shared);
                        if (next.CircuitOpenUntil is { } reopen && reopen.UtcDateTime > nextAt) nextAt = reopen.UtcDateTime;

                        if (nextAt > d.DeadlineAt)
                        {
                            d.Status = DeliveryStatus.Dead;
                            d.CompletedAt = now;
                            d.NextAttemptAt = null;
                            d.HeldReason = "window_exceeded";
                            WebhooksTelemetry.Terminal.Add(1, new KeyValuePair<string, object?>("state", "dead"));
                            break;
                        }

                        d.Status = DeliveryStatus.RetryScheduled;
                        d.NextAttemptAt = nextAt;
                        var scheduled = nextAt - now <= IDeliveryQueue.MaxSchedulingHorizon; // the broker only holds short timers; longer waits belong to the sweeper
                        d.DispatchUntil = scheduled ? nextAt.AddSeconds(options.Value.Sweeper.DispatchLeaseSeconds) : null;
                        if (scheduled) followUp = (new DeliveryMessage(d.Id, d.TenantId, attemptNo, Activity.Current?.Id), new DateTimeOffset(DateTime.SpecifyKind(nextAt, DateTimeKind.Utc)));
                        break;
                }
                ep.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            if (ours && success)
            {
                await db.ExecuteAsync(resetHealth
                    ? "UPDATE webhooks.endpoint SET consecutive_failures = 0, failing_since = NULL, circuit_open_until = NULL, circuit_probe_step = 0, last_success_at = @now, updated_at = @now WHERE id = @id"
                    : "UPDATE webhooks.endpoint SET updated_at = @now WHERE id = @id", ct, ("@now", now), ("@id", endpoint.Id));
            }
            await tx.CommitAsync(ct);
        });

        WebhooksTelemetry.Attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcomeLabel));
        WebhooksTelemetry.AttemptDuration.Record(result.Duration.TotalMilliseconds);
        if (delivery.AttemptCount == 0 && !delivery.IsTest)
            WebhooksTelemetry.CommitToFirstAttempt.Record(Math.Max(0, (startedAt - ev.OccurredAt).TotalSeconds));
        logger.LogInformation("Delivery {DeliveryId} attempt {Attempt} to endpoint {EndpointId}: {Outcome} ({Status}) in {Ms:0} ms {Error}",
            delivery.Id, delivery.AttemptCount + 1, endpoint.Id, outcomeLabel, result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "no response", result.Duration.TotalMilliseconds, result.Error);

        if (followUp is { } f) await TryEnqueueAsync(f.Message, f.At, ct);
    }

    private static void Hold(DeliveryEntity d, string reason, DateTime now, int holdDays)
    {
        d.Status = DeliveryStatus.Held;
        d.HeldReason = reason;
        d.HeldUntil = now.AddDays(holdDays);
        d.NextAttemptAt = null;
        d.DispatchUntil = null;
        WebhooksTelemetry.Terminal.Add(1, new KeyValuePair<string, object?>("state", "held"));
    }

    /// <summary>The platform tells the tenant through its own pipeline: the meta-event is written in the same transaction, so it cannot be lost either.</summary>
    private async Task PublishDisabledAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, EndpointEntity ep, string reason, EndpointHealth health, DateTime now, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(new
        {
            endpoint_id = $"ep_{ep.Id:N}",
            name = ep.Name,
            url = ep.Url,
            reason,
            consecutive_failures = health.ConsecutiveFailures,
            failing_since = health.FailingSince,
        }, WebJson);
        await audit.WriteAsync(tx.GetDbTransaction(), new AuditEntry("endpoint.disabled", "endpoint", ep.Id.ToString(), ep.Name, new { reason, consecutiveFailures = health.ConsecutiveFailures }, Actor: "system"), ct);
        await publisher.PublishAsync(tx.GetDbTransaction(), new DomainEvent("webhook.endpoint.auto_disabled", $"ep_{ep.Id:N}", new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeMilliseconds(), data, new DateTimeOffset(now, TimeSpan.Zero)), ct);
    }

    /// <summary>"Your endpoint is at 50 % of the failures that would switch it off." Written in the same transaction, so the warning cannot be lost either.</summary>
    private async Task PublishDegradedAsync(IDbContextTransaction tx, EndpointEntity ep, AutoDisableDecision level, EndpointHealth health, DateTime now, CancellationToken ct)
    {
        var percent = level switch { AutoDisableDecision.Warn25 => 25, AutoDisableDecision.Warn50 => 50, _ => 75 };
        var data = JsonSerializer.Serialize(new
        {
            endpoint_id = $"ep_{ep.Id:N}",
            name = ep.Name,
            level_percent = percent,
            consecutive_failures = health.ConsecutiveFailures,
            auto_disable_at_failures = ep.AutodisableFailures,
            failing_since = health.FailingSince,
        }, WebJson);
        await publisher.PublishAsync(tx.GetDbTransaction(), new DomainEvent("webhook.endpoint.degraded", $"ep_{ep.Id:N}", new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeMilliseconds(), data, new DateTimeOffset(now, TimeSpan.Zero)), ct);
    }

    /// <summary>How many deliveries of this endpoint are waiting, counted up to 500 (a bounded read, however long the line is).</summary>
    private Task<int> BacklogAsync(Guid endpointId, CancellationToken ct) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM (SELECT TOP (500) 1 AS x FROM webhooks.delivery WHERE endpoint_id = @e AND status IN ('pending', 'retry_scheduled')) AS t", ct, ("@e", endpointId));

    private readonly record struct BusyState(TimeSpan HeldFor, int Waiting);

    /// <summary>How long the oldest attempt on this endpoint has been running (a slot lease starts at the moment of acquisition and lasts timeout + grace) and how many deliveries wait behind it.</summary>
    private async Task<BusyState> BusyStateAsync(EndpointEntity endpoint, DateTime now, CancellationToken ct)
    {
        var rows = await db.QueryAsync("""
            SELECT (SELECT MIN(leased_until) FROM webhooks.endpoint_slot WHERE endpoint_id = @e AND slot_no <= @max AND delivery_id IS NOT NULL AND leased_until > SYSUTCDATETIME()),
                   (SELECT COUNT(*) FROM (SELECT TOP (500) 1 AS x FROM webhooks.delivery WHERE endpoint_id = @e AND status IN ('pending', 'retry_scheduled')) AS t)
            """, r => (Oldest: r.IsDBNull(0) ? (DateTime?)null : r.GetDateTime(0), Waiting: r.GetInt32(1)), ct, ("@e", endpoint.Id), ("@max", endpoint.MaxInflight));
        var (oldest, waiting) = rows[0];
        var heldFor = oldest is { } until ? TimeSpan.FromSeconds(endpoint.TimeoutSeconds) + ClaimGrace - (until - now) : TimeSpan.Zero;
        return new BusyState(heldFor < TimeSpan.Zero ? TimeSpan.Zero : heldFor, waiting);
    }

    // ---- small state moves ----

    private Task<int> EndAsync(Guid id, string status, string reason, DateTime now, CancellationToken ct, int holdDays = 7)
    {
        var holdUntil = now.AddDays(holdDays);
        WebhooksTelemetry.Terminal.Add(1, new KeyValuePair<string, object?>("state", status));
        return db.Deliveries
            .Where(d => d.Id == id && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.RetryScheduled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.HeldReason, reason)
                .SetProperty(d => d.HeldUntil, status == DeliveryStatus.Held ? holdUntil : (DateTime?)null)
                .SetProperty(d => d.CompletedAt, status == DeliveryStatus.Dead ? now : (DateTime?)null)
                .SetProperty(d => d.DispatchUntil, (DateTime?)null)
                .SetProperty(d => d.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Puts the delivery back for later without spending an attempt. With <paramref name="viaBroker"/> a timer message is queued when the wait is short; without it the wait lives
    /// in SQL only and the sweeper brings the delivery back when it is due (cheap for the broker: one batch instead of one timer per delivery).
    /// </summary>
    private async Task DeferAsync(DeliveryEntity delivery, DateTime notBefore, string reason, DateTime now, CancellationToken ct, bool viaBroker = true)
    {
        WebhooksTelemetry.Deferred.Add(1, new KeyValuePair<string, object?>("reason", reason));
        var scheduled = viaBroker && notBefore - now <= IDeliveryQueue.MaxSchedulingHorizon;
        var dispatchUntil = scheduled ? notBefore.AddSeconds(options.Value.Sweeper.DispatchLeaseSeconds) : (DateTime?)null;
        var updated = await db.Deliveries
            .Where(d => d.Id == delivery.Id && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.RetryScheduled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.NextAttemptAt, (DateTime?)notBefore)
                .SetProperty(d => d.DispatchUntil, dispatchUntil)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (updated > 0 && scheduled)
            await TryEnqueueAsync(new DeliveryMessage(delivery.Id, delivery.TenantId, delivery.AttemptCount, Activity.Current?.Id),
                new DateTimeOffset(DateTime.SpecifyKind(notBefore, DateTimeKind.Utc)), ct);
    }

    /// <summary>
    /// The state is already saved, so a failed enqueue must not fail the message: the sweeper re-queues anything whose
    /// <c>dispatch_until</c> lapsed. (This is also what recovers timers lost when the broker restarts: the emulator has no persistence.)
    /// </summary>
    private async Task TryEnqueueAsync(DeliveryMessage message, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            await queue.EnqueueAsync([message], at, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not queue the timer for delivery {DeliveryId}; the sweeper will pick it up", message.DeliveryId);
        }
    }

    private async Task ReleaseInFlightAsync(Guid deliveryId)
    {
        await db.ExecuteAsync("""
            UPDATE webhooks.delivery SET status = 'retry_scheduled', next_attempt_at = SYSUTCDATETIME(), claimed_by = NULL, claimed_until = NULL, dispatch_until = NULL, updated_at = SYSUTCDATETIME()
            WHERE id = @id AND status = 'in_flight' AND claimed_by = @me
            """, CancellationToken.None, ("@id", deliveryId), ("@me", Me));
    }

    // ---- per-endpoint concurrency (ASR-07) ----

    private async Task<int?> TryAcquireSlotAsync(EndpointEntity endpoint, Guid deliveryId, DateTime now, CancellationToken ct)
    {
        var lease = now.AddSeconds(endpoint.TimeoutSeconds) + ClaimGrace;
        var slots = await db.QueryAsync("""
            UPDATE TOP (1) s SET delivery_id = @delivery, leased_until = @until
            OUTPUT inserted.slot_no
            FROM webhooks.endpoint_slot AS s WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE s.endpoint_id = @endpoint AND s.slot_no <= @max AND (s.delivery_id IS NULL OR s.leased_until < SYSUTCDATETIME())
            """, r => r.GetInt32(0), ct, ("@delivery", deliveryId), ("@until", lease), ("@endpoint", endpoint.Id), ("@max", endpoint.MaxInflight));
        return slots.Count == 0 ? null : slots[0];
    }

    private Task<int> ReleaseSlotAsync(Guid endpointId, int slot, Guid deliveryId) =>
        db.ExecuteAsync("UPDATE webhooks.endpoint_slot SET delivery_id = NULL, leased_until = NULL WHERE endpoint_id = @e AND slot_no = @n AND delivery_id = @d",
            CancellationToken.None, ("@e", endpointId), ("@n", slot), ("@d", deliveryId));

    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

internal static class Health
{
    public static EndpointHealth Of(EndpointEntity e) => new(e.ConsecutiveFailures, Utc(e.FailingSince), Utc(e.CircuitOpenUntil), e.CircuitProbeStep);

    public static void Apply(EndpointEntity e, EndpointHealth h)
    {
        e.ConsecutiveFailures = h.ConsecutiveFailures;
        e.FailingSince = h.FailingSince?.UtcDateTime;
        e.CircuitOpenUntil = h.CircuitOpenUntil?.UtcDateTime;
        e.CircuitProbeStep = h.ProbeStep;
    }

    private static DateTimeOffset? Utc(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));

    public static DateTimeOffset ToUtc(this DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}

/// <summary>The settings an endpoint has chosen, turned into the logic objects delivery runs on.</summary>
internal static class Policies
{
    /// <summary>The retry policy of an endpoint. The global override (used by integration tests and workshops) wins over the endpoint's own choice.</summary>
    public static RetryPolicy Retry(WebhooksOptions options, string preset, string? customJson) =>
        string.IsNullOrWhiteSpace(options.Delivery.ForceRetryPreset) ? RetryPolicy.For(preset, customJson) : RetryPolicy.FromName(options.Delivery.ForceRetryPreset);

    public static AutoDisableRule AutoDisable(EndpointEntity e) =>
        new(e.AutodisableFailures, TimeSpan.FromHours(e.AutodisableHours), TimeSpan.FromDays(e.AutodisableDays));
}
