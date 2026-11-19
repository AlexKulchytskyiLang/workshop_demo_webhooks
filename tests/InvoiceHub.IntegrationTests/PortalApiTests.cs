using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// The endpoint workspace's API on the real stack (M3): checks while typing, delivery settings, headers and credentials, signing (HMAC, ES256, both),
/// the negative tests, and the overview numbers. Receivers are the real receiver app, configured the way a tenant would configure theirs.
/// </summary>
[Collection(PlatformCollection.Name)]
public class PortalApiTests(PlatformFixture platform) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private static readonly Guid SeededEndpoint = Guid.Parse("0198a000-0000-7000-8000-0000000001e1");

    public async ValueTask InitializeAsync() => await ResetAsync();

    public async ValueTask DisposeAsync() => await ResetAsync();

    private async Task ResetAsync()
    {
        // every endpoint a test created is deleted again: left behind it would keep receiving the simulator's events and retrying against a receiver that no longer trusts it
        var owner = await platform.DemoTokenAsync("owner@contoso.test");
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

    // ---------------------------------------------------------------- settings that need no receiver

    [Fact]
    public async Task Retry_presets_and_the_live_preview_come_from_the_same_code_that_delivers()
    {
        var token = await DeveloperAsync();
        var presets = await Json(HttpMethod.Get, "/api/portal/retry-presets", token);
        Assert.Equal(["standard", "aggressive", "demo"], presets.GetProperty("presets").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());

        var standard = await Json(HttpMethod.Post, "/api/portal/retry-preview", token, new { preset = "standard" });
        Assert.True(standard.GetProperty("valid").GetBoolean());
        Assert.Equal(5, standard.GetProperty("attempts")[1].GetProperty("delaySeconds").GetInt32()); // first retry after 5 s
        Assert.Equal(72 * 3600, standard.GetProperty("windowSeconds").GetInt32());

        var aggressive = await Json(HttpMethod.Post, "/api/portal/retry-preview", token, new { preset = "aggressive" });
        Assert.Equal(8, aggressive.GetProperty("attempts").GetArrayLength());

        var custom = await Json(HttpMethod.Post, "/api/portal/retry-preview", token, new { preset = "custom", custom = new { attempts = 4, backoff = "fixed", initialSeconds = 10, capSeconds = 600, windowSeconds = 3600 } });
        Assert.Equal([0, 10, 20, 30], custom.GetProperty("attempts").EnumerateArray().Select(a => a.GetProperty("atSeconds").GetInt32()).ToArray());

        // an impossible combination is explained, not silently accepted
        var bad = await Json(HttpMethod.Post, "/api/portal/retry-preview", token, new { preset = "custom", custom = new { attempts = 4, backoff = "fixed", initialSeconds = 600, capSeconds = 600, windowSeconds = 60 } });
        Assert.False(bad.GetProperty("valid").GetBoolean());
        Assert.Contains("shorter than the first retry", bad.GetProperty("errors")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delivery_settings_are_validated_before_they_are_saved_and_come_back_as_saved()
    {
        var token = await DeveloperAsync();
        var id = await CreateAsync(token, "Settings check");

        foreach (var (body, code) in new (object, string)[]
        {
            (new { preset = "turbo" }, "invalid_retry_preset"),
            (new { preset = "custom", custom = new { attempts = 1, backoff = "exp4", initialSeconds = 5, capSeconds = 600, windowSeconds = 3600 } }, "invalid_retry"),
            (new { circuitFailures = 1 }, "invalid_circuit"),
            (new { autoDisable = new { failures = 3, withinHours = 24, days = 5 } }, "invalid_auto_disable"),
            (new { circuitFailures = 20, autoDisable = new { failures = 10, withinHours = 24, days = 5 } }, "invalid_auto_disable"),
            (new { holdDays = 90 }, "invalid_hold"),
            (new { timeoutSeconds = 120 }, "invalid_timeout"),
            (new { maxInflight = 50 }, "invalid_concurrency"),
        })
        {
            var response = await platform.Api.SendAsync(Request(HttpMethod.Put, $"/api/portal/endpoints/{id}/delivery", token, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
        }

        var saved = await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/delivery", token, new
        {
            preset = "custom", custom = new { attempts = 6, backoff = "exp3", initialSeconds = 2, capSeconds = 900, windowSeconds = 7200 },
            circuitFailures = 3, autoDisable = new { failures = 50, withinHours = 12, days = 3 }, holdDays = 14, timeoutSeconds = 5, maxInflight = 2,
        });
        Assert.Equal("custom", saved.GetProperty("preset").GetString());
        Assert.Equal(14, saved.GetProperty("holdDays").GetInt32());

        var detail = await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", token);
        Assert.Equal(6, detail.GetProperty("delivery").GetProperty("custom").GetProperty("attempts").GetInt32());
        Assert.Equal(50, detail.GetProperty("delivery").GetProperty("autoDisable").GetProperty("failures").GetInt32());
        Assert.Equal(5, detail.GetProperty("endpoint").GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Checking_a_URL_explains_each_problem_and_warns_about_a_duplicate()
    {
        var token = await DeveloperAsync();

        var ok = await Json(HttpMethod.Post, "/api/portal/endpoints/check-url", token, new { url = ReceiverHook("check-ok") });
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Contains(ok.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "address" && c.GetProperty("status").GetString() == "ok");
        Assert.Contains(ok.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "ownership" && c.GetProperty("status").GetString() == "todo");

        foreach (var (url, check) in new[] { ("http://example.com/hook", "https"), ("https://169.254.169.254/latest", "address"), ("https://10.0.0.5/x", "address"), ("not a url", "format") })
        {
            var bad = await Json(HttpMethod.Post, "/api/portal/endpoints/check-url", token, new { url });
            Assert.False(bad.GetProperty("ok").GetBoolean(), url);
            Assert.Contains(bad.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == check && c.GetProperty("status").GetString() == "error");
        }

        var id = await CreateAsync(token, "Duplicate source", ReceiverHook("dup"));
        var dup = await Json(HttpMethod.Post, "/api/portal/endpoints/check-url", token, new { url = ReceiverHook("dup") });
        Assert.Contains(dup.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "duplicate" && c.GetProperty("status").GetString() == "warn");
        var same = await Json(HttpMethod.Post, "/api/portal/endpoints/check-url", token, new { url = ReceiverHook("dup"), endpointId = id });
        Assert.DoesNotContain(same.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "duplicate"); // editing the endpoint itself is not a duplicate
    }

    [Fact]
    public async Task Changing_the_signing_mode_rotating_and_revealing_each_need_a_fresh_sign_in()
    {
        var token = await DeveloperAsync(); // a plain token has no recent authentication time
        var id = await CreateAsync(token, "Step-up check");
        foreach (var (method, path, body) in new (HttpMethod, string, object)[]
        {
            (HttpMethod.Put, $"/api/portal/endpoints/{id}/signing", new { mode = "both" }),
            (HttpMethod.Post, $"/api/portal/endpoints/{id}/secret/rotate", new { overlapHours = 24 }),
            (HttpMethod.Post, $"/api/portal/endpoints/{id}/secret/reveal", new { }),
        })
        {
            var response = await platform.Api.SendAsync(Request(method, path, token, body));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("step_up_required", (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
        }

        // and a viewer may not even try
        var viewer = await platform.DemoTokenAsync("viewer@contoso.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.Api.SendAsync(Request(HttpMethod.Post, $"/api/portal/endpoints/{id}/secret/rotate", viewer, new { }))).StatusCode);
    }

    // ---------------------------------------------------------------- against the real receiver

    [Fact]
    public async Task An_HMAC_endpoint_is_verified_with_the_shared_secret_and_the_negative_tests_prove_the_receiver_is_safe()
    {
        var token = await DeveloperAsync();
        var id = await CreateAsync(token, "HMAC verify", ReceiverHook("hmac"));

        // before the receiver knows the secret: refused, with advice that points at the real cause
        var refused = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", token);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(401, refused.GetProperty("statusCode").GetInt32());
        Assert.Contains("signature", refused.GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("pending_verification", (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", token)).GetProperty("endpoint").GetProperty("health").GetString());

        // the tenant pastes the secret into their application
        await TrustAsync([await platform.HmacSecretOfAsync(id)], null);
        var verified = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", token);
        Assert.True(verified.GetProperty("ok").GetBoolean());
        var endpoint = (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", token)).GetProperty("endpoint");
        Assert.Equal("healthy", endpoint.GetProperty("health").GetString());
        Assert.NotEqual(JsonValueKind.Null, endpoint.GetProperty("verifiedAt").ValueKind);

        var normal = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid", scenario = "normal" });
        Assert.True(normal.GetProperty("ok").GetBoolean());
        Assert.Contains(normal.GetProperty("checks").EnumerateArray(), c => c.GetProperty("status").GetString() == "ok");
        Assert.Contains("evt_test_", normal.GetProperty("requestId").GetString(), StringComparison.Ordinal);

        // negative tests pass when the receiver REFUSES the bad request
        foreach (var scenario in new[] { "bad_signature", "old_timestamp" })
        {
            var result = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid", scenario });
            Assert.True(result.GetProperty("ok").GetBoolean(), scenario);
            Assert.Equal(401, result.GetProperty("statusCode").GetInt32());
        }

        var duplicate = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid", scenario = "duplicate" });
        Assert.True(duplicate.GetProperty("ok").GetBoolean());
        var stats = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");
        Assert.True(stats.GetProperty("duplicates").GetInt32() >= 1);
        Assert.True(stats.GetProperty("rejected").GetInt32() >= 3); // the unsigned ping, the tampered one and the stale one

        // test traffic is not business traffic: no delivery rows, no effect on health
        Assert.Equal(0, await Sql($"SELECT COUNT(*) FROM webhooks.delivery WHERE endpoint_id = '{id}'"));
    }

    [Fact]
    public async Task An_ES256_endpoint_publishes_its_public_key_and_the_receiver_verifies_without_any_secret()
    {
        var token = await DeveloperAsync();
        var id = await CreateAsync(token, "ES256 verify", ReceiverHook("es256"), signing: "es256");
        var jwksUrl = (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", token)).GetProperty("endpoint").GetProperty("jwksUrl").GetString()!;

        // anyone can fetch the public keys, without signing in; nothing private is in them
        var jwks = await platform.Api.GetFromJsonAsync<JsonElement>(new Uri(jwksUrl).PathAndQuery);
        Assert.Equal(1, jwks.GetProperty("keys").GetArrayLength());
        var key = jwks.GetProperty("keys")[0];
        Assert.Equal("ES256", key.GetProperty("alg").GetString());
        Assert.False(key.TryGetProperty("d", out _));

        // the private key can never be revealed, whoever asks (the step-up check comes first; an ES256-only endpoint has no shared secret at all)
        Assert.Equal(0, await Sql($"SELECT COUNT(*) FROM webhooks.endpoint_secret WHERE endpoint_id = '{id}' AND algorithm = 'hmac'"));

        await TrustAsync([], jwksUrl); // the receiver trusts the published key set, nothing secret
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", token)).GetProperty("ok").GetBoolean());
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "payment.completed" })).GetProperty("ok").GetBoolean());
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid", scenario = "bad_signature" })).GetProperty("ok").GetBoolean());

        // another endpoint's key set does not make this one's requests valid
        var other = await CreateAsync(token, "ES256 other", ReceiverHook("es256-other"), signing: "es256");
        var otherUrl = (await Json(HttpMethod.Get, $"/api/portal/endpoints/{other}", token)).GetProperty("endpoint").GetProperty("jwksUrl").GetString()!;
        await TrustAsync([], otherUrl);
        Assert.False((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", token)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task During_a_signing_migration_a_request_carries_both_signatures_and_either_kind_of_receiver_accepts_it()
    {
        var token = await DeveloperAsync();
        var id = await CreateAsync(token, "Both verify", ReceiverHook("both"), signing: "both");
        var jwksUrl = (await Json(HttpMethod.Get, $"/api/portal/endpoints/{id}", token)).GetProperty("endpoint").GetProperty("jwksUrl").GetString()!;
        var secret = await platform.HmacSecretOfAsync(id);

        await TrustAsync([secret], null); // an old receiver that only knows the shared secret
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/ping", token)).GetProperty("ok").GetBoolean());

        await TrustAsync([], jwksUrl); // a migrated receiver that only knows the public keys
        Assert.True((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid" })).GetProperty("ok").GetBoolean());

        await TrustAsync([WebhookSecret.Other], null); // a receiver that trusts neither
        Assert.False((await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid" })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Custom_headers_and_the_delivery_credential_reach_the_receiver_and_secret_values_never_come_back()
    {
        var token = await DeveloperAsync();
        var id = await CreateAsync(token, "Headers", ReceiverHook("headers"));
        await TrustAsync([await platform.HmacSecretOfAsync(id)], null);

        // unsafe headers are refused before they are saved
        foreach (var name in new[] { "webhook-id", "Host", "Content-Type", "bad name" })
        {
            var response = await platform.Api.SendAsync(Request(HttpMethod.Put, $"/api/portal/endpoints/{id}/headers", token, new { headers = new[] { new { name, value = "x", secret = false } } }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_headers", (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
        }
        var injected = await platform.Api.SendAsync(Request(HttpMethod.Put, $"/api/portal/endpoints/{id}/headers", token, new { headers = new[] { new { name = "X-A", value = "ok\r\nX-Evil: 1", secret = false } } }));
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);

        var saved = await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/headers", token, new
        {
            headers = new[] { new { name = "X-Target-Env", value = "staging", secret = false }, new { name = "X-Gateway-Key", value = "gw-s3cret", secret = true } },
        });
        var secretHeader = saved.GetProperty("headers").EnumerateArray().Single(h => h.GetProperty("name").GetString() == "X-Gateway-Key");
        Assert.Equal(JsonValueKind.Null, secretHeader.GetProperty("value").ValueKind); // the value is not echoed
        Assert.True(secretHeader.GetProperty("hasValue").GetBoolean());

        // the delivery credential: stored encrypted, never returned, and it may not collide with a custom header
        var clash = await platform.Api.SendAsync(Request(HttpMethod.Put, $"/api/portal/endpoints/{id}/auth", token, new { type = "header", headerName = "X-Gateway-Key", value = "k" }));
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        var auth = await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/auth", token, new { type = "header", headerName = "X-Api-Key", value = "api-k3y-123" });
        Assert.True(auth.GetProperty("hasValue").GetBoolean());
        Assert.DoesNotContain("api-k3y-123", auth.GetRawText(), StringComparison.Ordinal);

        // saving the secret header again without a value keeps it
        await Json(HttpMethod.Put, $"/api/portal/endpoints/{id}/headers", token, new
        {
            headers = new[] { new { name = "X-Target-Env", value = "production", secret = false }, new { name = "X-Gateway-Key", value = (string?)null, secret = true } },
        });

        var test = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "invoice.paid" });
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Equal("••••••••", test.GetProperty("request").GetProperty("headers").GetProperty("X-Api-Key").GetString()); // masked even in the test view

        var inbox = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{test.GetProperty("requestId").GetString()}");
        var headers = inbox.GetProperty("headers");
        Assert.Equal("production", headers.GetProperty("x-target-env").GetString());
        Assert.Equal("gw-s3cret", headers.GetProperty("x-gateway-key").GetString());
        Assert.Equal("api-k3y-123", headers.GetProperty("x-api-key").GetString());
    }

    [Fact]
    public async Task Personal_data_events_stay_thin_even_on_an_endpoint_set_to_auto()
    {
        var token = await DeveloperAsync();
        var id = await CreateAsync(token, "Thin by law", ReceiverHook("thin"), events: ["kyc.completed", "invoice.paid"], style: "auto");
        await TrustAsync([await platform.HmacSecretOfAsync(id)], null);

        var test = await Json(HttpMethod.Post, $"/api/portal/endpoints/{id}/test", token, new { eventType = "kyc.completed" });
        Assert.True(test.GetProperty("ok").GetBoolean());
        var inbox = await platform.Receiver.GetFromJsonAsync<JsonElement>($"/api/inbox/{test.GetProperty("requestId").GetString()}");
        using var body = JsonDocument.Parse(inbox.GetProperty("body").GetString()!);
        Assert.Equal("thin", body.RootElement.GetProperty("payload_style").GetString());
        Assert.Single(body.RootElement.GetProperty("data").EnumerateObject()); // just the reference id

        // and a fat endpoint cannot be switched on for it afterwards
        var fat = await platform.Api.SendAsync(Request(HttpMethod.Put, $"/api/portal/endpoints/{id}", token, new { payloadStyle = "fat" }));
        Assert.Equal("pii_requires_thin", (await platform.ReadJsonAsync(fat)).GetProperty("code").GetString());
    }

    // ---------------------------------------------------------------- numbers and filters from real traffic

    [Fact]
    public async Task A_filter_can_be_tested_against_the_latest_real_events_and_the_overview_shows_real_numbers()
    {
        var token = await DeveloperAsync();
        var response = await platform.Simulator.PostAsJsonAsync("/simulator/invoices", new { tenant = "contoso", count = 4, perSecond = 20, payRatio = 1, overdueRatio = 0, failedPaymentRatio = 0 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            var counts = (await platform.Receiver.GetFromJsonAsync<JsonElement>("/api/stats")).GetProperty("counts");
            return counts.GetProperty("received").GetInt32() >= 12 && counts.GetProperty("waiting").GetInt32() == 0;
        }, Patience, "12 events to be delivered");

        var everything = await Json(HttpMethod.Post, "/api/portal/endpoints/filter-test", token, new
        {
            eventType = "invoice.paid", filter = new { groups = new[] { new[] { new { path = "data.amount", op = "gte", value = 0 } } } },
        });
        Assert.True(everything.GetProperty("valid").GetBoolean());
        Assert.True(everything.GetProperty("total").GetInt32() >= 4);
        Assert.Equal(everything.GetProperty("total").GetInt32(), everything.GetProperty("matched").GetInt32());
        Assert.Equal("data.amount >= 0", everything.GetProperty("expression").GetString());

        var nothing = await Json(HttpMethod.Post, "/api/portal/endpoints/filter-test", token, new
        {
            eventType = "invoice.paid", filter = new { groups = new[] { new[] { new { path = "data.amount", op = "gt", value = 1_000_000 } } } },
        });
        Assert.Equal(0, nothing.GetProperty("matched").GetInt32());
        Assert.True(nothing.GetProperty("total").GetInt32() >= 4); // "matches nothing" is visible BEFORE saving, so it can be warned about

        var invalid = await Json(HttpMethod.Post, "/api/portal/endpoints/filter-test", token, new { eventType = "invoice.paid", filter = new { groups = new[] { new[] { new { path = "data.amount", op = "regex", value = "x" } } } } });
        Assert.False(invalid.GetProperty("valid").GetBoolean());
        Assert.Contains("Unknown operator", invalid.GetProperty("error").GetString(), StringComparison.Ordinal);

        var overview = await Json(HttpMethod.Get, "/api/portal/overview", token);
        var kpis = overview.GetProperty("kpis");
        Assert.True(kpis.GetProperty("deliveries24h").GetInt32() >= 12);
        Assert.InRange(kpis.GetProperty("successRate").GetDouble(), 1.0, 100.0); // a real percentage (the shared database also holds what the chaos and noisy-neighbour tests failed on purpose)
        Assert.True(kpis.GetProperty("avgLatencyMs").GetInt32() > 0);
        Assert.True(kpis.GetProperty("p95LatencyMs").GetInt32() >= kpis.GetProperty("avgLatencyMs").GetInt32() / 2);

        var list = await Json(HttpMethod.Get, "/api/portal/endpoints", token);
        var contoso = list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("summary").GetProperty("name").GetString() == "Contoso ERP");
        Assert.True(contoso.GetProperty("attempts24h").GetInt32() >= 12);
        Assert.Contains(contoso.GetProperty("series").EnumerateArray(), p => p.ValueKind == JsonValueKind.Number);
        Assert.Contains("invoice.paid", contoso.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
    }

    // ---------------------------------------------------------------- helpers

    private Task<string> DeveloperAsync() => platform.DemoTokenAsync("developer@contoso.test");

    private string ReceiverHook(string key) => new Uri(platform.ReceiverUrl, $"hooks/{key}").ToString();

    private async Task<Guid> CreateAsync(string token, string name, string? url = null, string signing = "hmac", string[]? events = null, string style = "fat")
    {
        var response = await platform.Api.SendAsync(Request(HttpMethod.Post, "/api/portal/endpoints", token, new
        {
            name, url = url ?? ReceiverHook(Guid.NewGuid().ToString("N")[..8]), events = events ?? ["invoice.paid", "payment.completed"], signing, payloadStyle = style,
        }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await platform.ReadJsonAsync(response)).GetProperty("id").GetGuid();
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

    private async Task TrustAsync(string[] secrets, string? jwksUrl) =>
        (await platform.Receiver.PutAsJsonAsync("/api/trust", new { secrets, jwksUrl })).EnsureSuccessStatusCode();

    private Task<int> Sql(string query) => platform.ScalarAsAsync<int>("worker_user", query, system: true);
}

/// <summary>A well-formed secret nobody uses: stands for "a receiver that trusts something else".</summary>
internal static class WebhookSecret
{
    public const string Other = "whsec_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}
