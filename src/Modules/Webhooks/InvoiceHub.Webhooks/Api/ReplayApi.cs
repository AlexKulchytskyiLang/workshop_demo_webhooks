using System.Globalization;
using System.Security.Claims;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Delivery;
using InvoiceHub.Webhooks.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Api;

internal sealed record ReplaySelectionInput(Guid? EndpointId, string[]? States, string? EventType, DateTime? From, DateTime? To, string? Confirm = null, int? RatePerSecond = null);

internal sealed record GuidedResumeInput(string? Mode, int? RecentHours);

/// <summary>
/// Replay and resume (UX-SPEC D3 and D9). Replaying keeps the SAME <c>webhook-id</c>: a receiver that already handled the event ignores it, a receiver that missed it finally gets it.
/// Every replay is a deliberate, bounded act: a dry run first, a typed confirmation that names the number of events, a pace, and a daily cap per tenant.
/// A job is nothing more than a set of deliveries put back in the queue with spaced due times, so the normal delivery path (circuit breaker, per-endpoint limits, retries) protects the receiver.
/// </summary>
internal static class ReplayApi
{
    private static readonly string[] ReplayableStates = [DeliveryStatus.Dead, DeliveryStatus.Held, DeliveryStatus.Succeeded];
    private static readonly string[] ResumeModes = ["deliver_all", "only_recent", "discard"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/portal/replay").WithTags("Replay");
        g.MapGet("/overview", Overview).RequireAuthorization(Permissions.EndpointsRead);
        g.MapPost("/preview", Preview).RequireAuthorization(Permissions.ReplayRun);
        g.MapPost("/jobs", StartJob).RequireAuthorization(Permissions.ReplayRun);
        g.MapGet("/jobs", Jobs).RequireAuthorization(Permissions.EndpointsRead);
        g.MapPost("/jobs/{id:guid}/cancel", Cancel).RequireAuthorization(Permissions.ReplayRun);
        app.MapPost("/api/portal/endpoints/{id:guid}/resume-guided", ResumeGuided).RequireAuthorization(Permissions.ReplayRun).WithTags("Replay");
    }

    // ------------------------------------------------------------------ what is waiting

    private static async Task<IResult> Overview(WebhooksDbContext db, IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var o = options.Value.Replay;
        var endpoints = await db.Endpoints.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var counts = await db.QueryAsync("""
            SELECT endpoint_id, status, COUNT(*), MIN(held_until), MIN(created_at)
            FROM webhooks.delivery WHERE status IN ('held', 'dead') AND is_test = 0 GROUP BY endpoint_id, status
            """, r => (Endpoint: r.GetGuid(0), Status: r.GetString(1), Count: r.GetInt32(2), HeldUntil: r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3), Oldest: r.GetDateTime(4)), ct);

