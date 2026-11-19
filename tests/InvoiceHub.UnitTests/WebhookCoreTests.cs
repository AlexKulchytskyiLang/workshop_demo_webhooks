using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using InvoiceHub.Webhooks.Core;

namespace InvoiceHub.UnitTests;

public class SigningTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"type\":\"invoice.paid\",\"data\":{\"amount\":10}}");

    [Fact]
    public void Our_signature_verifies_with_the_reference_Standard_Webhooks_library()
    {
        // interop proof (ASR-08): a receiver using the unmodified open-source library accepts what we send
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headers = new HeaderDictionary
        {
            [WebhookSigning.IdHeader] = "evt_123",
            [WebhookSigning.TimestampHeader] = ts.ToString(),
            [WebhookSigning.SignatureHeader] = WebhookSigning.SignatureHeaderValue([Secret], "evt_123", ts, Body),
        };
        new StandardWebhooks.StandardWebhook(Secret).Verify(Encoding.UTF8.GetString(Body), headers); // throws when invalid
    }

    [Fact]
    public void The_reference_library_produces_the_same_signature_we_do()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var reference = new StandardWebhooks.StandardWebhook(Secret).Sign("evt_123", at, Encoding.UTF8.GetString(Body));
        Assert.Equal(reference, WebhookSigning.Sign(Secret, "evt_123", at.ToUnixTimeSeconds(), Body));
    }

    [Fact]
    public void The_reference_library_rejects_a_tampered_body_signed_by_us()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headers = new HeaderDictionary
        {
            [WebhookSigning.IdHeader] = "evt_123",
            [WebhookSigning.TimestampHeader] = ts.ToString(),
            [WebhookSigning.SignatureHeader] = WebhookSigning.Sign(Secret, "evt_123", ts, Body),
        };
        Assert.ThrowsAny<Exception>(() => new StandardWebhooks.StandardWebhook(Secret).Verify("{\"tampered\":true}", headers));
    }

    [Fact]
    public void Rotation_overlap_both_old_and_new_secret_verify_the_same_request()
    {
        var oldSecret = WebhookSigning.GenerateSecret();
        var newSecret = WebhookSigning.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var header = WebhookSigning.SignatureHeaderValue([newSecret, oldSecret], "evt_1", ts, Body);

        Assert.Equal(2, header.Split(' ').Length);
        Assert.StartsWith(WebhookSigning.Sign(newSecret, "evt_1", ts, Body), header); // newest first
        Assert.Equal(WebhookSigning.VerifyResult.Valid, WebhookSigning.Verify([oldSecret], "evt_1", ts.ToString(), header, Body, now));
        Assert.Equal(WebhookSigning.VerifyResult.Valid, WebhookSigning.Verify([newSecret], "evt_1", ts.ToString(), header, Body, now));
    }

    [Fact]
    public void A_wrong_secret_or_body_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var header = WebhookSigning.Sign(Secret, "evt_1", ts, Body);
        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, WebhookSigning.Verify([WebhookSigning.GenerateSecret()], "evt_1", ts.ToString(), header, Body, now));
        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, WebhookSigning.Verify([Secret], "evt_1", ts.ToString(), header, Encoding.UTF8.GetBytes("{}"), now));
        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, WebhookSigning.Verify([Secret], "evt_other", ts.ToString(), header, Body, now));
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void A_timestamp_outside_the_five_minute_tolerance_is_rejected(int minutes)
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.AddMinutes(minutes).ToUnixTimeSeconds();
        var header = WebhookSigning.Sign(Secret, "evt_1", ts, Body);
        Assert.Equal(WebhookSigning.VerifyResult.TimestampOutOfTolerance, WebhookSigning.Verify([Secret], "evt_1", ts.ToString(), header, Body, now));
    }

    [Fact]
    public void Missing_headers_are_malformed()
    {
        Assert.Equal(WebhookSigning.VerifyResult.Malformed, WebhookSigning.Verify([Secret], null, "1", "v1,x", Body, DateTimeOffset.UtcNow));
        Assert.Equal(WebhookSigning.VerifyResult.Malformed, WebhookSigning.Verify([Secret], "evt", "abc", "v1,x", Body, DateTimeOffset.UtcNow));
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void Standard_schedule_matches_ADR_07_without_jitter()
    {
        var offsets = RetryPolicy.Standard.Preview(12);
        Assert.Equal(TimeSpan.FromSeconds(5), offsets[0]);
        Assert.Equal(TimeSpan.FromSeconds(35), offsets[1]);
        Assert.Equal(TimeSpan.FromSeconds(155), offsets[2]);
        // the window is 72 h, so the preview never goes beyond it
        Assert.All(offsets, o => Assert.True(o <= TimeSpan.FromHours(72)));
    }

    [Fact]
    public void Every_delay_stays_within_plus_minus_20_percent_of_the_base()
    {
        var random = new Random(42);
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var baseDelay = RetryPolicy.Standard.Delays[Math.Min(attempt - 1, RetryPolicy.Standard.Delays.Count - 1)];
            for (var i = 0; i < 200; i++)
            {
                var d = RetryPolicy.Standard.NextDelay(attempt, null, random);
                Assert.InRange(d.TotalSeconds, baseDelay.TotalSeconds * 0.8, baseDelay.TotalSeconds * 1.2);
            }
        }
    }

    [Fact]
    public void Jitter_really_spreads_the_retries()
    {
        var random = new Random(1);
        var delays = Enumerable.Range(0, 100).Select(_ => RetryPolicy.Standard.NextDelay(3, null, random)).Distinct().Count();
        Assert.True(delays > 90, "retries must not synchronise");
    }

    [Fact]
    public void Retry_After_is_a_floor_not_a_ceiling()
    {
        var random = new Random(7);
        Assert.Equal(TimeSpan.FromMinutes(20), RetryPolicy.Standard.NextDelay(1, TimeSpan.FromMinutes(20), random));
        Assert.True(RetryPolicy.Standard.NextDelay(5, TimeSpan.FromSeconds(1), random) > TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void The_schedule_after_the_last_listed_delay_repeats_it()
    {
        var random = new Random(3);
        var d = RetryPolicy.Standard.NextDelay(50, null, random);
        Assert.InRange(d.TotalHours, 12 * 0.8, 12 * 1.2);
    }
}

public class ClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200, AttemptOutcome.Success)]
    [InlineData(204, AttemptOutcome.Success)]
    [InlineData(410, AttemptOutcome.Gone)]
    [InlineData(500, AttemptOutcome.Retry)]
    [InlineData(503, AttemptOutcome.Retry)]
    [InlineData(429, AttemptOutcome.Retry)]
    [InlineData(301, AttemptOutcome.Retry)]
    [InlineData(404, AttemptOutcome.Retry)]
    public void Status_codes_map_to_outcomes(int code, AttemptOutcome expected) =>
        Assert.Equal(expected, ResponseClassifier.Classify(code, false, false, null, Now).Outcome);

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(408, true)]
    [InlineData(429, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(302, false)]
    public void Only_a_sick_receiver_counts_toward_the_breaker_and_auto_disable(int code, bool counts)
    {
        var c = ResponseClassifier.Classify(code, false, false, null, Now);
        Assert.Equal(counts, c.CountsTowardBreaker);
        Assert.Equal(counts, c.CountsTowardAutoDisable);
    }

    [Fact]
    public void Timeouts_and_network_errors_count_as_a_sick_receiver()
    {
        Assert.True(ResponseClassifier.Classify(null, true, false, null, Now).CountsTowardBreaker);
        Assert.True(ResponseClassifier.Classify(null, false, true, null, Now).CountsTowardBreaker);
    }

    [Fact]
    public void Retry_After_accepts_seconds_and_http_dates_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), ResponseClassifier.ParseRetryAfter("120", Now));
        Assert.Equal(TimeSpan.FromMinutes(5), ResponseClassifier.ParseRetryAfter(Now.AddMinutes(5).ToString("R"), Now));
        Assert.Equal(TimeSpan.FromHours(12), ResponseClassifier.ParseRetryAfter("99999999", Now)); // a hostile receiver cannot park us for a year
        Assert.Null(ResponseClassifier.ParseRetryAfter("soon", Now));
        Assert.Equal(TimeSpan.FromSeconds(30), ResponseClassifier.Classify(429, false, false, "30", Now).RetryAfter);
    }
}

