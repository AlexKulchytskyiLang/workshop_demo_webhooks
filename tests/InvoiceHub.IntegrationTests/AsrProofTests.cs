using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// The scenarios of the ASR register that need a misbehaving neighbour or a change of keys in the middle of a run (ASR-06, ASR-07, ASR-09, ASR-13). Real stack, real receivers,
/// the real simulator as the load: each test states the register's scenario and its measurable response, and asserts exactly that.
/// </summary>
[Collection(PlatformCollection.Name)]
public class AsrProofTests(PlatformFixture platform) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(180);
    private static readonly Guid SeededContoso = Guid.Parse("0198a000-0000-7000-8000-0000000001e1");
    private static readonly Guid SeededFabrikam = Guid.Parse("0198a000-0000-7000-8000-0000000001e2");

    public async ValueTask InitializeAsync() => await ResetAsync();

    public async ValueTask DisposeAsync() => await ResetAsync();

    private async Task ResetAsync()
    {
        await platform.Simulator.PostAsync("/simulator/stop-all", null);
        foreach (var receiver in new[] { platform.Receiver, platform.Receiver2 })
        {
            (await receiver.PutAsJsonAsync("/api/chaos", new { mode = "ok", delaySeconds = 15 })).EnsureSuccessStatusCode();
            (await receiver.PutAsJsonAsync("/api/trust", new { secrets = Array.Empty<string>(), jwksUrl = (string?)null })).EnsureSuccessStatusCode();
        }
        // what these scenarios leave behind (a backlog waiting behind a hung or failing receiver, an open circuit, an endpoint switched off for failing) must not leak into the next test:
        // the seeded endpoints are given back clean, and whatever was still waiting is closed as test leftovers
        // (attempts that were already on the wire finish and record themselves a few seconds later, so the cleanup repeats until none is left in flight)
        var seededIds = $"'{SeededContoso}', '{SeededFabrikam}'";
        await PlatformFixture.WaitUntilAsync(async () => await platform.ScalarAsAsync<int>("worker_user",
            $"UPDATE webhooks.delivery SET status = 'dead', last_error = N'test cleanup', completed_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE endpoint_id IN ({seededIds}) AND status IN ('pending', 'retry_scheduled'); " +
            $"UPDATE webhooks.endpoint SET status = 'active', disabled_reason = NULL, consecutive_failures = 0, failing_since = NULL, circuit_open_until = NULL, circuit_probe_step = 0 WHERE id IN ({seededIds}) AND deleted_at IS NULL; " +
            $"SELECT COUNT(*) FROM webhooks.delivery WHERE endpoint_id IN ({seededIds}) AND status = 'in_flight'", system: true) == 0, TimeSpan.FromSeconds(60), "the attempts still on the wire to finish");
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var list = await Json(HttpMethod.Get, "/api/portal/endpoints", owner);
        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("summary").GetProperty("id").GetGuid();
            if (id != SeededContoso) await platform.Api.SendAsync(Request(HttpMethod.Delete, $"/api/portal/endpoints/{id}", owner));
        }
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
        (await platform.Receiver2.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ASR_06_one_tenants_endpoint_that_hangs_on_every_call_does_not_slow_another_tenant()
    {
        // the register says "other tenants' p95 degrades by at most 5 s". Three runs of the same Fabrikam traffic: alone, next to a busy but healthy Contoso (the same load the hung run has),
        // and next to a Contoso whose receiver hangs on every call. What the HANG adds is the difference between the last two; the first shows what plain load alone does
        await FabrikamPhaseAsync(contosoLoad: false, contosoMode: "ok"); // warm-up, not measured: the first traffic after a boot pays for cold connections, plans and JIT
        var alone = await FabrikamPhaseAsync(contosoLoad: false, contosoMode: "ok");
        var healthyNeighbour = await FabrikamPhaseAsync(contosoLoad: true, contosoMode: "ok");
        // Contoso's receiver answers only after 15 s (the sender's timeout is 10 s, so every attempt there times out) while Fabrikam's receiver is healthy
        var hungNeighbour = await FabrikamPhaseAsync(contosoLoad: true, contosoMode: "slow");

        var added = hungNeighbour.P95 - healthyNeighbour.P95;
        var summary = $"Fabrikam's p95 commit-to-inbox: {alone.P95:0} ms alone, {healthyNeighbour.P95:0} ms next to a busy healthy neighbour, {hungNeighbour.P95:0} ms next to one that hangs on every call, " +
                      $"so the hang added {added:0} ms (the limit is 5000 ms). Where the time went, p95: alone [{alone.Stages}]; healthy neighbour [{healthyNeighbour.Stages}]; hung neighbour [{hungNeighbour.Stages}]";
        Record("ASR-06", summary);
        Assert.True(added <= 5000, summary);

        // and the noisy neighbour is contained: no more than 5 requests in flight at its receiver at any time is enforced by the slots (see the unit and pipeline tests);
        // here the visible consequence is that its own events are still waiting, not lost
        Assert.True((await Counts(platform.Receiver)).GetProperty("received").GetInt32() < 120);
    }

    [Fact]
    public async Task ASR_07_a_failing_receiver_is_not_hammered_while_its_circuit_is_open()
    {
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "fail500", delaySeconds = 15 })).EnsureSuccessStatusCode();
        await Simulate("contoso", 60, 30); // 180 deliveries are waiting for a receiver that is down

        await Task.Delay(TimeSpan.FromSeconds(20));
        var hits = (await Counts(platform.Receiver)).GetProperty("chaosHits").GetInt32();
        Assert.InRange(hits, 1, 80); // five failures open the circuit; after that only occasional probes reach the sick receiver, not 180 attempts and their retries

        // nothing is lost: when it recovers, the backlog drains
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "ok", delaySeconds = 15 })).EnsureSuccessStatusCode();
        await PlatformFixture.WaitUntilAsync(async () => (await Counts(platform.Receiver)).GetProperty("received").GetInt32() >= 180, Patience, "the backlog to drain after recovery");
    }

    [Fact]
    public async Task ASR_09_rotating_a_secret_while_deliveries_are_in_flight_causes_no_failed_verification()
    {
        var developer = await platform.StepUpTokenAsync("developer@contoso.test");
        var created = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/endpoints", developer, new { name = "Rotation under load", url = new Uri(platform.ReceiverUrl, "hooks/rotation").ToString(), events = new[] { "invoice.paid" } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await platform.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        var oldSecret = await platform.HmacSecretOfAsync(id);
        await Trust(oldSecret);
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", developer)).GetProperty("ok").GetBoolean());
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
        await Trust(oldSecret);

        await Simulate("contoso", 40, 10); // 4 seconds of traffic
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var rotated = await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/secret/rotate", developer, new { overlapHours = 24 }));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var newSecret = await platform.HmacSecretOfAsync(id);
        Assert.NotEqual(oldSecret, newSecret);

        // before the tenant has touched their configuration, requests are signed with BOTH keys, so a receiver that only knows the old one still verifies them
        await Task.Delay(TimeSpan.FromSeconds(1));
        // then the tenant switches over to the new secret only, in the middle of the run
        await Trust(newSecret);
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = await Counts(platform.Receiver);
            return counts.GetProperty("received").GetInt32() >= 120 && counts.GetProperty("waiting").GetInt32() == 0;
        }, Patience, "all events to be delivered through the rotation");

        var final = await Counts(platform.Receiver);
        Assert.Equal(0, final.GetProperty("rejected").GetInt32()); // not one failed verification, before, during or after the switch
        Assert.Equal(0, final.GetProperty("dead").GetInt32());

        // and the old key is still valid for the overlap, then gone from what we sign with after it
        var keys = (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", developer)).GetProperty("secrets").EnumerateArray().ToList();
        Assert.Equal(2, keys.Count(k => k.GetProperty("algorithm").GetString() == "hmac"));
        Assert.Single(keys, k => k.GetProperty("current").GetBoolean() && k.GetProperty("validUntil").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ASR_13_one_trace_id_follows_an_event_from_its_commit_to_the_receivers_inbox()
    {
        await Simulate("contoso", 3, 10);
        await PlatformFixture.WaitUntilAsync(async () => (await Counts(platform.Receiver)).GetProperty("received").GetInt32() >= 9, Patience, "the events to arrive");

        var inbox = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/inbox?limit=20")).EnumerateArray().Where(r => r.GetProperty("traceParent").ValueKind == JsonValueKind.String).Take(6).ToList();
        Assert.NotEmpty(inbox);
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        foreach (var row in inbox)
        {
            var webhookId = row.GetProperty("webhookId").GetString()!;
            var receiverTrace = row.GetProperty("traceParent").GetString()!.Split('-')[1];

            // the trace the platform recorded when the business change was committed
            var committed = await platform.ScalarAsAsync<string>("worker_user", $"SELECT trace_parent FROM events.event WHERE id = '{Guid.ParseExact(webhookId["evt_".Length..], "N")}'", system: true);
            Assert.Equal(committed.Split('-')[1], receiverTrace);

            // the trace of the delivery attempt shown in the portal's log
            var delivery = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededContoso}&q={webhookId}", owner)).GetProperty("items")[0];
            var detail = await Json(HttpMethod.Get, $"/api/portal/deliveries/{delivery.GetProperty("id").GetGuid()}", owner);
            Assert.Equal(receiverTrace, detail.GetProperty("attempts")[0].GetProperty("traceId").GetString());
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>One run of Fabrikam's traffic (30 invoices, 90 events) with Contoso either silent or running 40 invoices against a receiver in the given chaos mode.</summary>
    private async Task<(double P95, string Stages)> FabrikamPhaseAsync(bool contosoLoad, string contosoMode)
    {
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
        (await platform.Receiver2.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = contosoMode, delaySeconds = 15 })).EnsureSuccessStatusCode();
        var since = DateTime.UtcNow.AddSeconds(-1);
        if (contosoLoad) await Simulate("contoso", 40, 20);
        await Simulate("fabrikam", 30, 15);

        await PlatformFixture.WaitUntilAsync(async () => (await Counts(platform.Receiver2)).GetProperty("received").GetInt32() >= 90, Patience, $"Fabrikam's 90 events to arrive (Contoso: {(contosoLoad ? contosoMode : "silent")})");
        if (contosoLoad && contosoMode == "ok")
            await PlatformFixture.WaitUntilAsync(async () => (await Counts(platform.Receiver)).GetProperty("received").GetInt32() >= 120, Patience, "Contoso's own 120 events to arrive, so the next run starts clean");
        var latency = await platform.Receiver2.GetFromJsonAsync<JsonElement>($"/api/latency?since={Uri.EscapeDataString(since.ToString("O"))}");
        Assert.True(latency.GetProperty("count").GetInt32() >= 90);
        return (latency.GetProperty("p95").GetDouble(), await StageReportAsync(since));
    }

    /// <summary>The measured numbers of a passing run, kept next to the test results (the temp folder, or the file named by ASR_RESULTS_FILE) so the proof document can quote them.</summary>
    private static void Record(string asr, string text)
    {
        try { File.AppendAllText(Environment.GetEnvironmentVariable("ASR_RESULTS_FILE") ?? Path.Combine(Path.GetTempPath(), "invoicehub-asr-results.txt"), $"{DateTime.UtcNow:u} {asr}: {text}{Environment.NewLine}"); }
        catch (IOException) { /* the numbers are a convenience, never a reason to fail */ }
    }

    /// <summary>Per stage, how long Fabrikam's events waited: commit to delivery row (outbox, broker, fan-out), delivery row to first attempt (queue, workers), and how many needed a second try.</summary>
    private async Task<string> StageReportAsync(DateTime since)
    {
        var from = $"FROM webhooks.delivery d JOIN events.event e ON e.id = d.event_id WHERE d.endpoint_id = '{SeededFabrikam}' AND e.occurred_at >= '{since:yyyy-MM-dd HH:mm:ss}'";
        string P95(string diff) => $"SELECT CAST(MAX(p) AS varchar(20)) FROM (SELECT PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY ms) OVER () AS p FROM (SELECT {diff} AS ms {from}) a) b";
        var commitToDelivery = await platform.ScalarAsAsync<string>("worker_user", P95("DATEDIFF_BIG(millisecond, e.occurred_at, d.created_at)"), system: true);
        var deliveryToFirstAttempt = await platform.ScalarAsAsync<string>("worker_user", P95("DATEDIFF_BIG(millisecond, d.created_at, d.first_attempt_at)"), system: true);
        var retried = await platform.ScalarAsAsync<string>("worker_user", $"SELECT CAST(SUM(CASE WHEN d.attempt_count > 1 THEN 1 ELSE 0 END) AS varchar(10)) + ' of ' + CAST(COUNT(*) AS varchar(10)) {from}", system: true);
        return $"commit to delivery row {commitToDelivery}; delivery row to first attempt {deliveryToFirstAttempt}; retried {retried}";
    }

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

    private async Task Simulate(string tenant, int invoices, double perSecond) =>
        Assert.Equal(HttpStatusCode.Accepted, (await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant, count = invoices, perSecond, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0, maxInFlight = 64 })).StatusCode);

    private static async Task<JsonElement> Counts(HttpClient receiver) => (await receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");

    private async Task Trust(string secret) => (await platform.Receiver.PutAsJsonAsync("/api/trust", new { secrets = new[] { secret }, jwksUrl = (string?)null })).EnsureSuccessStatusCode();
}
