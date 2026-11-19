using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// M5 on the real stack, and the deck's demo scenes made executable: a 50 MB export travels as a claim-check envelope and is verified from Azurite, a KYC result travels as
/// ids only and is pulled with an API key (and audited), payload versions are projected per delivery, a second tenant has its own receiver, and a receiver that lost
/// events recovers them by pulling. Everything runs against Azurite, Keycloak, SQL Server, the Service Bus emulator and the real receiver apps.
/// </summary>
[Collection(PlatformCollection.Name)]
public partial class ClaimCheckAndPullTests(PlatformFixture platform) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(180);
    private static readonly Guid SeededContoso = Guid.Parse("0198a000-0000-7000-8000-0000000001e1");
    private static readonly Guid SeededFabrikam = Guid.Parse("0198a000-0000-7000-8000-0000000001e2");

    /// <summary>The demo keys that are seeded from infra/seed/seed.json (fixed on purpose, so the receivers can be configured without a manual step).</summary>
    private const string ContosoKey = "ihk_0198a000000070008000000000000001_0198a0000000700080000000000002a1_demoContosoReceiverKey2026-notASecret-xxxxxxxx";
    private const string FabrikamKey = "ihk_0198a000000070008000000000000002_0198a0000000700080000000000002b1_demoFabrikamReceiverKey2026-notASecret-xxxxxxx";

    public async ValueTask InitializeAsync() => await ResetAsync();

    public async ValueTask DisposeAsync() => await ResetAsync();

    private async Task ResetAsync()
    {
        await platform.Simulator.PostAsync("/simulator/stop-all", null);
        (await platform.Receiver.PutAsJsonAsync("/api/chaos", new { mode = "ok", delaySeconds = 15 })).EnsureSuccessStatusCode();
        (await platform.Receiver.PutAsJsonAsync("/api/trust", new { secrets = Array.Empty<string>(), jwksUrl = (string?)null })).EnsureSuccessStatusCode();
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

    // ---------------------------------------------------------------- demo scene 4: the export

    [Fact]
    public async Task A_50_megabyte_export_travels_as_a_tiny_envelope_and_the_receiver_verifies_the_file_from_blob_storage()
    {
        var response = await platform.Simulator.PostAsJsonAsync("/simulator/exports", new { tenant = "contoso", sizeMegabytes = 50 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // the runner writes the file to Azurite, the event announces it, delivery mints a link, the receiver downloads and hashes it
        await PlatformFixture.WaitUntilAsync(async () => (await Files()).Any(f => f.GetProperty("type").GetString() == "export.completed"), Patience, "the receiver to fetch the export");
        var file = (await Files()).First(f => f.GetProperty("type").GetString() == "export.completed");
        Assert.Equal("verified", file.GetProperty("verdict").GetString());
        Assert.InRange(file.GetProperty("actualSize").GetInt64(), 50L * 1024 * 1024, 50L * 1024 * 1024 + 200);
        Assert.Equal(file.GetProperty("expectedSha256").GetString(), file.GetProperty("actualSha256").GetString());

        // the webhook itself: about 600 bytes, a link and a checksum, and no trace of where the file really lives
        var webhookId = file.GetProperty("webhookId").GetString()!;
        var inbox = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{webhookId}");
        var body = inbox.GetProperty("body").GetString()!;
        Assert.True(body.Length < 1024, $"the envelope is {body.Length} bytes");
        using var envelope = JsonDocument.Parse(body);
        var data = envelope.RootElement.GetProperty("data");
        Assert.Equal("claim", envelope.RootElement.GetProperty("payload_style").GetString());
        Assert.False(data.TryGetProperty("file", out _));
        Assert.DoesNotContain("\"container\"", body, StringComparison.Ordinal); // the internal file reference (container, blob name, size) is not in the envelope; only the signed link names the file
        Assert.Equal(file.GetProperty("actualSha256").GetString(), data.GetProperty("sha256").GetString());
        var url = data.GetProperty("download").GetProperty("url").GetString()!;

        // the link is a short-lived signed address: tampered or stripped of its signature it opens nothing
        Assert.Equal(HttpStatusCode.OK, (await Download(url)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Download(Regex.Replace(url, "sig=[^&]+", "sig=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%3D"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Download(url[..url.IndexOf('?', StringComparison.Ordinal)])).StatusCode);

        // the platform's own views show the envelope without a working link and without the internal reference
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var delivery = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededContoso}&eventType=export.completed&limit=1", owner)).GetProperty("items")[0];
        var detail = await Json(HttpMethod.Get, $"/api/portal/deliveries/{delivery.GetProperty("id").GetGuid()}", owner);
        var shown = detail.GetProperty("request").GetProperty("body").GetString()!;
        Assert.Contains("a new link is minted for every attempt", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("claim-checks", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", shown, StringComparison.Ordinal);

        // the pull API shows size and checksum, never the file reference
        var pulled = await PullAsync(ContosoKey, "type=export.completed");
        var pulledData = pulled.GetProperty("items")[0].GetProperty("data");
        Assert.False(pulledData.TryGetProperty("file", out _));
        Assert.Equal(data.GetProperty("size_bytes").GetInt64(), pulledData.GetProperty("size_bytes").GetInt64());
    }

    [Fact]
    public async Task A_vendor_catalogue_import_announces_its_report_the_same_way()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await platform.Simulator.PostAsJsonAsync("/simulator/imports", new { tenant = "contoso", products = 800 })).StatusCode);
        await PlatformFixture.WaitUntilAsync(async () => (await Files()).Any(f => f.GetProperty("type").GetString() == "catalog.products.imported"), Patience, "the receiver to fetch the import report");

        var file = (await Files()).First(f => f.GetProperty("type").GetString() == "catalog.products.imported");
        Assert.Equal("verified", file.GetProperty("verdict").GetString());
        var inbox = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{file.GetProperty("webhookId").GetString()}");
        using var body = JsonDocument.Parse(inbox.GetProperty("body").GetString()!);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(800, data.GetProperty("products_added").GetInt32() + data.GetProperty("products_updated").GetInt32() + data.GetProperty("rejected_count").GetInt32());
        Assert.False(data.TryGetProperty("file", out _));
    }

    // ---------------------------------------------------------------- demo scene 5: thin KYC and the pull

    [Fact]
    public async Task An_identity_check_travels_as_ids_only_and_the_details_are_pulled_with_a_scoped_key_that_leaves_an_audit_line()
    {
        const string canary = "Canary Testperson 7f3a91";
        Assert.Equal(HttpStatusCode.Accepted, (await platform.Simulator.PostAsJsonAsync("/simulator/kyc", new { tenant = "contoso", count = 3, canaryName = canary })).StatusCode);

        // the receiver got a thin event and pulled the details itself
        await PlatformFixture.WaitUntilAsync(async () => (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/kyc")).GetArrayLength() >= 3, Patience, "the receiver to pull three verifications");
        var results = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/kyc")).EnumerateArray().ToList();
        Assert.Contains(results, r => r.GetProperty("name").GetString() == canary);

        // ASR-12: the personal data is nowhere on the wire or in the platform's event store
        var inbox = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/inbox?limit=100")).EnumerateArray().Where(i => i.GetProperty("type").GetString() == "kyc.completed").ToList();
        Assert.Equal(3, inbox.Count);
        foreach (var row in inbox)
        {
            var detail = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{row.GetProperty("webhookId").GetString()}");
            var raw = detail.GetProperty("body").GetString()!;
            Assert.DoesNotContain("Canary", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("document", raw, StringComparison.OrdinalIgnoreCase);
            using var parsed = JsonDocument.Parse(raw);
            Assert.Equal("thin", parsed.RootElement.GetProperty("payload_style").GetString());
            Assert.Equal(["verification_id"], parsed.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name).ToArray());
        }
        Assert.Equal(0, await Sql("SELECT COUNT(*) FROM events.event WHERE data_json LIKE N'%Canary%' OR data_json LIKE N'%Testperson%'"));
        Assert.Equal(0, await Sql("SELECT COUNT(*) FROM webhooks.delivery_attempt WHERE ISNULL(response_snippet, N'') LIKE N'%Canary%'"));

        // the pull needs the scope, and tenants cannot read each other's verifications
        var verificationId = results.First(r => r.GetProperty("name").GetString() == canary).GetProperty("verificationId").GetString()!;
        var direct = await Pull($"/v1/kyc/{verificationId}", ContosoKey);
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal(canary, (await platform.ReadJsonAsync(direct)).GetProperty("subject").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Pull($"/v1/kyc/{verificationId}", FabrikamKey)).StatusCode); // another tenant's key: simply not found
        var fresh = await platform.StepUpTokenAsync("developer@contoso.test");
        var eventsOnly = (await Json(HttpMethod.Post, "/api/portal/api-keys", fresh, new { name = "Events only", scopes = new[] { "events:read" } })).GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await Pull($"/v1/kyc/{verificationId}", eventsOnly)).StatusCode);

        // every read is on the audit log, attributed to the key (never to a person), with no personal data in the line
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var audit = await Json(HttpMethod.Get, "/api/portal/audit?action=kyc.read&limit=50", owner);
        var reads = audit.GetProperty("items").EnumerateArray().ToList();
        Assert.True(reads.Count >= 4, $"expected at least the receiver's three pulls and ours, found {reads.Count}");
        Assert.All(reads, l => Assert.StartsWith("api-key:ihk_", l.GetProperty("actor").GetString(), StringComparison.Ordinal));
        Assert.DoesNotContain(canary, audit.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(reads, l => l.GetProperty("targetId").GetString() == verificationId);
    }

    // ---------------------------------------------------------------- demo scene 6: tenant isolation

    [Fact]
    public async Task One_tenant_cannot_see_open_or_change_the_other_tenants_endpoint_even_with_its_id()
    {
        var contoso = await platform.DemoTokenAsync("owner@contoso.test");
        var fabrikam = await platform.DemoTokenAsync("owner@fabrikam.test");

        Assert.Contains((await Json(HttpMethod.Get, "/api/portal/endpoints", fabrikam)).GetProperty("items").EnumerateArray(), i => i.GetProperty("summary").GetProperty("name").GetString() == "Fabrikam ERP");
        Assert.DoesNotContain((await Json(HttpMethod.Get, "/api/portal/endpoints", contoso)).GetProperty("items").EnumerateArray(), i => i.GetProperty("summary").GetProperty("name").GetString() == "Fabrikam ERP");

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/portal/endpoints/{SeededFabrikam}"), (HttpMethod.Post, $"/api/portal/endpoints/{SeededFabrikam}/pause"), (HttpMethod.Delete, $"/api/portal/endpoints/{SeededFabrikam}"),
            (HttpMethod.Post, $"/api/portal/endpoints/{SeededFabrikam}/ping"), (HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededFabrikam}"),
        })
        {
            var response = await platform.Api.SendAsync(Request(method, path, contoso));
            if (path.Contains("deliveries", StringComparison.Ordinal))
                Assert.Equal(0, (await platform.ReadJsonAsync(response)).GetProperty("items").GetArrayLength()); // a list is simply empty, with no hint that the id exists
            else
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // and the database says the same without any help from the API: the wrong tenant context sees nothing
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", $"SELECT COUNT(*) FROM webhooks.endpoint WHERE id = '{SeededFabrikam}'", Guid.Parse("0198a000-0000-7000-8000-000000000001")));
        Assert.Equal(1, await platform.ScalarAsAsync<int>("api_user", $"SELECT COUNT(*) FROM webhooks.endpoint WHERE id = '{SeededFabrikam}'", Guid.Parse("0198a000-0000-7000-8000-000000000002")));
    }

    [Fact]
    public async Task The_second_tenant_has_its_own_receiver_and_the_two_never_see_each_others_events()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "fabrikam", count = 3, perSecond = 20, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0 })).StatusCode);
        await PlatformFixture.WaitUntilAsync(async () => (await Stats(platform.Receiver2)).GetProperty("received").GetInt32() >= 6, Patience, "Fabrikam's receiver to get its events");
        Assert.Equal(0, (await Stats(platform.Receiver)).GetProperty("received").GetInt32()); // Contoso's receiver heard nothing about Fabrikam's business

        await PlatformFixture.WaitUntilAsync(async () => (await platform.Receiver2.GetFromJsonAsync<JsonElement>("/api/invoices?limit=20")).EnumerateArray().Count(i => i.GetProperty("status").GetString() == "paid") == 3, Patience, "all three invoices to be paid in Fabrikam's mirror");
    }

    // ---------------------------------------------------------------- payload versions

    [Fact]
    public async Task A_delivery_keeps_the_payload_version_it_was_created_with_even_after_the_endpoint_moves_on()
    {
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        // the seeded endpoint would deliver the same events (same webhook-id) to the same receiver and win the race: it sits this one out, and is released afterwards
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{SeededContoso}/pause", owner))).EnsureSuccessStatusCode();
        try
        {
        var invalid = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/endpoints", owner, new { name = "Bad version", url = Hook("v-bad"), events = new[] { "invoice.paid" }, apiVersion = "2030-01-01" }));
        Assert.Equal("invalid_api_version", (await platform.ReadJsonAsync(invalid)).GetProperty("code").GetString());

        var created = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/endpoints", owner, new { name = "Versions", url = Hook("versions"), events = new[] { "invoice.paid" }, apiVersion = "2026-06-01" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await platform.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        await Trust(await platform.HmacSecretOfAsync(id));
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", owner)).GetProperty("ok").GetBoolean());

        // the diff the portal shows before an upgrade, on a real sample
        var diff = await Json(HttpMethod.Get, "/api/portal/api-versions/diff?from=2026-11-01&to=2027-02-01&type=invoice.paid", owner);
        Assert.True(diff.GetProperty("to").GetProperty("data").TryGetProperty("total", out _));
        Assert.True(diff.GetProperty("from").GetProperty("data").TryGetProperty("amount", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.Api.SendAsync(Request(HttpMethod.Get, "/api/portal/api-versions/diff?from=1999-01-01&to=2027-02-01&type=invoice.paid", owner))).StatusCode);

        // while paused, one event is created under 2026-06-01, the endpoint is moved to the preview, and a second event is created under 2027-02-01
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/pause", owner))).EnsureSuccessStatusCode();
        await Simulate("contoso", 1);
        await WaitHeld(owner, id, 1);
        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}", owner, new { apiVersion = "2027-02-01" });
        await Simulate("contoso", 1);
        await WaitHeld(owner, id, 2);

        var held = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={id}&status=held", owner)).GetProperty("items").EnumerateArray().ToList();
        var detailOld = await Json(HttpMethod.Get, $"/api/portal/deliveries/{held.Last().GetProperty("id").GetGuid()}", owner);
        var detailNew = await Json(HttpMethod.Get, $"/api/portal/deliveries/{held.First().GetProperty("id").GetGuid()}", owner);
        Assert.Equal("2026-06-01", detailOld.GetProperty("delivery").GetProperty("apiVersion").GetString());
        Assert.Equal("2027-02-01", detailNew.GetProperty("delivery").GetProperty("apiVersion").GetString());

        // release both: the first still arrives in the OLD shape, the second in the NEW one
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", owner)).GetProperty("ok").GetBoolean());
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/resume-guided", owner, new { mode = "deliver_all" }))).EnsureSuccessStatusCode();
        await PlatformFixture.WaitUntilAsync(async () => (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/inbox?limit=20")).EnumerateArray().Count(r => r.GetProperty("type").GetString() == "invoice.paid" && !r.GetProperty("test").GetBoolean()) >= 2, Patience, "both held events to arrive");

        var rows = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/inbox?limit=20")).EnumerateArray().Where(r => r.GetProperty("type").GetString() == "invoice.paid" && !r.GetProperty("test").GetBoolean()).ToList();
        Assert.Equal(2, rows.Count);
        var shapes = new Dictionary<string, JsonElement>();
        foreach (var row in rows)
        {
            var body = (await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{row.GetProperty("webhookId").GetString()}")).GetProperty("body").GetString()!;
            using var parsed = JsonDocument.Parse(body);
            shapes[parsed.RootElement.GetProperty("api_version").GetString()!] = parsed.RootElement.GetProperty("data").Clone();
        }
        Assert.True(shapes["2026-06-01"].TryGetProperty("customer_name", out _));
        Assert.False(shapes["2026-06-01"].TryGetProperty("customer", out _));
        Assert.True(shapes["2027-02-01"].TryGetProperty("total", out var total));
        Assert.True(total.TryGetProperty("amount", out _));
        Assert.False(shapes["2027-02-01"].TryGetProperty("amount", out _));

        // the receiver understood both (it reads whichever shape arrives)
        await PlatformFixture.WaitUntilAsync(async () => (await Stats(platform.Receiver)).GetProperty("processed").GetInt32() >= 2, Patience, "both events to be applied");
        var mirror = await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/invoices?limit=10");
        Assert.All(mirror.EnumerateArray(), i => Assert.NotEqual(JsonValueKind.Null, i.GetProperty("amount").ValueKind));
        }
        finally
        {
            await ReleaseSeededAsync(owner);
        }
    }

    // ---------------------------------------------------------------- "did I miss anything?" and the trace

    [Fact]
    public async Task A_receiver_that_lost_its_inbox_recovers_by_pulling_the_event_stream_and_processes_it_the_normal_way()
    {
        var since = DateTime.UtcNow.AddSeconds(-1);
        await Simulate("contoso", 4);
        await PlatformFixture.WaitUntilAsync(async () => (await Stats(platform.Receiver)).GetProperty("received").GetInt32() >= 12 && (await Stats(platform.Receiver)).GetProperty("waiting").GetInt32() == 0, Patience, "the events to be delivered");

        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededContoso}&limit=1&from={Uri.EscapeDataString(since.ToString("O"))}", owner)).GetProperty("counts");
            return counts.GetProperty("succeeded").GetInt32() >= 12 && counts.GetProperty("pending").GetInt32() == 0 && counts.GetProperty("retrying").GetInt32() == 0;
        }, Patience, "every delivery of this run to be finished");

        // the receiving system loses everything it had (a restore from an old backup, a bug, a wiped table)
        (await platform.Receiver.PostAsync("/api/reset", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Stats(platform.Receiver)).GetProperty("received").GetInt32());

        var response = await platform.Receiver.PostAsJsonAsync("/api/reconcile", new { hours = 1, since });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await platform.ReadJsonAsync(response);
        // 4 invoices: generated, paid and the payment, as the pull API lists them (no subscription needed). The tenant's stream may also hold a platform notice or two from the same second
        // (an endpoint warning raised by an earlier test), which a receiver that lost everything would of course get as well
        Assert.InRange(result.GetProperty("missing").GetInt32(), 12, 20);
        Assert.Equal(result.GetProperty("missing").GetInt32(), result.GetProperty("recovered").GetInt32());

        // recovered events go through the same inbox: idempotent, sequence-guarded, applied
        await PlatformFixture.WaitUntilAsync(async () => (await Stats(platform.Receiver)).GetProperty("waiting").GetInt32() == 0, Patience, "the recovered events to be processed");
        var stats = await Stats(platform.Receiver);
        Assert.Equal(stats.GetProperty("received").GetInt32(), stats.GetProperty("recovered").GetInt32());
        var rebuilt = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/invoices?limit=50")).EnumerateArray().ToList();
        Assert.True(rebuilt.Count(i => i.GetProperty("status").GetString() == "paid") >= 4, "the four invoices of this test are back in the receiver's books, paid");

        // asking again finds nothing missing
        var again = await platform.ReadJsonAsync(await platform.Receiver.PostAsJsonAsync("/api/reconcile", new { hours = 1, since }));
        Assert.Equal(0, again.GetProperty("missing").GetInt32());

        // without a working key the receiver says so instead of pretending
        (await platform.Receiver.PutAsJsonAsync("/api/pull-config", new { apiUrl = (string?)null, apiKey = "ihk_" + new string('x', 70) })).EnsureSuccessStatusCode();
        try
        {
            var refused = await platform.Receiver.PostAsJsonAsync("/api/reconcile", new { hours = 1, since });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("error", (await platform.ReadJsonAsync(refused)).GetProperty("problem").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (await platform.Receiver.PutAsJsonAsync("/api/pull-config", new { apiUrl = (string?)null, apiKey = ContosoKey })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task The_trace_id_of_a_delivery_attempt_is_the_trace_the_receiver_saw()
    {
        await Simulate("contoso", 1);
        await PlatformFixture.WaitUntilAsync(async () => (await Stats(platform.Receiver)).GetProperty("received").GetInt32() >= 2, Patience, "events to be delivered");

        var row = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/inbox?limit=10")).EnumerateArray().First(r => r.GetProperty("traceParent").ValueKind == JsonValueKind.String);
        var traceParent = row.GetProperty("traceParent").GetString()!;
        Assert.True(Regex.IsMatch(traceParent, "^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$"), $"traceparent as received: [{traceParent}]"); // W3C trace context (any flags: 01 sampled, 03 sampled + random trace id), sent by the platform's HTTP client

        var owner = await platform.DemoTokenAsync("owner@contoso.test");
        var delivery = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededContoso}&q={row.GetProperty("webhookId").GetString()}", owner)).GetProperty("items")[0];
        var detail = await Json(HttpMethod.Get, $"/api/portal/deliveries/{delivery.GetProperty("id").GetGuid()}", owner);
        var attemptTrace = detail.GetProperty("attempts")[0].GetProperty("traceId").GetString();
        Assert.Equal(traceParent.Split('-')[1], attemptTrace); // one trace from the platform's log to the receiver's inbox
        var committed = await platform.ScalarAsAsync<string>("worker_user", $"SELECT trace_parent FROM events.event WHERE id = '{Guid.ParseExact(row.GetProperty("webhookId").GetString()!["evt_".Length..], "N")}'", system: true);
        Assert.Equal(committed.Split('-')[1], attemptTrace); // and back to the business commit that started it
    }

    // ---------------------------------------------------------------- helpers

    private string Hook(string key) => new Uri(platform.ReceiverUrl, $"hooks/{key}").ToString();

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

    private Task<HttpResponseMessage> Pull(string path, string key) => platform.Api.SendAsync(new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new("Bearer", key) } });

    private async Task<JsonElement> PullAsync(string key, string query)
    {
        var response = await Pull($"/v1/events?{query}", key);
        Assert.True(response.IsSuccessStatusCode, $"GET /v1/events?{query} -> {(int)response.StatusCode}");
        return await platform.ReadJsonAsync(response);
    }

    private static async Task<HttpResponseMessage> Download(string url)
    {
        using var http = new HttpClient();
        return await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    }

    private async Task<List<JsonElement>> Files() => (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/files")).EnumerateArray().ToList();

    private static async Task<JsonElement> Stats(HttpClient receiver) => (await receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");

    private async Task Trust(string secret) => (await platform.Receiver.PutAsJsonAsync("/api/trust", new { secrets = new[] { secret }, jwksUrl = (string?)null })).EnsureSuccessStatusCode();

    private async Task Simulate(string tenant, int invoices) =>
        Assert.Equal(HttpStatusCode.Accepted, (await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant, count = invoices, perSecond = 20, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0 })).StatusCode);

    private async Task WaitHeld(string token, Guid endpointId, int atLeast) =>
        await PlatformFixture.WaitUntilAsync(async () => (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={endpointId}&limit=1", token)).GetProperty("counts").GetProperty("held").GetInt32() >= atLeast, Patience, $"{atLeast} held deliveries");

    /// <summary>Brings the seeded endpoint back the way a person would: a passing test, then deliver what was held.</summary>
    private async Task ReleaseSeededAsync(string owner)
    {
        await Trust(WebhookSeedSecret);
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{SeededContoso}/ping", owner)).GetProperty("ok").GetBoolean());
        (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{SeededContoso}/resume-guided", owner, new { mode = "deliver_all" }))).EnsureSuccessStatusCode();
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = (await Json(HttpMethod.Get, $"/api/portal/deliveries?endpointId={SeededContoso}&limit=1", owner)).GetProperty("counts");
            return counts.GetProperty("pending").GetInt32() == 0 && counts.GetProperty("retrying").GetInt32() == 0 && counts.GetProperty("held").GetInt32() == 0;
        }, Patience, "the seeded endpoint to be caught up");
    }

    private const string WebhookSeedSecret = "whsec_AqUfFzouCVdRJvYxal/3v+ozQ3V97EioucsCk+oTwL0=";

    private Task<int> Sql(string query) => platform.ScalarAsAsync<int>("worker_user", query, system: true);
}