public class CircuitBreakerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Five_consecutive_failures_open_the_circuit_with_a_30_second_first_probe()
    {
        var h = EndpointHealth.Healthy;
        for (var i = 0; i < 4; i++) h = CircuitBreaker.OnFailure(h, true, T0);
        Assert.False(h.IsCircuitOpen(T0));
        h = CircuitBreaker.OnFailure(h, true, T0);
        Assert.True(h.IsCircuitOpen(T0));
        Assert.Equal(T0.AddSeconds(30), h.CircuitOpenUntil);
    }

    [Fact]
    public void Failed_probes_double_the_wait_up_to_15_minutes()
    {
        var h = EndpointHealth.Healthy;
        for (var i = 0; i < 5; i++) h = CircuitBreaker.OnFailure(h, true, T0);
        var waits = new List<TimeSpan>();
        var now = T0;
        for (var i = 0; i < 8; i++)
        {
            now = h.CircuitOpenUntil!.Value;
            h = CircuitBreaker.OnFailure(h, true, now); // the probe at the end of the wait failed again
            waits.Add(h.CircuitOpenUntil!.Value - now);
        }
        Assert.Equal(TimeSpan.FromMinutes(1), waits[0]);
        Assert.Equal(TimeSpan.FromMinutes(2), waits[1]);
        Assert.Equal(TimeSpan.FromMinutes(15), waits[^1]);
        Assert.All(waits, w => Assert.True(w <= TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void One_success_closes_the_circuit_and_forgets_everything()
    {
        var h = EndpointHealth.Healthy;
        for (var i = 0; i < 7; i++) h = CircuitBreaker.OnFailure(h, true, T0);
        Assert.Equal(EndpointHealth.Healthy, CircuitBreaker.OnSuccess(h));
    }

    [Fact]
    public void Failures_that_do_not_mean_a_sick_receiver_never_open_the_circuit()
    {
        var h = EndpointHealth.Healthy;
        for (var i = 0; i < 50; i++) h = CircuitBreaker.OnFailure(h, false, T0);
        Assert.Equal(EndpointHealth.Healthy, h);
    }
}

public class AutoDisableTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly AutoDisableRule Rule = new(100, TimeSpan.FromHours(24), TimeSpan.FromDays(5));

    [Theory]
    [InlineData(24, AutoDisableDecision.None)]
    [InlineData(25, AutoDisableDecision.Warn25)]
    [InlineData(26, AutoDisableDecision.None)]
    [InlineData(50, AutoDisableDecision.Warn50)]
    [InlineData(75, AutoDisableDecision.Warn75)]
    [InlineData(99, AutoDisableDecision.None)]
    [InlineData(100, AutoDisableDecision.Disable)]
    public void Warnings_fire_once_at_25_50_75_percent_and_the_endpoint_is_disabled_at_100(int failures, AutoDisableDecision expected) =>
        Assert.Equal(expected, AutoDisable.Evaluate(Rule, new EndpointHealth(failures, T0.AddHours(-2), null, 0), T0));

    [Fact]
    public void A_quiet_endpoint_is_still_disabled_after_five_days_of_continuous_failure()
    {
        // 3 events a day never reach 100 failures: the second rule exists for exactly this
        var health = new EndpointHealth(15, T0.AddDays(-5).AddMinutes(-1), null, 0);
        Assert.Equal(AutoDisableDecision.Disable, AutoDisable.Evaluate(Rule, health, T0));
    }

    [Fact]
    public void One_hundred_failures_spread_over_more_than_24_hours_do_not_trip_the_first_rule()
    {
        var health = new EndpointHealth(100, T0.AddHours(-30), null, 0);
        Assert.NotEqual(AutoDisableDecision.Disable, AutoDisable.Evaluate(Rule, health, T0));
    }

    [Fact]
    public void A_healthy_endpoint_is_left_alone() =>
        Assert.Equal(AutoDisableDecision.None, AutoDisable.Evaluate(Rule, EndpointHealth.Healthy, T0));
}

public class StateMachineTests
{
    [Theory]
    [InlineData("pending", "in_flight", true)]
    [InlineData("in_flight", "succeeded", true)]
    [InlineData("in_flight", "retry_scheduled", true)]
    [InlineData("retry_scheduled", "in_flight", true)]
    [InlineData("retry_scheduled", "dead", true)]
    [InlineData("held", "pending", true)]
    [InlineData("dead", "pending", true)]
    [InlineData("succeeded", "pending", true)]
    [InlineData("succeeded", "dead", false)]
    [InlineData("succeeded", "in_flight", false)]
    [InlineData("pending", "succeeded", false)]
    [InlineData("dead", "succeeded", false)]
    [InlineData("held", "in_flight", false)]
    [InlineData("nonsense", "pending", false)]
    public void Only_documented_transitions_are_legal(string from, string to, bool legal) =>
        Assert.Equal(legal, DeliveryStateMachine.CanMove(from, to));
}

public class SsrfGuardTests
{
    private sealed class FakeDns(Dictionary<string, IPAddress[]> map) : IDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) =>
            map.TryGetValue(host, out var a) ? Task.FromResult<IReadOnlyList<IPAddress>>(a) : throw new System.Net.Sockets.SocketException(11001);
    }

    private static SsrfGuard Guard(SsrfPolicy? policy = null, params (string Host, string Ip)[] dns) =>
        new(new FakeDns(dns.GroupBy(d => d.Host).ToDictionary(g => g.Key, g => g.Select(x => IPAddress.Parse(x.Ip)).ToArray())), policy ?? SsrfPolicy.Strict);

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("169.254.169.254")]       // cloud metadata
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:127.0.0.1")]      // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]       // NAT64 to 127.0.0.1
    [InlineData("2002:7f00:1::")]         // 6to4 to 127.0.0.1
    public void Private_loopback_link_local_and_reserved_addresses_are_blocked(string ip) =>
        Assert.True(IpRanges.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.False(IpRanges.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("http://example.com/hook", "https_required")]
    [InlineData("ftp://example.com/hook", "https_required")]
    [InlineData("https://user:pass@example.com/hook", "url_credentials")]
    [InlineData("https://example.com:5432/hook", "port_blocked")]
    [InlineData("https://127.0.0.1/hook", "address_blocked")]
    [InlineData("https://169.254.169.254/latest/meta-data", "address_blocked")]
    [InlineData("https://[::1]/hook", "address_blocked")]
    [InlineData("https://localhost-alias.example/hook", "address_blocked")]
    [InlineData("https://rebind.example/hook", "address_blocked")]
    [InlineData("https://nonexistent.example/hook", "dns_failed")]
    [InlineData("not a url", "url_invalid")]
    [InlineData("", "url_invalid")]
    public async Task Hostile_urls_are_rejected(string url, string code)
    {
        var guard = Guard(null, ("localhost-alias.example", "127.0.0.1"), ("rebind.example", "93.184.216.34"), ("rebind.example", "10.0.0.5"));
        var decision = await guard.EvaluateAsync(url, CancellationToken.None);
        Assert.False(decision.Allowed);
        Assert.Equal(code, decision.Code);
    }

    [Fact]
    public async Task A_public_https_host_is_allowed_and_the_checked_address_is_returned_for_pinning()
    {
        var decision = await Guard(null, ("hooks.acme.example", "93.184.216.34")).EvaluateAsync("https://hooks.acme.example/in", CancellationToken.None);
        Assert.True(decision.Allowed);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), decision.Address);
    }

    [Fact]
    public async Task Rebinding_one_private_answer_among_public_ones_is_enough_to_reject()
    {
        // the attacker's DNS returns a public address first and an internal one second
        var decision = await Guard(null, ("evil.example", "93.184.216.34"), ("evil.example", "169.254.169.254")).EvaluateAsync("https://evil.example/x", CancellationToken.None);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task The_dev_allow_list_permits_only_the_exact_host_and_port_and_still_blocks_everything_else()
    {
        var policy = new SsrfPolicy([new EgressAllowance("localhost", 5201)]);
        var guard = Guard(policy, ("localhost", "127.0.0.1"));

        var allowed = await guard.EvaluateAsync("http://localhost:5201/hooks/contoso", CancellationToken.None);
        Assert.True(allowed.Allowed);
        Assert.True(allowed.DevException);

        Assert.False((await guard.EvaluateAsync("http://localhost:5202/hooks", CancellationToken.None)).Allowed);     // other port
        Assert.False((await guard.EvaluateAsync("http://169.254.169.254/latest", CancellationToken.None)).Allowed);   // metadata stays blocked
        Assert.False((await guard.EvaluateAsync("https://10.0.0.1/hook", CancellationToken.None)).Allowed);           // private range stays blocked
    }
}