        var used = await CapUsedAsync(db, now, ct);
        return Results.Ok(new
        {
            cap = new { limit = o.DailyCap, used, remaining = Math.Max(0, o.DailyCap - used) },
            ratePerSecond = o.DefaultRatePerSecond,
            testFreshMinutes = o.TestFreshMinutes,
            recentHours = o.RecentHours,
            endpoints = endpoints.Select(e =>
            {
                var held = counts.FirstOrDefault(c => c.Endpoint == e.Id && c.Status == DeliveryStatus.Held);
                var dead = counts.FirstOrDefault(c => c.Endpoint == e.Id && c.Status == DeliveryStatus.Dead);
                return new
                {
                    e.Id, e.Name, e.Status, e.DisabledReason, health = EndpointViews.HealthOf(e, now),
                    held = held.Count, dead = dead.Count,
                    holdExpiresAt = EndpointViews.Utc(held.HeldUntil),
                    oldestHeld = EndpointViews.Utc(held.Count > 0 ? held.Oldest : null),
                    lastTestPassedAt = EndpointViews.Utc(e.LastTestPassedAt),
                    testFresh = e.LastTestPassedAt is { } t && t > now.AddMinutes(-o.TestFreshMinutes),
                };
            }),
        });
    }

    private static async Task<int> CapUsedAsync(WebhooksDbContext db, DateTime now, CancellationToken ct) =>
        await db.ScalarAsync<int>("SELECT COALESCE(SUM(total), 0) FROM webhooks.replay_job WHERE created_at >= @since AND cancelled_at IS NULL AND kind <> 'resume'", ct, ("@since", now.AddHours(-24)));

    // ------------------------------------------------------------------ dry run

    private static async Task<IResult> Preview(ReplaySelectionInput input, WebhooksDbContext db, IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var (selection, problem) = await ResolveAsync(input, db, ct);
        if (problem is not null) return problem;
        var now = clock.GetUtcNow().UtcDateTime;
        var o = options.Value.Replay;
        var (where, parameters) = Where(selection!);

        var byState = await db.QueryAsync($"SELECT status, COUNT(*), MIN(created_at), MAX(created_at) FROM webhooks.delivery WHERE {where} GROUP BY status",
            r => (Status: r.GetString(0), Count: r.GetInt32(1), Oldest: r.GetDateTime(2), Newest: r.GetDateTime(3)), ct, parameters);
        var byType = await db.QueryAsync($"SELECT TOP (8) event_type, COUNT(*) FROM webhooks.delivery WHERE {where} GROUP BY event_type ORDER BY COUNT(*) DESC",
            r => new { type = r.GetString(0), count = r.GetInt32(1) }, ct, parameters);

        var count = byState.Sum(s => s.Count);
        var used = await CapUsedAsync(db, now, ct);
        var rate = Math.Clamp(input.RatePerSecond ?? o.DefaultRatePerSecond, 1, 50);
        var endpoint = selection!.Endpoint;
        return Results.Ok(new
        {
            count,
            byState = byState.ToDictionary(s => s.Status, s => s.Count),
            byType,
            oldest = EndpointViews.Utc(byState.Count == 0 ? null : byState.Min(s => s.Oldest)),
            newest = EndpointViews.Utc(byState.Count == 0 ? null : byState.Max(s => s.Newest)),
            ratePerSecond = rate,
            estimatedSeconds = (int)Math.Ceiling(count / (double)rate),
            cap = new { limit = o.DailyCap, used, remaining = Math.Max(0, o.DailyCap - used), exceeded = used + count > o.DailyCap },
            endpoint = new { endpoint.Id, endpoint.Name, endpoint.Status, canReceive = endpoint.Status == EndpointStatus.Active },
            confirmPhrase = $"REPLAY {count}",
            sameWebhookId = true,
            note = "Each event is sent again with the same webhook-id. A receiver that already processed it should ignore it; one that missed it receives it now.",
        });
    }

    // ------------------------------------------------------------------ start, list, cancel

    private static async Task<IResult> StartJob(
        ReplaySelectionInput input, ClaimsPrincipal user, WebhooksDbContext db, IAuditLog audit, IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var (selection, problem) = await ResolveAsync(input, db, ct);
        if (problem is not null) return problem;
        var endpoint = selection!.Endpoint;
        if (endpoint.Status != EndpointStatus.Active) return Problems.Conflict("endpoint_not_active", $"“{endpoint.Name}” is {endpoint.Status}. Resume it first (Replay center, Held tab), then replay.");

        var now = clock.GetUtcNow().UtcDateTime;
        var o = options.Value.Replay;
        var (where, parameters) = Where(selection);
        var count = await db.ScalarAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery WHERE {where}", ct, parameters);
        if (count == 0) return Problems.Conflict("nothing_to_replay", "No events match. Widen the filter.");

        var expected = $"REPLAY {count}";
        if (!string.Equals(input.Confirm?.Trim(), expected, StringComparison.Ordinal))
            return Problems.Create(StatusCodes.Status400BadRequest, "confirmation_required", "Confirmation needed", $"Type {expected} to confirm. {count} events will be sent again.");

        var used = await CapUsedAsync(db, now, ct);
        if (used + count > o.DailyCap)
            return Problems.Conflict("replay_cap_reached", $"A tenant may replay {o.DailyCap} events per 24 hours. {used} are used, this job needs {count}. Narrow the filter or try again tomorrow.");

        var rate = Math.Clamp(input.RatePerSecond ?? o.DefaultRatePerSecond, 1, 50);
        var job = NewJob(endpoint, "replay", "replay", string.Join(',', selection.States), selection.EventType, selection.From, selection.To, rate, user, now);
        var released = await ReleaseAsync(db, audit, endpoint, job, where, parameters, now, options.Value,
            new AuditEntry("replay.started", "replay_job", job.Id.ToString(), endpoint.Name, new { count, states = selection.States, eventType = selection.EventType, from = selection.From, to = selection.To, ratePerSecond = rate }), ct);
        return Results.Accepted($"/api/portal/replay/jobs", new { jobId = job.Id, released, ratePerSecond = rate });
    }

    internal static async Task<IResult> ReplaySingle(Guid id, ClaimsPrincipal user, WebhooksDbContext db, IAuditLog audit, IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var d = await db.Deliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Problems.NotFound("delivery_not_found", "No such delivery.");
        if (!ReplayableStates.Contains(d.Status))
            return Problems.Conflict("not_replayable", d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.RetryScheduled || d.Status == DeliveryStatus.InFlight
                ? "This event is still being delivered. Wait for it to finish, or replay it afterwards."
                : "This event cannot be replayed.");

        var endpoint = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == d.EndpointId, ct);
        if (endpoint is null) return Problems.Conflict("endpoint_gone", "The endpoint of this event no longer exists.");
        if (endpoint.Status != EndpointStatus.Active) return Problems.Conflict("endpoint_not_active", $"“{endpoint.Name}” is {endpoint.Status}. Resume it first, then replay.");

        var now = clock.GetUtcNow().UtcDateTime;
        var used = await CapUsedAsync(db, now, ct);
        if (used + 1 > options.Value.Replay.DailyCap) return Problems.Conflict("replay_cap_reached", $"A tenant may replay {options.Value.Replay.DailyCap} events per 24 hours.");

        var job = NewJob(endpoint, "single", "replay", d.Status, d.EventType, null, null, 1, user, now);
        var where = "id = @single AND status = @state";
        (string, object?)[] parameters = [("@single", id), ("@state", d.Status)];
        var released = await ReleaseAsync(db, audit, endpoint, job, where, parameters, now, options.Value,
            new AuditEntry("replay.single", "delivery", id.ToString(), endpoint.Name, new { eventType = d.EventType, previousState = d.Status, webhookId = Core.PayloadBuilder.EventId(d.EventId, false) }), ct);
        return released == 0 ? Problems.Conflict("not_replayable", "The event changed state meanwhile. Reload and try again.") : Results.Accepted(value: new { jobId = job.Id, sameWebhookId = true });
    }

    private static async Task<IResult> Jobs(WebhooksDbContext db, int? limit, CancellationToken ct)
    {
        var rows = await db.QueryAsync("""
            SELECT TOP (@take) j.id, j.endpoint_id, e.name, j.kind, j.mode, j.states, j.event_type, j.total, j.discarded, j.rate_per_second, j.created_by, j.created_at, j.cancelled_at,
                   SUM(CASE WHEN d.status IN ('pending', 'retry_scheduled', 'in_flight') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN d.status = 'succeeded' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN d.status IN ('dead', 'held') THEN 1 ELSE 0 END)
            FROM webhooks.replay_job j
            JOIN webhooks.endpoint e ON e.id = j.endpoint_id
            LEFT JOIN webhooks.delivery d ON d.replay_job_id = j.id
            GROUP BY j.id, j.endpoint_id, e.name, j.kind, j.mode, j.states, j.event_type, j.total, j.discarded, j.rate_per_second, j.created_by, j.created_at, j.cancelled_at
            ORDER BY j.created_at DESC
            """,
            r =>
            {
                var remaining = r.IsDBNull(13) ? 0 : r.GetInt32(13);
                var cancelled = !r.IsDBNull(12);
                return new
                {
                    id = r.GetGuid(0), endpointId = r.GetGuid(1), endpointName = r.GetString(2), kind = r.GetString(3), mode = r.GetString(4), states = r.GetString(5),
                    eventType = r.IsDBNull(6) ? null : r.GetString(6), total = r.GetInt32(7), discarded = r.GetInt32(8), ratePerSecond = r.GetInt32(9), createdBy = r.GetString(10),
                    createdAt = EndpointViews.Utc(r.GetDateTime(11)), cancelledAt = EndpointViews.Utc(cancelled ? r.GetDateTime(12) : null),
                    status = cancelled ? "cancelled" : remaining > 0 ? "running" : "completed",
                    remaining, delivered = r.IsDBNull(14) ? 0 : r.GetInt32(14), failed = r.IsDBNull(15) ? 0 : r.GetInt32(15),
                };
            }, ct, ("@take", Math.Clamp(limit ?? 50, 1, 200)));
        return Results.Ok(new { items = rows });
    }

    /// <summary>Stops a running job: everything not yet attempted goes back to the state it was in, nothing already sent is undone.</summary>
    private static async Task<IResult> Cancel(Guid id, WebhooksDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var job = await db.ReplayJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return Problems.NotFound("job_not_found", "No such replay job.");
        if (job.CancelledAt is not null) return Problems.Conflict("already_cancelled", "This job was already cancelled.");

        var now = clock.GetUtcNow().UtcDateTime;
        var reverted = 0;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            reverted = await db.ExecuteAsync("""
                UPDATE d SET
                  status = CASE d.replay_prior_status WHEN 'succeeded' THEN 'succeeded' WHEN 'held' THEN 'held' ELSE 'dead' END,
                  held_reason = CASE d.replay_prior_status WHEN 'succeeded' THEN NULL ELSE 'replay_cancelled' END,
                  held_until = CASE d.replay_prior_status WHEN 'held' THEN DATEADD(DAY, e.hold_days, @now) ELSE NULL END,
                  completed_at = CASE d.replay_prior_status WHEN 'held' THEN NULL ELSE @now END,
                  next_attempt_at = NULL, dispatch_until = NULL, replay_prior_status = NULL, replay_count = CASE WHEN d.replay_count > 0 THEN d.replay_count - 1 ELSE 0 END, updated_at = @now
                FROM webhooks.delivery d JOIN webhooks.endpoint e ON e.id = d.endpoint_id
                WHERE d.replay_job_id = @job AND d.status = 'pending' AND d.claimed_by IS NULL AND d.replay_prior_status IS NOT NULL
                """, ct, ("@job", id), ("@now", now));
            await db.ExecuteAsync("UPDATE webhooks.replay_job SET cancelled_at = @now WHERE id = @job", ct, ("@job", id), ("@now", now));
            await audit.WriteAsync(tx.GetDbTransaction(), new AuditEntry("replay.cancelled", "replay_job", id.ToString(), null, new { reverted }), ct);
            await tx.CommitAsync(ct);
        });
        return Results.Ok(new { cancelled = true, reverted });
    }

    // ------------------------------------------------------------------ the guided resume

    /// <summary>
    /// Resume after a pause or an auto-disable: the endpoint must have passed a test just now (so we never release a backlog at a receiver that is still broken),
    /// then the person decides what happens to what was held: deliver all at a steady pace, only the recent events, or discard.
    /// </summary>
    private static async Task<IResult> ResumeGuided(
        Guid id, GuidedResumeInput input, ClaimsPrincipal user, WebhooksDbContext db, IAuditLog audit, IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();
        if (input.Mode is null || !ResumeModes.Contains(input.Mode)) return Problems.Validation("invalid_mode", "Choose what to do with the held events: deliver_all, only_recent or discard.");
        if (e.Status is not (EndpointStatus.Paused or EndpointStatus.Disabled)) return Problems.Conflict("not_resumable", $"The endpoint is {e.Status}.");

        var now = clock.GetUtcNow().UtcDateTime;
        var o = options.Value.Replay;
        if (e.LastTestPassedAt is not { } tested || tested < now.AddMinutes(-o.TestFreshMinutes))
            return Problems.Conflict("test_required", $"Send a test that passes first (within the last {o.TestFreshMinutes} minutes). We do not release held events to a receiver we have not just checked.");
        var recentHours = Math.Clamp(input.RecentHours ?? o.RecentHours, 1, 720);

        e.Status = EndpointStatus.Active;
        e.DisabledReason = null;
        Health.Apply(e, Core.EndpointHealth.Healthy);
        e.UpdatedAt = now;

        var job = NewJob(e, "resume", input.Mode, DeliveryStatus.Held, null, null, null, o.DefaultRatePerSecond, user, now);
        var released = 0;
        var discarded = 0;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);

            var recentOnly = input.Mode == "only_recent";
            var cut = now.AddHours(-recentHours);
            if (input.Mode is "only_recent" or "discard")
            {
                discarded = await db.ExecuteAsync($"""
                    UPDATE webhooks.delivery SET status = 'dead', held_reason = 'discarded', held_until = NULL, completed_at = @now, next_attempt_at = NULL, dispatch_until = NULL, updated_at = @now
                    WHERE endpoint_id = @e AND status = 'held' {(recentOnly ? "AND created_at < @cut" : "")}
                    """, ct, ("@e", id), ("@now", now), ("@cut", cut));
            }
            if (input.Mode is "deliver_all" or "only_recent")
            {
                var spacing = Math.Max(1, 1000 / Math.Max(1, o.DefaultRatePerSecond));
                job.Discarded = discarded;
                db.ReplayJobs.Add(job);
                await db.SaveChangesAsync(ct);
                released = await Release(db, e, job, "endpoint_id = @e AND status = 'held'", [("@e", id)], spacing, now, options.Value, ct);
                await db.ExecuteAsync("UPDATE webhooks.replay_job SET total = @n WHERE id = @job", ct, ("@n", released), ("@job", job.Id));
            }
            await audit.WriteAsync(tx.GetDbTransaction(), new AuditEntry("endpoint.resumed", "endpoint", id.ToString(), e.Name, new { mode = input.Mode, released, discarded, recentHours = recentOnly ? recentHours : (int?)null }), ct);
            await tx.CommitAsync(ct);
        });
        return Results.Ok(new { status = e.Status, released, discarded, jobId = released > 0 ? job.Id : (Guid?)null });
    }

    // ------------------------------------------------------------------ shared pieces

    private sealed record Selection(EndpointEntity Endpoint, string[] States, string? EventType, DateTime? From, DateTime? To);

    private static async Task<(Selection?, IResult?)> ResolveAsync(ReplaySelectionInput input, WebhooksDbContext db, CancellationToken ct)
    {
        if (input.EndpointId is not { } endpointId) return (null, Problems.Validation("endpoint_required", "Choose an endpoint."));
        var states = (input.States is { Length: > 0 } s ? s : [DeliveryStatus.Dead]).Distinct().ToArray();
        if (states.Any(x => !ReplayableStates.Contains(x))) return (null, Problems.Validation("invalid_state", $"States to replay: {string.Join(", ", ReplayableStates)}."));
        input = input with { From = input.From.AsUtc(), To = input.To.AsUtc() };
        if (input.From is not null && input.To is not null && input.From >= input.To) return (null, Problems.Validation("invalid_range", "The start of the time range must be before its end."));
        if (input.EventType is { Length: > 80 }) return (null, Problems.Validation("invalid_event_type", "The event type is too long."));

        var endpoint = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == endpointId, ct);
        return endpoint is null ? (null, EndpointsApi.NotFound()) : (new Selection(endpoint, states, string.IsNullOrWhiteSpace(input.EventType) ? null : input.EventType.Trim(), input.From, input.To), null);
    }

    /// <summary>The state names come from a fixed list (validated above), so they can sit in the SQL text; every other value is a parameter.</summary>
    private static (string Where, (string, object?)[] Parameters) Where(Selection s)
    {
        var parts = new List<string> { "endpoint_id = @e", "is_test = 0", $"status IN ({string.Join(", ", s.States.Select(x => $"'{x}'"))})" };
        var parameters = new List<(string, object?)> { ("@e", s.Endpoint.Id) };
        if (s.EventType is not null) { parts.Add("event_type = @type"); parameters.Add(("@type", s.EventType)); }
        if (s.From is not null) { parts.Add("created_at >= @from"); parameters.Add(("@from", s.From)); }
        if (s.To is not null) { parts.Add("created_at < @to"); parameters.Add(("@to", s.To)); }
        return (string.Join(" AND ", parts), parameters.ToArray());
    }

    private static ReplayJobEntity NewJob(EndpointEntity endpoint, string kind, string mode, string states, string? eventType, DateTime? from, DateTime? to, int rate, ClaimsPrincipal user, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = endpoint.TenantId, EndpointId = endpoint.Id, Kind = kind, Mode = mode, States = states, EventType = eventType, FromAt = from, ToAt = to,
        RatePerSecond = rate, CreatedBy = EndpointViews.Actor(user), CreatedAt = now,
    };

    /// <summary>Creates the job and puts the chosen deliveries back in the queue, spaced by the rate, in one transaction with the audit line.</summary>
    private static async Task<int> ReleaseAsync(
        WebhooksDbContext db, IAuditLog audit, EndpointEntity endpoint, ReplayJobEntity job, string where, (string, object?)[] parameters, DateTime now, WebhooksOptions options, AuditEntry entry, CancellationToken ct)
    {
        var released = 0;
        var spacing = Math.Max(1, 1000 / Math.Max(1, job.RatePerSecond));
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ReplayJobs.Add(job);
            await db.SaveChangesAsync(ct);
            released = await Release(db, endpoint, job, where, parameters, spacing, now, options, ct);
            await db.ExecuteAsync("UPDATE webhooks.replay_job SET total = @n WHERE id = @job", ct, ("@n", released), ("@job", job.Id));
            await audit.WriteAsync(tx.GetDbTransaction(), entry, ct);
            await tx.CommitAsync(ct);
        });
        return released;
    }

    private static async Task<int> Release(WebhooksDbContext db, EndpointEntity endpoint, ReplayJobEntity job, string where, (string, object?)[] parameters, int spacingMs, DateTime now, WebhooksOptions options, CancellationToken ct)
    {
        // a replayed delivery gets a fresh retry window, plus the time it spends waiting in line for its turn
        var window = Policies.Retry(options, endpoint.RetryPreset, endpoint.RetryJson).Window;
        var args = new List<(string, object?)>(parameters) { ("@spacing", spacingMs), ("@job", job.Id), ("@now", now), ("@window", (int)window.TotalSeconds) };
        return await db.ExecuteAsync($"""
            ;WITH pick AS (SELECT id, ROW_NUMBER() OVER (ORDER BY created_at, id) AS rn FROM webhooks.delivery WHERE {where})
            UPDATE d SET
              replay_prior_status = d.status, status = 'pending', held_reason = NULL, held_until = NULL, completed_at = NULL,
              claimed_by = NULL, claimed_until = NULL, dispatch_until = NULL,
              next_attempt_at = DATEADD(MILLISECOND, (pick.rn - 1) * @spacing, @now),
              deadline_at = DATEADD(MILLISECOND, pick.rn * @spacing, DATEADD(SECOND, @window, @now)),
              replay_job_id = @job, replay_count = d.replay_count + 1, replayed_at = @now, attempt_floor = d.attempt_count, updated_at = @now
            FROM webhooks.delivery d JOIN pick ON pick.id = d.id
            """, ct, args.ToArray());
    }
}
