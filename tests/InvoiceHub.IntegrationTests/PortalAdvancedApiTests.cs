using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// M4 on the real stack: the delivery log, replay and guided resume, the audit trail, API keys with the pull API, team management, alerts by inbox and by email,
/// and the Playground's editable test data. Same rules as the other suites: real Keycloak tokens, real SQL with row-level security, the real receiver app.
/// </summary>
[Collection(PlatformCollection.Name)]
public class PortalAdvancedApiTests(PlatformFixture platform) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private static readonly Guid SeededEndpoint = Guid.Parse("0198a000-0000-7000-8000-0000000001e1");
    private static readonly Guid Contoso = Guid.Parse("0198a000-0000-7000-8000-000000000001");

    public async ValueTask InitializeAsync() => await ResetAsync();

    public async ValueTask DisposeAsync() => await ResetAsync();

    private async Task ResetAsync()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "ok", delaySeconds = 15 })).EnsureSuccessStatusCode();
        var list = await Json(HttpMethod.Get, "/api/portal/endpoints", owner);
        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("summary").GetProperty("id").GetGuid();
            if (id != SeededEndpoint) await platform.Api.SendAsync(Request(HttpMethod.Delete, $"/api/portal/endpoints/{id}", owner));
        }
        await TrustAsync([], null);
        await platform.Simulator.PostAsync("/simulator/stop-all", null);
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task Every_change_leaves_an_audit_line_that_only_owners_and_admins_can_read_and_nobody_can_alter()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var id = await CreateAsync(owner, "Audit trail");

        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}", owner, new { description = "changed" });
        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/delivery", owner, new { holdDays = 10 });
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/pause", owner))).EnsureSuccessStatusCode();
        (await platform.Api.SendAsync(Request(HttpMethod.Delete, $"/api/portal/endpoints/{id}", owner))).EnsureSuccessStatusCode();

        var log = await Json(HttpMethod.Get, $"/api/portal/audit?targetId={id}", owner);
        var actions = log.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.Equal(["endpoint.deleted", "endpoint.paused", "endpoint.delivery_changed", "endpoint.updated", "endpoint.created"], actions); // newest first
        var created = log.GetProperty("items").EnumerateArray().Last();
        Assert.Equal("owner@contoso.test", created.GetProperty("actor").GetString());
        Assert.Equal("owner", created.GetProperty("actorRole").GetString());
        Assert.False(string.IsNullOrEmpty(created.GetProperty("traceId").GetString())); // the line can be followed into the traces
        Assert.Contains("invoice.paid", created.GetProperty("details").GetRawText(), StringComparison.Ordinal);

        // action filter with a prefix
        var onlyEndpointChanges = await Json(HttpMethod.Get, "/api/portal/audit?action=endpoint.*&limit=2", owner);
        Assert.Equal(2, onlyEndpointChanges.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, onlyEndpointChanges.GetProperty("nextCursor").ValueKind);

        // roles: a developer cannot read it
        var developer = await platform.DemoTokenAsync("developer@contoso.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/portal/audit", developer))).StatusCode);

        // tenants: another tenant's owner sees none of it
        var fabrikam = await platform.DemoTokenAsync("owner@fabrikam.test");
        var theirs = await Json(HttpMethod.Get, $"/api/portal/audit?targetId={id}", fabrikam);
        Assert.Equal(0, theirs.GetProperty("items").GetArrayLength());

        // nobody holding an application login can change or delete history, whatever the code above them does
        foreach (var user in new[] { "api_user", "worker_user" })
        {
            await Assert.ThrowsAsync<SqlException>(() => platform.ScalarAsAsync<int>(user, "UPDATE audit.entry SET actor = N'x' WHERE 1 = 0; SELECT 1", Contoso));
            await Assert.ThrowsAsync<SqlException>(() => platform.ScalarAsAsync<int>(user, "DELETE audit.entry WHERE 1 = 0; SELECT 1", Contoso));
        }
    }

    [Fact]
    public async Task Revealing_a_secret_needs_a_real_fresh_sign_in_and_is_audited_without_the_secret()
    {
        var plain = await platform.DemoTokenAsync("developer@contoso.test");
        var id = await CreateAsync(plain, "Reveal audit");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/secret/reveal", plain, new { }))).StatusCode);

        var fresh = await platform.StepUpTokenAsync("developer@contoso.test");
        var reveal = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/secret/reveal", fresh, new { });
        var secret = reveal.GetProperty("secret").GetString()!;
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);

        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var log = await Json(HttpMethod.Get, $"/api/portal/audit?targetId={id}&action=secret.revealed", owner);
        var line = Assert.Single(log.GetProperty("items").EnumerateArray());
        Assert.Equal("developer@contoso.test", line.GetProperty("actor").GetString());
        Assert.DoesNotContain(secret, line.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(reveal.GetProperty("keyId").GetString(), line.GetProperty("details").GetProperty("keyId").GetString());
    }

    // ---------------------------------------------------------------- delivery log

    [Fact]
    public async Task The_delivery_log_filters_shows_the_request_as_sent_and_never_shows_a_credential()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var id = await CreateAsync(owner, "Log check", ReceiverHook("log-check"), ["invoice.paid"]);
        var secret = await platform.HmacSecretOfAsync(id);
        await TrustAsync([secret], null);
        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/headers", owner, new { headers = new[] { new { name = "X-Gateway-Key", value = "gw-s3cret", secret = true }, new { name = "X-Region", value = "eu", secret = false } } });
        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/auth", owner, new { type = "header", headerName = "X-Api-Key", value = "api-k3y-123" });
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", owner)).GetProperty("ok").GetBoolean());

        await SimulateAsync(3);
        await WaitDeliveriesAsync(owner, id, "succeeded", 3);

        // filters and counts
        var all = await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}", owner);
        Assert.Equal(3, all.GetProperty("counts").GetProperty("succeeded").GetInt32());
        Assert.Equal(3, all.GetProperty("counts").GetProperty("total").GetInt32());
        var first = all.GetProperty("items")[0];
        Assert.Equal("invoice.paid", first.GetProperty("eventType").GetString());
        Assert.StartsWith("evt_", first.GetProperty("webhookId").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&status=dead", owner)).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&eventType=invoice.overdue", owner)).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&from={Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("O"))}", owner)).GetProperty("items").GetArrayLength());
        Assert.Equal(3, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&from={Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("O"))}", owner)).GetProperty("items").GetArrayLength());

        // free text: the business object, the webhook id and the delivery id all find the row
        var aggregate = first.GetProperty("aggregateId").GetString()!;
        Assert.Equal(1, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&q={Uri.EscapeDataString(aggregate)}", owner)).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&q={first.GetProperty("webhookId").GetString()}", owner)).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&q={first.GetProperty("id").GetGuid()}", owner)).GetProperty("items").GetArrayLength());
        var invalid = await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/portal/deliveries?status=exploded", owner));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // paging
        var page = await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&limit=2", owner);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        var next = await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&limit=2&cursor={page.GetProperty("nextCursor").GetString()}", owner);
        Assert.Equal(1, next.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("counts").ValueKind); // counted once per search, not per page

        // the drawer: request as sent, secrets masked, the body, the attempts
        var detail = await Json(HttpMethod.Get, $"/api/portal/deliveries/{first.GetProperty("id").GetGuid()}", owner);
        var headers = detail.GetProperty("request").GetProperty("headers");
        Assert.Equal("••••••••", headers.GetProperty("X-Api-Key").GetString());
        Assert.Equal("••••••••", headers.GetProperty("X-Gateway-Key").GetString());
        Assert.Equal("eu", headers.GetProperty("X-Region").GetString());
        Assert.EndsWith("...", headers.GetProperty("webhook-signature").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("gw-s3cret", detail.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-k3y-123", detail.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"type\": \"invoice.paid\"", detail.GetProperty("request").GetProperty("body").GetString(), StringComparison.Ordinal);
        var attempt = Assert.Single(detail.GetProperty("attempts").EnumerateArray());
        Assert.Equal("success", attempt.GetProperty("outcome").GetString());
        Assert.Equal(200, attempt.GetProperty("statusCode").GetInt32());

        // who may see a payload: a viewer sees the delivery but not the body; support sees it
        var viewer = await platform.DemoTokenAsync("viewer@contoso.test");
        var asViewer = await Json(HttpMethod.Get, $"/api/portal/deliveries/{first.GetProperty("id").GetGuid()}", viewer);
        Assert.False(asViewer.GetProperty("canViewPayload").GetBoolean());
        Assert.Equal(JsonValueKind.Null, asViewer.GetProperty("request").GetProperty("body").ValueKind);
        var support = await platform.DemoTokenAsync("support@contoso.test");
        Assert.NotEqual(JsonValueKind.Null, (await Json(HttpMethod.Get, $"/api/portal/deliveries/{first.GetProperty("id").GetGuid()}", support)).GetProperty("request").GetProperty("body").ValueKind);

        // another tenant's delivery id is simply not found
        var fabrikam = await platform.DemoTokenAsync("owner@fabrikam.test");
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(Request(HttpMethod.Get, $"/api/portal/deliveries/{first.GetProperty("id").GetGuid()}", fabrikam))).StatusCode);
    }

    // ---------------------------------------------------------------- replay

    [Fact]
    public async Task A_single_replay_resends_the_same_webhook_id_and_the_receiver_treats_it_as_a_duplicate()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var since = DateTime.UtcNow.AddSeconds(-1);
        await SimulateAsync(2);
        await WaitSettledAsync(owner, SeededEndpoint, since, minimum: 4);
        var item = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededEndpoint}&status=succeeded&limit=1&from={Uri.EscapeDataString(since.ToString("O"))}", owner)).GetProperty("items")[0];
        var deliveryId = item.GetProperty("id").GetGuid();
        var before = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts").GetProperty("duplicates").GetInt32();

        // a viewer may not; support may (single event)
        var viewer = await platform.DemoTokenAsync("viewer@contoso.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/deliveries/{deliveryId}/replay", viewer))).StatusCode);
        var support = await platform.DemoTokenAsync("support@contoso.test");
        var accepted = await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/deliveries/{deliveryId}/replay", support));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var d = (await Json(HttpMethod.Get, $"/api/portal/deliveries/{deliveryId}", owner)).GetProperty("delivery");
            return d.GetProperty("status").GetString() == "succeeded" && d.GetProperty("attemptCount").GetInt32() == 2;
        }, Patience, "the replayed delivery to be sent again");

        var detail = await Json(HttpMethod.Get, $"/api/portal/deliveries/{deliveryId}", owner);
        Assert.Equal(1, detail.GetProperty("delivery").GetProperty("replayCount").GetInt32());
        Assert.Equal([1, 2], detail.GetProperty("attempts").EnumerateArray().Select(a => a.GetProperty("attemptNo").GetInt32()).ToArray()); // the log keeps counting
        Assert.Equal(item.GetProperty("webhookId").GetString(), detail.GetProperty("delivery").GetProperty("webhookId").GetString());
        await PlatformFixture.WaitUntilAsync(async () =>
            (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts").GetProperty("duplicates").GetInt32() > before, Patience, "the receiver to see the same webhook-id again");

        // an event that is still being delivered cannot be replayed on top of itself
        var log = await Json(HttpMethod.Get, "/api/portal/audit?action=replay.single", owner);
        Assert.Equal("support@contoso.test", log.GetProperty("items")[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_replay_job_needs_a_dry_run_and_a_typed_confirmation_and_runs_at_the_chosen_pace_and_can_be_cancelled()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var since = DateTime.UtcNow.AddSeconds(-1);
        await SimulateAsync(3);
        await WaitSettledAsync(owner, SeededEndpoint, since, minimum: 6);
        var selection = new { endpointId = SeededEndpoint, states = new[] { "succeeded" }, eventType = "invoice.paid", from = since.ToString("O") };
        // the paid events come a little after the generated ones: wait for this test's three, not just for "six deliveries of any kind"
        await PlatformFixture.WaitUntilAsync(async () => (await Json(HttpMethod.Post, "/api/portal/replay/preview", owner, selection)).GetProperty("count").GetInt32() >= 3, Patience, "the three paid invoices to be delivered");

        // support cannot run jobs
        var support = await platform.DemoTokenAsync("support@contoso.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/replay/preview", support, selection))).StatusCode);

        var preview = await Json(HttpMethod.Post, "/api/portal/replay/preview", owner, selection);
        var count = preview.GetProperty("count").GetInt32();
        Assert.InRange(count, 3, 6); // this test's three paid invoices (plus, at most, the last paid event of an earlier test that lands in the same second): the time range scopes the job
        Assert.Equal($"REPLAY {count}", preview.GetProperty("confirmPhrase").GetString());
        Assert.True(preview.GetProperty("sameWebhookId").GetBoolean());
        Assert.False(preview.GetProperty("cap").GetProperty("exceeded").GetBoolean());
        Assert.Equal("invoice.paid", preview.GetProperty("byType")[0].GetProperty("type").GetString());

        // the typed confirmation names the number of events
        var refused = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/replay/jobs", owner, new { selection.endpointId, selection.states, selection.eventType, selection.from, confirm = "REPLAY 1" }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("confirmation_required", (await platform.ReadJsonAsync(refused)).GetProperty("code").GetString());

        var started = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/replay/jobs", owner, new { selection.endpointId, selection.states, selection.eventType, selection.from, confirm = $"REPLAY {count}", ratePerSecond = 50 }));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var jobId = (await platform.ReadJsonAsync(started)).GetProperty("jobId").GetGuid();
        await PlatformFixture.WaitUntilAsync(async () => (await JobAsync(owner, jobId)).GetProperty("status").GetString() == "completed", Patience, "the replay job to finish");
        var job = await JobAsync(owner, jobId);
        Assert.Equal(count, job.GetProperty("delivered").GetInt32());
        Assert.Equal(0, job.GetProperty("failed").GetInt32());

        // a slow job can be cancelled: whatever has not been sent goes back to where it was
        var again = await Json(HttpMethod.Post, "/api/portal/replay/preview", owner, selection); // every job starts with its own dry run, as the portal does
        var slow = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/replay/jobs", owner, new { selection.endpointId, selection.states, selection.eventType, selection.from, confirm = again.GetProperty("confirmPhrase").GetString(), ratePerSecond = 1 }));
        Assert.True(slow.StatusCode == HttpStatusCode.Accepted, $"slow job: {(int)slow.StatusCode} {await slow.Content.ReadAsStringAsync()}");
        var slowId = (await platform.ReadJsonAsync(slow)).GetProperty("jobId").GetGuid();
        var cancelled = await Json(HttpMethod.Post, $"/api/portal/replay/jobs/{slowId}/cancel", owner);
        Assert.True(cancelled.GetProperty("reverted").GetInt32() >= 1);
        Assert.Equal("cancelled", (await JobAsync(owner, slowId)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/replay/jobs/{slowId}/cancel", owner))).StatusCode);

        var audit = await Json(HttpMethod.Get, "/api/portal/audit?action=replay.*", owner);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), i => i.GetProperty("action").GetString() == "replay.started");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), i => i.GetProperty("action").GetString() == "replay.cancelled");
    }

    [Fact]
    public async Task A_paused_endpoint_holds_events_and_resume_needs_a_passing_test_then_releases_or_discards_what_was_held()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var id = await CreateAsync(owner, "Resume guide", ReceiverHook("resume-guide"), ["invoice.paid"]);
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/pause", owner))).EnsureSuccessStatusCode();

        await SimulateAsync(3);
        await WaitDeliveriesAsync(owner, id, "held", 3);
        var overview = await Json(HttpMethod.Get, "/api/portal/replay/overview", owner);
        var row = overview.GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal(3, row.GetProperty("held").GetInt32());
        Assert.False(row.GetProperty("testFresh").GetBoolean());

        // no passing test, no resume
        var blocked = await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/resume-guided", owner, new { mode = "deliver_all" }));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("test_required", (await platform.ReadJsonAsync(blocked)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/resume-guided", owner, new { mode = "everything" }))).StatusCode);

        await TrustAsync([await platform.HmacSecretOfAsync(id)], null);
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", owner)).GetProperty("ok").GetBoolean());
        Assert.True((await Json(HttpMethod.Get, "/api/portal/replay/overview", owner)).GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id).GetProperty("testFresh").GetBoolean());

        // deliver, paced
        var resumed = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/resume-guided", owner, new { mode = "only_recent", recentHours = 24 });
        Assert.Equal(3, resumed.GetProperty("released").GetInt32());
        Assert.Equal(0, resumed.GetProperty("discarded").GetInt32());
        await WaitDeliveriesAsync(owner, id, "succeeded", 3);
        Assert.Equal("healthy", (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", owner)).GetProperty("endpoint").GetProperty("health").GetString());

        // pause again, collect two more, and this time discard them: they are kept as dead (visible, replayable), not erased
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/pause", owner))).EnsureSuccessStatusCode();
        await SimulateAsync(2);
        await WaitDeliveriesAsync(owner, id, "held", 2);
        var discarded = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/resume-guided", owner, new { mode = "discard" });
        Assert.Equal(2, discarded.GetProperty("discarded").GetInt32());
        var dead = await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&status=dead", owner);
        Assert.Equal(2, dead.GetProperty("items").GetArrayLength());
        Assert.All(dead.GetProperty("items").EnumerateArray(), d => Assert.Equal("discarded", d.GetProperty("heldReason").GetString()));

        // and they can still be brought back deliberately
        var selection = new { endpointId = id, states = new[] { "dead" } };
        var preview = await Json(HttpMethod.Post, "/api/portal/replay/preview", owner, selection);
        Assert.Equal(2, preview.GetProperty("count").GetInt32());
        var replay = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/replay/jobs", owner, new { selection.endpointId, selection.states, confirm = "REPLAY 2", ratePerSecond = 20 }));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await WaitDeliveriesAsync(owner, id, "succeeded", 5);

        var audit = await Json(HttpMethod.Get, $"/api/portal/audit?targetId={id}&action=endpoint.resumed", owner);
        Assert.Equal(["discard", "only_recent"], audit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("details").GetProperty("mode").GetString()).ToArray());
    }

    // ---------------------------------------------------------------- alerts

    [Fact]
    public async Task A_failing_endpoint_warns_at_25_50_75_percent_then_switches_off_and_the_owner_hears_in_the_portal_and_by_email()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var id = await CreateAsync(owner, "Alert target", ReceiverHook("alerts"), ["invoice.paid"]);
        await TrustAsync([await platform.HmacSecretOfAsync(id)], null);
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", owner)).GetProperty("ok").GetBoolean());
        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/delivery", owner, new { circuitFailures = 2, autoDisable = new { failures = 5, withinHours = 24, days = 5 } });

        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "fail500", delaySeconds = 15 })).EnsureSuccessStatusCode();
        await SimulateAsync(8);

        await PlatformFixture.WaitUntilAsync(async () => (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", owner)).GetProperty("endpoint").GetProperty("status").GetString() == "disabled", Patience, "the endpoint to be switched off");
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "ok", delaySeconds = 15 })).EnsureSuccessStatusCode();

        // the in-app inbox (the bell): three pre-warnings and the switch-off, each once
        await PlatformFixture.WaitUntilAsync(async () => (await InboxAsync(owner)).GetProperty("items").EnumerateArray().Count(i => i.GetProperty("endpointId").ValueKind != JsonValueKind.Null && i.GetProperty("title").GetString()!.Contains("Alert target", StringComparison.Ordinal)) >= 4, Patience, "the alerts to arrive");
        var inbox = await InboxAsync(owner);
        var mine = inbox.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("title").GetString()!.Contains("Alert target", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, mine.Count(i => i.GetProperty("kind").GetString() == "degraded"));
        Assert.Equal(1, mine.Count(i => i.GetProperty("kind").GetString() == "auto_disabled"));
        Assert.Equal([25, 50, 75], mine.Where(i => i.GetProperty("kind").GetString() == "degraded").Select(i => int.Parse(System.Text.RegularExpressions.Regex.Match(i.GetProperty("title").GetString()!, @"(\d+)%").Groups[1].Value)).Order().ToArray());
        Assert.True(inbox.GetProperty("unread").GetInt32() >= 4);

        // email, to the roles of the rule (the switch-off is emailed by default, the pre-warnings are in-app only)
        await PlatformFixture.WaitUntilAsync(async () => (await MailAsync()).Any(m => m.To == "owner@contoso.test" && m.Subject.Contains("Alert target", StringComparison.Ordinal) && m.Subject.Contains("switched off", StringComparison.Ordinal)), Patience, "the email to arrive in Mailpit");
        var mails = (await MailAsync()).Where(m => m.Subject.Contains("Alert target", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(mails, m => m.Subject.Contains("failing", StringComparison.Ordinal)); // no email for the pre-warnings
        Assert.Contains(mails, m => m.To == "developer@contoso.test");
        Assert.DoesNotContain(mails, m => m.To == "viewer@contoso.test");

        // the same event never alerts twice: nothing new appears after the dust settles
        await Task.Delay(2000);
        Assert.Equal(4, (await InboxAsync(owner)).GetProperty("items").EnumerateArray().Count(i => i.GetProperty("title").GetString()!.Contains("Alert target", StringComparison.Ordinal)));

        // reading: one, then all
        var firstId = mine[0].GetProperty("id").GetInt64();
        var unread = inbox.GetProperty("unread").GetInt32();
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/notifications/{firstId}/read", owner))).EnsureSuccessStatusCode();
        Assert.Equal(unread - 1, (await InboxAsync(owner)).GetProperty("unread").GetInt32());
        (await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/notifications/read-all", owner))).EnsureSuccessStatusCode();
        Assert.Equal(0, (await InboxAsync(owner)).GetProperty("unread").GetInt32());

        // a viewer was not addressed, so the bell is empty for them; and another tenant never sees these alerts
        Assert.Equal(0, (await InboxAsync(await platform.DemoTokenAsync("viewer@contoso.test"))).GetProperty("items").GetArrayLength());
        Assert.DoesNotContain((await InboxAsync(await platform.DemoTokenAsync("owner@fabrikam.test"))).GetProperty("items").EnumerateArray(), i => i.GetProperty("title").GetString()!.Contains("Alert target", StringComparison.Ordinal));

        // the held events are waiting, and the platform said so in its own audit trail
        Assert.True((await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", owner)).GetProperty("endpoint").GetProperty("backlog").GetProperty("held").GetInt32() >= 1);
        var audit = await Json(HttpMethod.Get, $"/api/portal/audit?targetId={id}&action=endpoint.disabled", owner);
        Assert.Equal("system", audit.GetProperty("items")[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Alert_rules_can_be_changed_by_owners_and_admins_only_and_the_change_is_audited()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var rules = await Json(HttpMethod.Get, "/api/portal/alerts", await platform.DemoTokenAsync("viewer@contoso.test"));
        var degraded = rules.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("trigger").GetString() == "degraded");
        Assert.True(degraded.GetProperty("isDefault").GetBoolean());
        Assert.False(degraded.GetProperty("email").GetBoolean());

        var developer = await platform.DemoTokenAsync("developer@contoso.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Put, "/api/portal/alerts/degraded", developer, new { enabled = true, email = true }))).StatusCode);

        foreach (var (body, code) in new (object, string)[]
        {
            (new { enabled = true, roles = Array.Empty<string>() }, "no_recipients"),
            (new { enabled = true, inApp = false, email = false }, "no_channel"),
            (new { enabled = true, roles = new[] { "emperor" } }, "invalid_role"),
        })
        {
            var response = await platform.Api.SendAsync(Request(HttpMethod.Put, "/api/portal/alerts/degraded", owner, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(Request(HttpMethod.Put, "/api/portal/alerts/nonsense", owner, new { enabled = true }))).StatusCode);

        (await platform.Api.SendAsync(Request(HttpMethod.Put, "/api/portal/alerts/degraded", owner, new { enabled = true, inApp = true, email = true, roles = new[] { "owner" } }))).EnsureSuccessStatusCode();
        var changed = (await Json(HttpMethod.Get, "/api/portal/alerts", owner)).GetProperty("items").EnumerateArray().Single(r => r.GetProperty("trigger").GetString() == "degraded");
        Assert.True(changed.GetProperty("email").GetBoolean());
        Assert.False(changed.GetProperty("isDefault").GetBoolean());
        Assert.Equal(["owner"], changed.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
        var audit = await Json(HttpMethod.Get, "/api/portal/audit?action=alerts.changed", owner);
        Assert.Equal("degraded", audit.GetProperty("items")[0].GetProperty("targetId").GetString());

        // put the default back for the other tests
        (await platform.Api.SendAsync(Request(HttpMethod.Put, "/api/portal/alerts/degraded", owner, new { enabled = true, inApp = true, email = false, roles = new[] { "owner", "admin", "developer" } }))).EnsureSuccessStatusCode();
    }

    // ---------------------------------------------------------------- API keys and the pull API

    [Fact]
    public async Task An_api_key_is_shown_once_unlocks_exactly_its_scopes_and_stops_working_when_revoked()
    {
        var plain = await platform.DemoTokenAsync("developer@contoso.test");
        var refused = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/api-keys", plain, new { name = "No step-up", scopes = new[] { "events:read" } }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("step_up_required", (await platform.ReadJsonAsync(refused)).GetProperty("code").GetString());

        var fresh = await platform.StepUpTokenAsync("developer@contoso.test");
        foreach (var (body, code) in new (object, string)[]
        {
            (new { name = "", scopes = new[] { "events:read" } }, "invalid_name"),
            (new { name = "x", scopes = Array.Empty<string>() }, "no_scopes"),
            (new { name = "x", scopes = new[] { "admin:everything" } }, "unknown_scope"),
            (new { name = "x", scopes = new[] { "events:read" }, expiresInDays = 7 }, "invalid_expiry"),
        })
        {
            var response = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/api-keys", fresh, body));
            Assert.Equal(code, (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
        }

        var created = await Json(HttpMethod.Post, "/api/portal/api-keys", fresh, new { name = "Reconciliation job", scopes = new[] { "events:read" }, expiresInDays = 30 });
        var key = created.GetProperty("key").GetString()!;
        var keyId = created.GetProperty("id").GetGuid();
        Assert.StartsWith("ihk_", key, StringComparison.Ordinal);

        // the list never shows the key again
        var list = await Json(HttpMethod.Get, "/api/portal/api-keys", plain);
        Assert.DoesNotContain(key, list.GetRawText(), StringComparison.Ordinal);
        var row = list.GetProperty("items").EnumerateArray().Single(k => k.GetProperty("id").GetGuid() == keyId);
        Assert.Equal("active", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(1, await SqlInt($"SELECT COUNT(*) FROM tenancy.api_key WHERE id = '{keyId}' AND DATALENGTH(key_hash) = 32")); // only a 32-byte hash is stored, there is no column for the key itself

        // the pull API: the stream of events, oldest first, with paging
        await SimulateAsync(2);
        await PlatformFixture.WaitUntilAsync(async () => (await PullAsync(key, "limit=100")).GetProperty("items").GetArrayLength() >= 4, Patience, "events to be readable");
        var page = await PullAsync(key, "limit=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        var next = await PullAsync(key, $"limit=2&after={page.GetProperty("next").GetString()}");
        Assert.NotEqual(page.GetProperty("items")[0].GetProperty("id").GetString(), next.GetProperty("items")[0].GetProperty("id").GetString());
        var paid = await PullAsync(key, "type=invoice.paid");
        Assert.All(paid.GetProperty("items").EnumerateArray(), e => Assert.Equal("invoice.paid", e.GetProperty("type").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await PullRawAsync(key, "type=no.such.event")).StatusCode);

        // "last used" is recorded, and the use itself is not an audit event (reads are many), but creation is
        var used = (await Json(HttpMethod.Get, "/api/portal/api-keys", plain)).GetProperty("items").EnumerateArray().Single(k => k.GetProperty("id").GetGuid() == keyId);
        Assert.NotEqual(JsonValueKind.Null, used.GetProperty("lastUsedAt").ValueKind);

        // a key only does what its scopes say; a portal login is not an API key; garbage is not either
        var kycOnly = (await Json(HttpMethod.Post, "/api/portal/api-keys", fresh, new { name = "KYC only", scopes = new[] { "kyc:read" } })).GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await PullRawAsync(kycOnly, "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PullRawAsync(plain, "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PullRawAsync(key[..^3] + "abc", "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PullRawAsync("ihk_" + Guid.NewGuid().ToString("N") + "_" + Guid.NewGuid().ToString("N") + "_" + new string('a', 43), "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await platform.Api.GetAsync("/v1/events")).StatusCode);

        // revoke: the very next call fails
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        Assert.Equal(HttpStatusCode.NoContent, (await platform.Api.SendAsync(Request(HttpMethod.Delete, $"/api/portal/api-keys/{keyId}", owner))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PullRawAsync(key, "")).StatusCode);
        Assert.Equal("revoked", (await Json(HttpMethod.Get, "/api/portal/api-keys", owner)).GetProperty("items").EnumerateArray().Single(k => k.GetProperty("id").GetGuid() == keyId).GetProperty("status").GetString());

        var audit = await Json(HttpMethod.Get, "/api/portal/audit?targetType=api_key", owner);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), i => i.GetProperty("action").GetString() == "apikey.created" && i.GetProperty("actor").GetString() == "developer@contoso.test");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), i => i.GetProperty("action").GetString() == "apikey.revoked");
        Assert.DoesNotContain(key, audit.GetRawText(), StringComparison.Ordinal);

        // another tenant has its own keys and cannot see or revoke these
        var fabrikam = await platform.DemoTokenAsync("owner@fabrikam.test");
        Assert.DoesNotContain((await Json(HttpMethod.Get, "/api/portal/api-keys", fabrikam)).GetProperty("items").EnumerateArray(), k => k.GetProperty("id").GetGuid() == keyId);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(Request(HttpMethod.Delete, $"/api/portal/api-keys/{Guid.NewGuid()}", fabrikam))).StatusCode);
    }

    [Fact]
    public async Task A_key_of_one_tenant_reads_only_that_tenants_events()
    {
        var contosoKey = (await Json(HttpMethod.Post, "/api/portal/api-keys", await platform.StepUpTokenAsync("owner@contoso.test"), new { name = "Isolation check", scopes = new[] { "events:read" } })).GetProperty("key").GetString()!;
        await SimulateAsync(2);
        await PlatformFixture.WaitUntilAsync(async () => (await PullAsync(contosoKey, "limit=100")).GetProperty("items").GetArrayLength() >= 2, Patience, "events to be readable");

        // the key names its tenant; swapping that part for another tenant's id cannot reach the other tenant's data (the hash is looked up under THEIR row-level security)
        var parts = contosoKey.Split('_', 4);
        var forged = $"ihk_{Guid.Parse("0198a000-0000-7000-8000-000000000002"):N}_{parts[2]}_{parts[3]}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await PullRawAsync(forged, "")).StatusCode);
    }

    // ---------------------------------------------------------------- team and roles

    [Fact]
    public async Task The_team_can_be_managed_without_ever_locking_the_tenant_out()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var developer = await platform.DemoTokenAsync("developer@contoso.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/portal/team", developer))).StatusCode);

        var team = await Json(HttpMethod.Get, "/api/portal/team", owner);
        Assert.True(team.GetProperty("canManageOwners").GetBoolean());
        var me = team.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("email").GetString() == "owner@contoso.test");
        Assert.True(me.GetProperty("you").GetBoolean());
        Assert.Equal("owner", team.GetProperty("items")[0].GetProperty("role").GetString()); // owners first

        // you cannot demote or disable yourself
        Assert.Equal("cannot_change_self", await ProblemCodeAsync(HttpMethod.Put, $"/api/portal/team/{me.GetProperty("id").GetGuid()}/role", owner, new { role = "viewer" }));
        Assert.Equal("cannot_disable_self", await ProblemCodeAsync(HttpMethod.Post, $"/api/portal/team/{me.GetProperty("id").GetGuid()}/disable", owner));

        // add a member: created in Keycloak with the role, temporary password shown once
        var email = $"team-{Guid.NewGuid():N}"[..14] + "@contoso.test";
        foreach (var (body, code) in new (object, string)[]
        {
            (new { email = "nope", firstName = "A", lastName = "B", role = "support" }, "invalid_email"),
            (new { email, firstName = "", lastName = "B", role = "support" }, "invalid_name"),
            (new { email, firstName = "A", lastName = "B", role = "emperor" }, "invalid_role"),
            (new { email = "owner@contoso.test", firstName = "A", lastName = "B", role = "support" }, "email_taken"),
        })
            Assert.Equal(code, await ProblemCodeAsync(HttpMethod.Post, "/api/portal/team", owner, body));

        var added = await Json(HttpMethod.Post, "/api/portal/team", owner, new { email, firstName = "Nina", lastName = "Tester", role = "support" });
        var memberId = added.GetProperty("id").GetGuid();
        Assert.Equal(16, added.GetProperty("temporaryPassword").GetString()!.Length);
        Assert.Equal(["support"], await KeycloakRolesAsync(email));

        // change the role: Keycloak is updated, the old role is taken away
        (await platform.Api.SendAsync(Request(HttpMethod.Put, $"/api/portal/team/{memberId}/role", owner, new { role = "developer" }))).EnsureSuccessStatusCode();
        Assert.Equal(["developer"], await KeycloakRolesAsync(email));
        Assert.Equal("developer", (await Json(HttpMethod.Get, "/api/portal/team", owner)).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == memberId).GetProperty("role").GetString());

        // an admin may manage the team but cannot touch an owner
        var admin = await platform.DemoTokenAsync("admin@contoso.test");
        Assert.Equal("owner_only", await ProblemCodeAsync(HttpMethod.Post, $"/api/portal/team/{me.GetProperty("id").GetGuid()}/disable", admin));
        Assert.Equal("owner_only", await ProblemCodeAsync(HttpMethod.Post, "/api/portal/team", admin, new { email = $"x{email}", firstName = "A", lastName = "B", role = "owner" }));

        // disable and enable
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/team/{memberId}/disable", admin))).EnsureSuccessStatusCode();
        Assert.False((await Json(HttpMethod.Get, "/api/portal/team", owner)).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == memberId).GetProperty("active").GetBoolean());
        Assert.False(await KeycloakEnabledAsync(email));
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/team/{memberId}/enable", owner))).EnsureSuccessStatusCode();
        Assert.True(await KeycloakEnabledAsync(email));

        var audit = await Json(HttpMethod.Get, "/api/portal/audit?action=team.*", owner);
        var kinds = audit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.Contains("team.member_added", kinds);
        Assert.Contains("team.role_changed", kinds);
        Assert.Contains("team.member_disabled", kinds);
        Assert.Contains("team.member_enabled", kinds);
        Assert.DoesNotContain(added.GetProperty("temporaryPassword").GetString()!, audit.GetRawText(), StringComparison.Ordinal);

        // another tenant's team is not ours
        Assert.DoesNotContain((await Json(HttpMethod.Get, "/api/portal/team", await platform.DemoTokenAsync("owner@fabrikam.test"))).GetProperty("items").EnumerateArray(), m => m.GetProperty("id").GetGuid() == memberId);
    }

    [Fact]
    public async Task A_disabled_member_is_locked_out_within_seconds_even_with_a_token_that_has_not_expired()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var supportToken = await platform.DemoTokenAsync("support@contoso.test");
        Assert.Equal(HttpStatusCode.OK, (await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/me", supportToken))).StatusCode);
        var supportId = (await Json(HttpMethod.Get, "/api/portal/team", owner)).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("email").GetString() == "support@contoso.test").GetProperty("id").GetGuid();

        try
        {
            (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/team/{supportId}/disable", owner))).EnsureSuccessStatusCode();
            await PlatformFixture.WaitUntilAsync(async () =>
            {
                var response = await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/me", supportToken));
                return response.StatusCode == HttpStatusCode.Forbidden && (await platform.ReadJsonAsync(response)).GetProperty("code").GetString() == "account_disabled";
            }, TimeSpan.FromSeconds(40), "the old token to stop working");
            await Assert.ThrowsAnyAsync<Exception>(() => platform.DemoTokenAsync("support@contoso.test")); // and no new sign-in is possible
        }
        finally
        {
            (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/team/{supportId}/enable", owner))).EnsureSuccessStatusCode();
        }
        await PlatformFixture.WaitUntilAsync(async () => (await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/me", await platform.DemoTokenAsync("support@contoso.test")))).StatusCode == HttpStatusCode.OK, TimeSpan.FromSeconds(40), "the member to be back");
    }

    // ---------------------------------------------------------------- playground

    [Fact]
    public async Task The_playground_sends_the_data_a_person_edited_and_remembers_the_last_passing_test()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var id = await CreateAsync(owner, "Playground", ReceiverHook("playground"), ["invoice.paid"]);
        await TrustAsync([await platform.HmacSecretOfAsync(id)], null);

        var sample = await Json(HttpMethod.Get, "/api/portal/events/invoice.paid/sample", owner);
        Assert.Equal("invoice.paid", sample.GetProperty("type").GetString());
        Assert.True(sample.GetProperty("data").TryGetProperty("amount", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/portal/events/no.such/sample", owner))).StatusCode);
        var pii = await Json(HttpMethod.Get, "/api/portal/events/kyc.completed/sample", owner);
        Assert.True(pii.GetProperty("containsPii").GetBoolean());
        Assert.False(string.IsNullOrEmpty(pii.GetProperty("note").GetString()));

        var custom = new { invoice_id = "inv_custom_42", number = "INV-CUSTOM", amount = 12.5, currency = "EUR", status = "paid" };
        var test = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", owner, new { eventType = "invoice.paid", data = custom });
        Assert.True(test.GetProperty("ok").GetBoolean());
        var inbox = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{test.GetProperty("requestId").GetString()}");
        Assert.Contains("inv_custom_42", inbox.GetProperty("body").GetString(), StringComparison.Ordinal);

        Assert.Equal("invalid_data", await ProblemCodeAsync(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", owner, new { eventType = "invoice.paid", data = new[] { 1, 2 } }));
        var overview = await Json(HttpMethod.Get, "/api/portal/replay/overview", owner);
        Assert.NotEqual(JsonValueKind.Null, overview.GetProperty("endpoints").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id).GetProperty("lastTestPassedAt").ValueKind);
    }

    // ---------------------------------------------------------------- helpers

    private string ReceiverHook(string key) => new Uri(platform.ReceiverUrl, $"hooks/{key}").ToString();

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Headers = { Authorization = new("Bearer", token) } };
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<JsonElement> Json(HttpMethod method, string path, string token, object? body = null)
    {
        var response = await platform.Api.SendAsync(Request(method, path, token, body));
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await platform.ReadJsonAsync(response);
    }

    private async Task<string?> ProblemCodeAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var response = await platform.Api.SendAsync(Request(method, path, token, body));
        Assert.False(response.IsSuccessStatusCode, $"{method} {path} unexpectedly succeeded");
        return (await platform.ReadJsonAsync(response)).GetProperty("code").GetString();
    }

    private async Task<Guid> CreateAsync(string token, string name, string? url = null, string[]? events = null)
    {
        var response = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/endpoints", token, new
        {
            name, url = url ?? ReceiverHook(Guid.NewGuid().ToString("N")[..8]), events = events ?? ["invoice.paid", "payment.completed"],
        }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await platform.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task TrustAsync(string[] secrets, string? jwksUrl) =>
        (await platform.Receiver.PutAsJsonAsync("/api/trust", new { secrets, jwksUrl })).EnsureSuccessStatusCode();

    /// <summary>Plays <paramref name="invoices"/> invoices that are generated and paid, so each one produces an <c>invoice.generated</c> and an <c>invoice.paid</c> event.</summary>
    private async Task SimulateAsync(int invoices)
    {
        var response = await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "contoso", count = invoices, perSecond = 20, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task WaitDeliveriesAsync(string token, Guid endpointId, string status, int atLeast) =>
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={endpointId}&limit=1", token)).GetProperty("counts");
            var key = status switch { "succeeded" => "succeeded", "held" => "held", "dead" => "dead", "retry_scheduled" => "retrying", _ => "pending" };
            return counts.GetProperty(key).GetInt32() >= atLeast;
        }, Patience, $"{atLeast} deliveries to be {status}");

    /// <summary>Waits until everything this test caused for the endpoint has been delivered (nothing still pending or retrying), so counts and picks are about this test only.</summary>
    private async Task WaitSettledAsync(string token, Guid endpointId, DateTime since, int minimum) =>
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={endpointId}&limit=1&from={Uri.EscapeDataString(since.ToString("O"))}", token)).GetProperty("counts");
            return counts.GetProperty("succeeded").GetInt32() >= minimum && counts.GetProperty("pending").GetInt32() == 0 && counts.GetProperty("retrying").GetInt32() == 0;
        }, Patience, $"{minimum} deliveries to settle");

    private async Task<JsonElement> JobAsync(string token, Guid jobId) =>
        (await Json(HttpMethod.Get, "/api/portal/replay/jobs", token)).GetProperty("items").EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == jobId);

    private Task<JsonElement> InboxAsync(string token) => Json(HttpMethod.Get, "/api/portal/notifications?limit=100", token);

    private Task<HttpResponseMessage> PullRawAsync(string key, string query) =>
        platform.Api.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/v1/events" + (query.Length > 0 ? "?" + query : "")) { Headers = { Authorization = new("Bearer", key) } });

    private async Task<JsonElement> PullAsync(string key, string query)
    {
        var response = await PullRawAsync(key, query);
        Assert.True(response.IsSuccessStatusCode, $"GET /v1/events?{query} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await platform.ReadJsonAsync(response);
    }

    private async Task<int> SqlInt(string query) => await platform.ScalarAsAsync<int>("worker_user", query, system: true);

    private async Task<List<(string To, string Subject)>> MailAsync()
    {
        var response = await platform.Mail.GetFromJsonAsync<JsonElement>("/api/v1/messages?limit=200");
        return response.GetProperty("messages").EnumerateArray()
            .SelectMany(m => m.GetProperty("To").EnumerateArray().Select(t => (t.GetProperty("Address").GetString()!, m.GetProperty("Subject").GetString()!)))
            .ToList();
    }

    private async Task<HttpClient> KeycloakAdminAsync()
    {
        using var anonymous = new HttpClient { BaseAddress = platform.KeycloakUrl };
        var token = await anonymous.PostAsync("/realms/invoicehub/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "tenancy-admin", ["client_secret"] = "invoicehub-dev-admin-secret",
        }));
        token.EnsureSuccessStatusCode();
        var http = new HttpClient { BaseAddress = platform.KeycloakUrl };
        http.DefaultRequestHeaders.Authorization = new("Bearer", JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString());
        return http;
    }

    private async Task<string> KeycloakUserIdAsync(HttpClient admin, string email)
    {
        var users = await admin.GetFromJsonAsync<JsonElement>($"/admin/realms/invoicehub/users?email={Uri.EscapeDataString(email)}&exact=true");
        return users[0].GetProperty("id").GetString()!;
    }

    private async Task<string[]> KeycloakRolesAsync(string email)
    {
        using var admin = await KeycloakAdminAsync();
        var roles = await admin.GetFromJsonAsync<JsonElement>($"/admin/realms/invoicehub/users/{await KeycloakUserIdAsync(admin, email)}/role-mappings/realm");
        return roles.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).Where(n => n is "owner" or "admin" or "developer" or "support" or "viewer").Order().ToArray();
    }

    private async Task<bool> KeycloakEnabledAsync(string email)
    {
        using var admin = await KeycloakAdminAsync();
        var users = await admin.GetFromJsonAsync<JsonElement>($"/admin/realms/invoicehub/users?email={Uri.EscapeDataString(email)}&exact=true");
        return users[0].GetProperty("enabled").GetBoolean();
    }
}
