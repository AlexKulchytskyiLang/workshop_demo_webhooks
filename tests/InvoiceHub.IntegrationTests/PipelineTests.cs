using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InvoiceHub.Webhooks.Core;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// The whole delivery pipeline on the real stack: simulator -> billing -> outbox -> relay -> Service Bus -> fan-out -> delivery workers ->
/// signed HTTP -> the receiver app. Nothing is mocked. The platform is asynchronous by design, so these tests wait for outcomes.
/// </summary>
[Collection(PlatformCollection.Name)]
public class PipelineTests(PlatformFixture platform) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);
    private static readonly Guid ContosoTenant = Guid.Parse("0198a000-0000-7000-8000-000000000001");
    private static readonly Guid ContosoEndpoint = Guid.Parse("0198a000-0000-7000-8000-0000000001e1");
    private const string ContosoSecret = "whsec_AqUfFzouCVdRJvYxal/3v+ozQ3V97EioucsCk+oTwL0=";

    // every test starts from a quiet system and a clean receiver, and leaves one behind
    public async ValueTask InitializeAsync() => await QuiesceAsync();

    public async ValueTask DisposeAsync()
    {
        await SetChaosAsync("ok");
        await QuiesceAsync();
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task Simulated_invoices_arrive_signed_and_the_receiver_converges_on_the_right_state()
    {
        var since = DateTime.UtcNow; // exactly when this test starts: nothing from an earlier test can fall into the window
        await SimulateAsync(count: 5, payRatio: 1, overdueRatio: 0, failedRatio: 0);

        // 5 invoices x (invoice.generated + payment.completed + invoice.paid)
        await WaitForCountsAsync(received: 15);

        var stats = await StatsAsync();
        Assert.Equal(0, stats.GetProperty("rejected").GetInt32());
        Assert.Equal(0, stats.GetProperty("dead").GetInt32());
        Assert.Equal(5, stats.GetProperty("invoices").GetInt32());

        // the events are produced a moment apart and delivered in parallel, so they may arrive in any order: the sequence guard makes the end state right anyway
        var invoices = await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/invoices?limit=100");
        Assert.Equal(5, invoices.GetArrayLength());
        Assert.All(invoices.EnumerateArray(), i =>
        {
            Assert.Equal("paid", i.GetProperty("status").GetString());
            Assert.Equal(2, i.GetProperty("sequence").GetInt64());
        });

        // the platform's own books agree: one delivery per event, all succeeded, and every event confirmed by fan-out
        Assert.Equal(15, await SqlAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery WHERE status = 'succeeded' AND created_at > '{since:O}'"));
        Assert.Equal(0, await SqlAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery WHERE status <> 'succeeded' AND created_at > '{since:O}'"));
        Assert.Equal(0, await SqlAsync<int>($"SELECT COUNT(*) FROM events.outbox WHERE created_at > '{since:O}' AND fanned_out_at IS NULL"));
        Assert.Equal(15, await SqlAsync<int>($"SELECT COUNT(*) FROM events.event e WHERE e.occurred_at > '{since:O}' AND EXISTS (SELECT 1 FROM webhooks.delivery d WHERE d.event_id = e.id)"));
    }

    [Fact]
    public async Task Every_delivery_attempt_is_recorded_with_a_trace_id_and_status()
    {
        await SimulateAsync(count: 1, payRatio: 1, overdueRatio: 0, failedRatio: 0);
        await WaitForCountsAsync(received: 3);

        var list = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/portal/deliveries?limit=5", await DeveloperTokenAsync())));
        var first = list.GetProperty("items")[0];
        var detail = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get($"/api/portal/deliveries/{first.GetProperty("id").GetGuid()}", await DeveloperTokenAsync())));

        var attempt = detail.GetProperty("attempts")[0];
        Assert.Equal("success", attempt.GetProperty("outcome").GetString());
        Assert.Equal(200, attempt.GetProperty("statusCode").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", attempt.GetProperty("traceId").GetString()!);
        Assert.Equal("succeeded", detail.GetProperty("delivery").GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- resilience

    [Fact]
    public async Task A_failing_receiver_loses_nothing_events_are_retried_until_it_recovers()
    {
        await SetChaosAsync("fail500");
        await SimulateAsync(count: 4, payRatio: 1, overdueRatio: 0, failedRatio: 0);

        // the receiver is being hit and answering 500; nothing may be accepted yet
        await PlatformFixture.WaitUntilAsync(async () => (await StatsAsync()).GetProperty("chaosHits").GetInt32() >= 4, Patience, "failed attempts reaching the sick receiver");
        Assert.Equal(0, (await StatsAsync()).GetProperty("received").GetInt32());

        await SetChaosAsync("ok");
        await WaitForCountsAsync(received: 12); // 4 invoices x 3 events: all of them, none lost

        Assert.True(await SqlAsync<int>("SELECT COUNT(*) FROM webhooks.delivery WHERE attempt_count > 1 AND status = 'succeeded'") > 0, "at least one delivery needed a retry");
        Assert.True(await SqlAsync<int>("SELECT COUNT(*) FROM webhooks.delivery_attempt WHERE outcome = 'server_error' AND status_code = 500") > 0, "the failed attempts are on record");

        // the endpoint heals: breaker closed, failure counter reset
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var endpoint = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get($"/api/portal/endpoints/{ContosoEndpoint}", await DeveloperTokenAsync())));
            return endpoint.GetProperty("endpoint").GetProperty("health").GetString() == "healthy";
        }, Patience, "the endpoint to report healthy again");
    }

    [Fact]
    public async Task A_paused_endpoint_holds_its_events_and_resume_releases_all_of_them()
    {
        var token = await OwnerTokenAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await platform.Api.SendAsync(Post($"/api/portal/endpoints/{ContosoEndpoint}/pause", token))).StatusCode);
        try
        {
            await SimulateAsync(count: 3, payRatio: 1, overdueRatio: 0, failedRatio: 0);
            await PlatformFixture.WaitUntilAsync(async () => await SqlAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery WHERE endpoint_id = '{ContosoEndpoint}' AND status = 'held'") >= 9,
                Patience, "9 events to be held");
            Assert.Equal(0, (await StatsAsync()).GetProperty("received").GetInt32()); // held, not delivered, and not dropped
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await platform.Api.SendAsync(Post($"/api/portal/endpoints/{ContosoEndpoint}/resume", token))).StatusCode);
        }

        await WaitForCountsAsync(received: 9);
        Assert.Equal(0, await SqlAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery WHERE endpoint_id = '{ContosoEndpoint}' AND status = 'held'"));
    }

    [Fact]
    public async Task An_attempt_abandoned_by_a_crashed_worker_is_recovered_by_the_sweeper()
    {
        var token = await OwnerTokenAsync();
        await platform.Api.SendAsync(Post($"/api/portal/endpoints/{ContosoEndpoint}/pause", token));
        await SimulateAsync(count: 1, payRatio: 1, overdueRatio: 0, failedRatio: 0);
        await PlatformFixture.WaitUntilAsync(async () => await SqlAsync<int>($"SELECT COUNT(*) FROM webhooks.delivery WHERE endpoint_id = '{ContosoEndpoint}' AND status = 'held'") >= 3, Patience, "3 held deliveries");

        // make it look exactly like a worker died mid-attempt: in flight, owned by a replica that no longer exists, lease long expired
        await SqlAsync<int>($"""
            UPDATE webhooks.endpoint SET status = 'active' WHERE id = '{ContosoEndpoint}';
            UPDATE webhooks.delivery SET status = 'in_flight', claimed_by = 'dead-replica:1', claimed_until = DATEADD(MINUTE, -5, SYSUTCDATETIME()), held_reason = NULL, held_until = NULL
            WHERE endpoint_id = '{ContosoEndpoint}' AND status = 'held';
            SELECT @@ROWCOUNT
            """);

        await WaitForCountsAsync(received: 3); // the sweeper reclaimed them and they were delivered
    }

    [Fact]
    public async Task An_event_the_broker_lost_is_republished_from_the_outbox()
    {
        // an event that was "published" a minute ago, but whose fan-out never confirmed it: exactly what a lost broker message looks like
        var eventId = Guid.CreateVersion7();
        var invoiceId = $"inv_{Guid.CreateVersion7():N}";
        var data = JsonSerializer.Serialize(new { invoice_id = invoiceId, number = "INV-LOST-1", amount = 10m, currency = "EUR", status = "open", customer = new { name = "Lost Message Ltd", country = "UA" } });
        await SqlAsync<int>($"""
            INSERT events.event (id, tenant_id, type, aggregate_id, sequence, data_json, occurred_at) VALUES ('{eventId}', '{ContosoTenant}', 'invoice.generated', '{invoiceId}', 1, N'{data}', SYSUTCDATETIME());
            INSERT events.outbox (event_id, tenant_id, published_at) VALUES ('{eventId}', '{ContosoTenant}', DATEADD(MINUTE, -1, SYSUTCDATETIME()));
            SELECT 1
            """, tenant: ContosoTenant);

        await WaitForCountsAsync(received: 1);
        Assert.Equal(1, await SqlAsync<int>($"SELECT COUNT(*) FROM events.outbox WHERE event_id = '{eventId}' AND fanned_out_at IS NOT NULL"));
        var invoices = await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/invoices");
        Assert.Contains(invoices.EnumerateArray(), i => i.GetProperty("invoiceId").GetString() == invoiceId);
    }

    // ---------------------------------------------------------------- the receiver side of the contract

    [Fact]
    public async Task The_same_event_sent_three_times_has_one_effect_and_bad_requests_are_refused()
    {
        var invoiceId = $"inv_{Guid.CreateVersion7():N}";
        var generated = Event(Guid.CreateVersion7(), "invoice.generated", invoiceId, sequence: 1, status: "open");
        var paid = Event(Guid.CreateVersion7(), "invoice.paid", invoiceId, sequence: 2, status: "paid");

        // out of order on purpose: "paid" first, then "generated"
        Assert.Equal(HttpStatusCode.OK, (await SendHookAsync(paid)).StatusCode);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await SendHookAsync(generated)).StatusCode); // 1 new + 2 duplicates: always 200

        await WaitForCountsAsync(received: 2);
        var stats = await StatsAsync();
        Assert.Equal(2, stats.GetProperty("duplicates").GetInt32());
        Assert.Equal(1, stats.GetProperty("staleIgnored").GetInt32()); // the late "generated" did not overwrite the newer "paid"

        var invoice = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/invoices")).EnumerateArray().Single(i => i.GetProperty("invoiceId").GetString() == invoiceId);
        Assert.Equal("paid", invoice.GetProperty("status").GetString());

        // refused: tampered body, wrong secret, 6-minute-old timestamp, missing headers
        var tampered = Event(Guid.CreateVersion7(), "invoice.paid", invoiceId, 3, "paid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendHookAsync(tampered, mutateBody: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendHookAsync(tampered, secret: WebhookSigning.GenerateSecret())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendHookAsync(tampered, timestamp: DateTimeOffset.UtcNow.AddMinutes(-6))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.Receiver.PostAsync("/hooks/contoso-erp", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);

        stats = await StatsAsync();
        Assert.Equal(4, stats.GetProperty("rejected").GetInt32());
        Assert.Equal(2, stats.GetProperty("received").GetInt32()); // nothing refused was stored as an event
    }

    // ---------------------------------------------------------------- tenant isolation and endpoint rules

    [Fact]
    public async Task Tenants_cannot_see_or_touch_each_others_endpoints_and_deliveries()
    {
        var contoso = await DeveloperTokenAsync();
        var fabrikam = await platform.DemoTokenAsync("owner@fabrikam.test");

        var own = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/portal/endpoints", contoso)));
        Assert.Contains(own.GetProperty("items").EnumerateArray(), e => e.GetProperty("summary").GetProperty("id").GetGuid() == ContosoEndpoint);

        // Fabrikam has its own receiving system (seeded since M5): it sees that and never anything of Contoso's
        var theirs = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/portal/endpoints", fabrikam)));
        Assert.DoesNotContain(theirs.GetProperty("items").EnumerateArray(), e => e.GetProperty("summary").GetProperty("id").GetGuid() == ContosoEndpoint);

        // a Contoso id used with a Fabrikam token is simply "not found": no hint that it exists
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(platform.Get($"/api/portal/endpoints/{ContosoEndpoint}", fabrikam))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(Post($"/api/portal/endpoints/{ContosoEndpoint}/pause", fabrikam))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(Post($"/api/portal/endpoints/{ContosoEndpoint}/ping", fabrikam))).StatusCode);

        await SimulateAsync(count: 1, payRatio: 1, overdueRatio: 0, failedRatio: 0);
        await WaitForCountsAsync(received: 3);
        var deliveries = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/portal/deliveries", fabrikam)));
        Assert.DoesNotContain(deliveries.GetProperty("items").EnumerateArray(), d => d.GetProperty("endpointId").GetGuid() == ContosoEndpoint); // Contoso's traffic is invisible to Fabrikam

        // and straight at the database, as the API's own login, with the wrong tenant: row-level security returns nothing of Contoso's
        var fabrikamTenant = Guid.Parse("0198a000-0000-7000-8000-000000000002");
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", $"SELECT COUNT(*) FROM webhooks.delivery WHERE endpoint_id = '{ContosoEndpoint}'", tenant: fabrikamTenant));
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", $"SELECT COUNT(*) FROM webhooks.delivery WHERE tenant_id = '{ContosoTenant}'", tenant: fabrikamTenant));
        Assert.True(await platform.ScalarAsAsync<int>("api_user", "SELECT COUNT(*) FROM webhooks.delivery", tenant: ContosoTenant) > 0);
    }

    [Fact]
    public async Task Endpoint_rules_are_enforced_by_the_API_not_just_the_screen()
    {
        var developer = await DeveloperTokenAsync();
        var viewer = await platform.DemoTokenAsync("viewer@contoso.test");
        var valid = new { name = "Rules check", url = $"{platform.ReceiverUrl}hooks/rules-check", events = new[] { "invoice.paid" } };

        // role: a viewer can look but not create
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Json(HttpMethod.Post, "/api/portal/endpoints", viewer, valid))).StatusCode);

        // SSRF and shape (ASR-10): each hostile address is refused with a specific code
        foreach (var (url, code) in new[]
        {
            ("https://169.254.169.254/latest/meta-data", "address_blocked"),
            ("https://127.0.0.1/hook", "address_blocked"),
            ("https://10.0.0.5/hook", "address_blocked"),
            ("https://[::1]/hook", "address_blocked"),
            ("http://example.com/hook", "https_required"),
            ("https://user:secret@example.com/hook", "url_credentials"),
            ("https://example.com:5432/hook", "port_blocked"),
            ("https://localhost:9999/hook", "address_blocked"), // loopback is allowed ONLY for the exact demo receiver host:port
        })
        {
            var response = await platform.Api.SendAsync(Json(HttpMethod.Post, "/api/portal/endpoints", developer, new { name = "x", url, events = new[] { "invoice.paid" } }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
        }

        // personal-data events cannot go out as fat payloads (ASR-12)
        var pii = await platform.Api.SendAsync(Json(HttpMethod.Post, "/api/portal/endpoints", developer, new { valid.name, valid.url, events = new[] { "kyc.completed" }, payloadStyle = "fat" }));
        Assert.Equal("pii_requires_thin", (await platform.ReadJsonAsync(pii)).GetProperty("code").GetString());
        Assert.Equal("unknown_event", (await platform.ReadJsonAsync(await platform.Api.SendAsync(Json(HttpMethod.Post, "/api/portal/endpoints", developer, new { valid.name, valid.url, events = new[] { "nope.nothing" } })))).GetProperty("code").GetString());

        // the happy path: created pending, a failing ping does not verify it, delete hides it
        var created = await platform.Api.SendAsync(Json(HttpMethod.Post, "/api/portal/endpoints", developer, valid));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await platform.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        var detail = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get($"/api/portal/endpoints/{id}", developer)));
        Assert.Equal("pending_verification", detail.GetProperty("endpoint").GetProperty("health").GetString());
        Assert.Equal(1, detail.GetProperty("secrets").GetArrayLength());

        var ping = await platform.ReadJsonAsync(await platform.Api.SendAsync(Post($"/api/portal/endpoints/{id}/ping", developer)));
        Assert.False(ping.GetProperty("ok").GetBoolean()); // the receiver does not know this endpoint's secret yet, so it rejects the signature
        Assert.Equal(401, ping.GetProperty("statusCode").GetInt32());
        Assert.Contains("signature", ping.GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);

        // revealing a secret needs a fresh sign-in; a plain token (no auth_time) is refused (step-up, ASR-09)
        var reveal = await platform.Api.SendAsync(Post($"/api/portal/endpoints/{id}/secret/reveal", developer));
        Assert.Equal(HttpStatusCode.Forbidden, reveal.StatusCode);
        Assert.Equal("step_up_required", (await platform.ReadJsonAsync(reveal)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await platform.Api.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/portal/endpoints/{id}") { Headers = { Authorization = new("Bearer", await OwnerTokenAsync()) } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Api.SendAsync(platform.Get($"/api/portal/endpoints/{id}", developer))).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private Task<string> DeveloperTokenAsync() => platform.DemoTokenAsync("developer@contoso.test");

    private Task<string> OwnerTokenAsync() => platform.DemoTokenAsync("owner@contoso.test");

    private static HttpRequestMessage Post(string url, string token) =>
        new(HttpMethod.Post, url) { Headers = { Authorization = new("Bearer", token) } };

    private static HttpRequestMessage Json(HttpMethod method, string url, string token, object body) =>
        new(method, url) { Headers = { Authorization = new("Bearer", token) }, Content = JsonContent.Create(body) };

    private async Task SimulateAsync(int count, double payRatio, double overdueRatio, double failedRatio)
    {
        var response = await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "contoso", count, perSecond = 20, payRatio, overdueRatio, failedPaymentRatio = failedRatio });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task<JsonElement> StatsAsync() =>
        (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");

    private async Task SetChaosAsync(string mode) =>
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode, delaySeconds = 15 })).EnsureSuccessStatusCode();

    private async Task WaitForCountsAsync(int received) =>
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var s = await StatsAsync();
            return s.GetProperty("received").GetInt32() >= received && s.GetProperty("waiting").GetInt32() == 0;
        }, Patience, $"{received} events to be received and processed");

    /// <summary>Nothing in flight anywhere, receiver empty and healthy: the baseline every test starts from.</summary>
    private async Task QuiesceAsync()
    {
        (await platform.Simulator.PostAsync("/simulator/stop-all", null)).EnsureSuccessStatusCode();
        await SetChaosAsync("ok");
        await PlatformFixture.WaitUntilAsync(async () =>
            await SqlAsync<int>("SELECT COUNT(*) FROM webhooks.delivery WHERE status IN ('pending', 'in_flight', 'retry_scheduled')") == 0
            && await SqlAsync<int>("SELECT COUNT(*) FROM events.outbox WHERE published_at IS NULL OR fanned_out_at IS NULL") == 0,
            Patience, "all deliveries to settle");
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
    }

    private Task<T> SqlAsync<T>(string query, Guid? tenant = null) =>
        platform.ScalarAsAsync<T>("worker_user", query, tenant: tenant, system: tenant is null);

    private static byte[] Event(Guid id, string type, string invoiceId, long sequence, string status)
    {
        var data = JsonSerializer.Serialize(new { invoice_id = invoiceId, number = "INV-T-1", amount = 99.5m, currency = "EUR", status, customer = new { name = "Test Customer", country = "UA" } });
        // the same builder the platform uses: the body is exactly what a real delivery would carry
        return PayloadBuilder.Build("fat", id, isTest: false, type, "2026-11-01", DateTimeOffset.UtcNow, invoiceId, sequence, data, []);
    }

    /// <summary>Sends a webhook to the receiver the way the platform does: raw bytes, Standard Webhooks headers.</summary>
    private Task<HttpResponseMessage> SendHookAsync(byte[] body, string secret = ContosoSecret, DateTimeOffset? timestamp = null, bool mutateBody = false)
    {
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signature = WebhookSigning.Sign(secret, id, ts, body);
        var sent = mutateBody ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("99.5", "9999.5", StringComparison.Ordinal)) : body;

        var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/contoso-erp") { Content = new ByteArrayContent(sent) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(WebhookSigning.IdHeader, id);
        request.Headers.Add(WebhookSigning.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSigning.SignatureHeader, signature);
        return platform.Receiver.SendAsync(request);
    }
}