public class EventFilterTests
{
    private static JsonElement Event(string dataJson, string type = "invoice.paid") =>
        JsonDocument.Parse(PayloadBuilder.CanonicalForFilter(type, "inv_1", 2, dataJson)).RootElement;

    private static EventFilter Parse(string json)
    {
        var (filter, error) = EventFilter.Parse(json);
        Assert.Null(error);
        return filter!;
    }

    [Fact]
    public void An_empty_filter_matches_everything()
    {
        Assert.True(Parse("").Matches(Event("{}")));
        Assert.True(Parse("{\"groups\":[]}").Matches(Event("{}")));
    }

    [Fact]
    public void Conditions_inside_a_group_are_anded_and_groups_are_ored()
    {
        var f = Parse("""{"groups":[[{"path":"data.amount","op":"gte","value":1000},{"path":"data.currency","op":"eq","value":"EUR"}],[{"path":"data.customer.country","op":"eq","value":"UA"}]]}""");
        Assert.True(f.Matches(Event("""{"amount":1500,"currency":"EUR","customer":{"country":"PL"}}""")));
        Assert.False(f.Matches(Event("""{"amount":1500,"currency":"USD","customer":{"country":"PL"}}""")));
        Assert.True(f.Matches(Event("""{"amount":5,"currency":"USD","customer":{"country":"UA"}}""")));
    }

    [Theory]
    [InlineData("eq", "\"EUR\"", true)]
    [InlineData("ne", "\"EUR\"", false)]
    [InlineData("in", "[\"EUR\",\"USD\"]", true)]
    [InlineData("nin", "[\"EUR\",\"USD\"]", false)]
    [InlineData("contains", "\"ur\"", true)]
    [InlineData("startsWith", "\"EU\"", true)]
    public void Text_operators(string op, string value, bool expected)
    {
        var f = Parse($$"""{"groups":[[{"path":"data.currency","op":"{{op}}","value":{{value}}}]]}""");
        Assert.Equal(expected, f.Matches(Event("""{"currency":"EUR"}""")));
    }

    [Theory]
    [InlineData("gt", 100, true)]
    [InlineData("gt", 250, false)]
    [InlineData("gte", 250, true)]
    [InlineData("lt", 251, true)]
    [InlineData("lte", 249, false)]
    public void Number_operators(string op, int value, bool expected)
    {
        var f = Parse($$"""{"groups":[[{"path":"data.amount","op":"{{op}}","value":{{value}}}]]}""");
        Assert.Equal(expected, f.Matches(Event("""{"amount":250.00}""")));
    }

    [Fact]
    public void A_missing_field_does_not_match_positive_operators_but_does_match_negative_ones()
    {
        Assert.False(Parse("""{"groups":[[{"path":"data.nothing","op":"eq","value":1}]]}""").Matches(Event("{}")));
        Assert.True(Parse("""{"groups":[[{"path":"data.nothing","op":"ne","value":1}]]}""").Matches(Event("{}")));
        Assert.True(Parse("""{"groups":[[{"path":"data.amount","op":"exists"}]]}""").Matches(Event("""{"amount":1}""")));
        Assert.False(Parse("""{"groups":[[{"path":"data.amount","op":"exists"}]]}""").Matches(Event("{}")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"groups":"x"}""")]
    [InlineData("""{"groups":[[{"path":"data.a","op":"regex","value":"x"}]]}""")]
    [InlineData("""{"groups":[[{"path":"data.a","op":"eq"}]]}""")]
    [InlineData("""{"groups":[[{"path":"data.a","op":"in","value":"x"}]]}""")]
    [InlineData("""{"groups":[[{"path":"data..a;drop","op":"eq","value":1}]]}""")]
    public void Malformed_or_unsafe_filters_are_rejected(string json)
    {
        var (filter, error) = EventFilter.Parse(json);
        Assert.Null(filter);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void More_than_ten_conditions_are_rejected()
    {
        var conditions = string.Join(",", Enumerable.Range(0, 11).Select(i => $$"""{"path":"data.a{{i}}","op":"eq","value":1}"""));
        var (filter, error) = EventFilter.Parse($$"""{"groups":[[{{conditions}}]]}""");
        Assert.Null(filter);
        Assert.Contains("10", error);
    }

    [Fact]
    public void The_compiled_expression_is_readable()
    {
        var f = Parse("""{"groups":[[{"path":"data.amount","op":"gte","value":1000},{"path":"data.currency","op":"eq","value":"EUR"}]]}""");
        Assert.Equal("data.amount >= 1000 AND data.currency = \"EUR\"", f.ToExpression());
    }
}

public class PayloadBuilderTests
{
    private static readonly Guid Id = Guid.Parse("0198a000-0000-7000-8000-000000000001");
    private const string Data = """{"invoice_id":"inv_1","number":"INV-1","customer":{"name":"Anna K","email":"anna@example.com"},"amount":10}""";

    [Fact]
    public void Fat_payload_carries_the_whole_data_object_and_the_stable_event_id()
    {
        var body = PayloadBuilder.Build("fat", Id, false, "invoice.paid", "2026-11-01", DateTimeOffset.UnixEpoch, "inv_1", 3, Data, ["invoice_id"]);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("evt_0198a00000007000800000000000000" + "1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("anna@example.com", doc.RootElement.GetProperty("data").GetProperty("customer").GetProperty("email").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("sequence").GetInt64());
        Assert.False(doc.RootElement.GetProperty("test").GetBoolean());
    }

    [Fact]
    public void Thin_payload_keeps_only_the_reference_keys_so_personal_data_never_leaves()
    {
        var body = PayloadBuilder.Build("thin", Id, false, "kyc.completed", "2026-11-01", DateTimeOffset.UnixEpoch, "inv_1", 1, Data, ["invoice_id"]);
        var text = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain("anna@example.com", text);
        Assert.DoesNotContain("Anna K", text);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("inv_1", doc.RootElement.GetProperty("data").GetProperty("invoice_id").GetString());
        Assert.Single(doc.RootElement.GetProperty("data").EnumerateObject());
    }

    [Fact]
    public void Test_events_carry_a_test_id_and_flag()
    {
        var body = PayloadBuilder.Build("fat", Id, true, "invoice.paid", "2026-11-01", DateTimeOffset.UnixEpoch, "inv_1", 1, Data, []);
        using var doc = JsonDocument.Parse(body);
        Assert.StartsWith("evt_test_", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("test").GetBoolean());
    }
}
