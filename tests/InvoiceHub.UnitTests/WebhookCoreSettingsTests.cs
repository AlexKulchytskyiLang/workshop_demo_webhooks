using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InvoiceHub.Webhooks.Core;
using Microsoft.AspNetCore.Http;

namespace InvoiceHub.UnitTests;

public class RetryParametersTests
{
    [Fact]
    public void The_aggressive_preset_triples_the_delay_up_to_the_cap_and_stops_after_eight_attempts()
    {
        var attempts = RetryPolicy.Aggressive.PreviewAttempts();
        Assert.Equal(8, attempts.Count);
        Assert.Equal(TimeSpan.Zero, attempts[0].At);
        Assert.Equal([1, 3, 9, 27, 81, 243, 729], attempts.Skip(1).Select(a => (int)a.Delay.TotalSeconds));
        Assert.Equal(RetryPolicy.Aggressive.Window, TimeSpan.FromHours(1));
    }

    [Fact]
    public void The_schedule_stops_when_the_next_attempt_would_fall_outside_the_window()
    {
        var policy = new RetryParameters(14, "exp2", 5, 43_200, 100).ToPolicy();
        var attempts = policy.PreviewAttempts();
        // 5 + 10 + 20 + 40 = 75 s, the next delay (80 s) would end at 155 s > 100 s
        Assert.Equal(5, attempts.Count);
        Assert.Equal(TimeSpan.FromSeconds(75), attempts[^1].At);
    }

    [Fact]
    public void A_cap_on_attempts_is_honoured_even_when_the_window_is_long()
    {
        var policy = new RetryParameters(3, "fixed", 10, 600, 72 * 3600).ToPolicy();
        Assert.Equal(3, policy.PreviewAttempts().Count);
        Assert.True(policy.HasMoreAttempts(2));
        Assert.False(policy.HasMoreAttempts(3));
        Assert.True(RetryPolicy.Standard.HasMoreAttempts(10_000)); // the standard schedule is limited by the window only
    }

    [Theory]
    [InlineData("linear", 5, new[] { 5, 10, 15, 20 })]
    [InlineData("fixed", 7, new[] { 7, 7, 7, 7 })]
    [InlineData("exp4", 2, new[] { 2, 8, 32, 128 })]
    public void Backoff_shapes(string backoff, int initial, int[] expected)
    {
        var delays = new RetryParameters(5, backoff, initial, 86_400, 72 * 3600).ToPolicy().Delays;
        Assert.Equal(expected, delays.Select(d => (int)d.TotalSeconds));
    }

    [Theory]
    [InlineData(1, "exp4", 5, 600, 3600, "Attempts")]
    [InlineData(31, "exp4", 5, 600, 3600, "Attempts")]
    [InlineData(5, "quadratic", 5, 600, 3600, "Backoff")]
    [InlineData(5, "exp4", 0, 600, 3600, "first retry delay")]
    [InlineData(5, "exp4", 60, 30, 3600, "longest delay")]
    [InlineData(5, "exp4", 5, 600, 72 * 3600 + 1, "72 hours")]
    [InlineData(5, "exp4", 30, 600, 20, "shorter than the first retry")]
    public void Invalid_combinations_are_rejected_with_the_reason(int attempts, string backoff, int initial, int cap, int window, string expected)
    {
        var errors = new RetryParameters(attempts, backoff, initial, cap, window).Validate().ToList();
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_default_custom_settings_are_valid() => Assert.Empty(RetryParameters.Default.Validate());

    [Fact]
    public void Custom_settings_round_trip_and_an_unreadable_value_falls_back_to_the_standard_schedule_not_to_no_retries()
    {
        var custom = new RetryParameters(6, "exp3", 2, 900, 3600);
        Assert.Equal(custom, RetryParameters.TryParse(custom.ToJson(), out var parsed) ? parsed : null);
        Assert.Equal(6, RetryPolicy.For("custom", custom.ToJson()).MaxAttempts);

        Assert.Equal(RetryPolicy.Standard, RetryPolicy.For("custom", "not json"));
        Assert.Equal(RetryPolicy.Standard, RetryPolicy.For("custom", new RetryParameters(1, "exp3", 2, 900, 3600).ToJson()));
        Assert.Equal(RetryPolicy.Standard, RetryPolicy.For("custom", null));
        Assert.Equal(RetryPolicy.Aggressive, RetryPolicy.For("aggressive", null));
    }

    [Fact]
    public void The_preview_is_exactly_what_delivery_does_the_first_retry_follows_the_first_delay()
    {
        var policy = RetryPolicy.Standard;
        var attempts = policy.PreviewAttempts();
        Assert.Equal(policy.Delays[0], attempts[1].Delay);
        Assert.Equal(policy.Delays[0] + policy.Delays[1], attempts[2].At);
        Assert.All(attempts, a => Assert.True(a.At <= policy.Window));
    }
}

public class CircuitThresholdTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void The_circuit_opens_exactly_at_the_configured_threshold(int threshold)
    {
        var h = EndpointHealth.Healthy;
        for (var i = 1; i < threshold; i++)
        {
            h = CircuitBreaker.OnFailure(h, true, T0, 1.0, threshold);
            Assert.False(h.IsCircuitOpen(T0), $"open after only {i} failures");
        }
        h = CircuitBreaker.OnFailure(h, true, T0, 1.0, threshold);
        Assert.True(h.IsCircuitOpen(T0));
    }
}

public class Es256Tests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"type":"invoice.paid","data":{"amount":10}}""");
    private const string Id = "evt_abc";

    [Fact]
    public void A_signature_verifies_with_the_published_public_key_and_nothing_secret_is_needed()
    {
        var key = Es256Keys.Generate("k_1");
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var signature = AsymmetricSigning.Sign(key.Pkcs8, Id, ts, Body);

        var published = Es256Keys.ParseJwks($$"""{"keys":[{{key.PublicJwk}}]}""");
        Assert.StartsWith("v1a,", signature);
        Assert.Equal(WebhookSigning.VerifyResult.Valid, AsymmetricSigning.Verify(published.Select(k => k.Key), Id, ts.ToString(CultureInfo.InvariantCulture), signature, Body, now));
        Assert.Equal("k_1", published[0].KeyId);
    }

    [Fact]
    public void The_public_document_contains_no_private_material()
    {
        var jwk = Es256Keys.Generate("k_1").PublicJwk;
        Assert.DoesNotContain("\"d\"", jwk);
        Assert.Contains("\"alg\":\"ES256\"", jwk);
        Assert.Contains("\"crv\":\"P-256\"", jwk);
    }

    [Fact]
    public void Tampering_a_wrong_key_an_old_timestamp_and_missing_input_are_all_rejected()
    {
        var key = Es256Keys.Generate("k_1");
        var other = Es256Keys.Generate("k_2");
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var signature = AsymmetricSigning.Sign(key.Pkcs8, Id, ts, Body);
        var keys = Es256Keys.ParseJwks($$"""{"keys":[{{key.PublicJwk}}]}""").Select(k => k.Key).ToList();
        var otherKeys = Es256Keys.ParseJwks($$"""{"keys":[{{other.PublicJwk}}]}""").Select(k => k.Key).ToList();
        var t = ts.ToString(CultureInfo.InvariantCulture);

        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, AsymmetricSigning.Verify(keys, Id, t, signature, Encoding.UTF8.GetBytes("{}"), now));
        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, AsymmetricSigning.Verify(otherKeys, Id, t, signature, Body, now));
        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, AsymmetricSigning.Verify(keys, "evt_other", t, signature, Body, now));
        Assert.Equal(WebhookSigning.VerifyResult.TimestampOutOfTolerance, AsymmetricSigning.Verify(keys, Id, t, signature, Body, now.AddMinutes(6)));
        Assert.Equal(WebhookSigning.VerifyResult.Malformed, AsymmetricSigning.Verify(keys, null, t, signature, Body, now));
        Assert.Equal(WebhookSigning.VerifyResult.BadSignature, AsymmetricSigning.Verify(keys, Id, t, "v1a,@@not-base64@@", Body, now));
    }

    [Fact]
    public void The_public_key_can_be_derived_from_the_stored_private_key()
    {
        var key = Es256Keys.Generate("k_1");
        Assert.Equal(key.PublicJwk, Es256Keys.PublicJwk("k_1", key.Pkcs8));
    }

    [Fact]
    public void During_a_migration_one_request_carries_both_signatures_and_each_kind_of_receiver_accepts_it()
    {
        var hmac = WebhookSigning.GenerateSecret();
        var es = Es256Keys.Generate("k_9");
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var t = ts.ToString(CultureInfo.InvariantCulture);

        var headers = SignatureHeaderBuilder.Build(
        [
            new SigningCredential(SigningAlgorithms.Es256, "k_9", es.Pkcs8),
            new SigningCredential(SigningAlgorithms.Hmac, "k_h", Encoding.UTF8.GetBytes(hmac)),
        ], Id, ts, Body);

        var parts = headers.Signature.Split(' ');
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("v1,", parts[0]); // HMAC first, so a receiver library that reads only the first entry still works
        Assert.StartsWith("v1a,", parts[1]);
        Assert.Equal("k_9", headers.KeyIds);

        var publicKeys = Es256Keys.ParseJwks($$"""{"keys":[{{es.PublicJwk}}]}""").Select(k => k.Key);
        Assert.Equal(WebhookSigning.VerifyResult.Valid, WebhookSigning.Verify([hmac], Id, t, headers.Signature, Body, now));
        Assert.Equal(WebhookSigning.VerifyResult.Valid, AsymmetricSigning.Verify(publicKeys, Id, t, headers.Signature, Body, now));
    }

    [Fact]
    public void The_reference_library_still_accepts_a_request_that_also_carries_an_asymmetric_signature()
    {
        const string secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        var es = Es256Keys.Generate("k_9");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headers = SignatureHeaderBuilder.Build(
        [
            new SigningCredential(SigningAlgorithms.Hmac, "k_h", Encoding.UTF8.GetBytes(secret)),
            new SigningCredential(SigningAlgorithms.Es256, "k_9", es.Pkcs8),
        ], Id, ts, Body);

        var http = new HeaderDictionary
        {
            ["webhook-id"] = Id,
            ["webhook-timestamp"] = ts.ToString(CultureInfo.InvariantCulture),
            ["webhook-signature"] = headers.Signature,
        };
        new StandardWebhooks.StandardWebhook(secret).Verify(Encoding.UTF8.GetString(Body), http); // throws if it does not verify
    }

    [Fact]
    public void Rotation_lists_the_new_and_the_old_key_so_both_generations_of_receivers_verify()
    {
        var oldKey = Es256Keys.Generate("k_old");
        var newKey = Es256Keys.Generate("k_new");
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var t = ts.ToString(CultureInfo.InvariantCulture);

        var headers = SignatureHeaderBuilder.Build(
        [
            new SigningCredential(SigningAlgorithms.Es256, "k_new", newKey.Pkcs8),
            new SigningCredential(SigningAlgorithms.Es256, "k_old", oldKey.Pkcs8),
        ], Id, ts, Body);

        Assert.Equal("k_new k_old", headers.KeyIds);
        foreach (var key in new[] { oldKey, newKey })
        {
            var published = Es256Keys.ParseJwks($$"""{"keys":[{{key.PublicJwk}}]}""").Select(k => k.Key);
            Assert.Equal(WebhookSigning.VerifyResult.Valid, AsymmetricSigning.Verify(published, Id, t, headers.Signature, Body, now));
        }
    }

    [Fact]
    public void Jwks_documents_that_are_not_p256_signing_keys_are_ignored()
    {
        Assert.Empty(Es256Keys.ParseJwks("""{"keys":[{"kty":"RSA","n":"x","e":"AQAB"},{"kty":"EC","crv":"P-384","x":"a","y":"b"}]}"""));
        Assert.Empty(Es256Keys.ParseJwks("""{"nothing":true}"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Es256Keys.ParseJwks("not json"));
    }

    [Fact]
    public void Signatures_are_not_deterministic_but_always_verify()
    {
        var key = Es256Keys.Generate("k");
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var a = AsymmetricSigning.Sign(key.Pkcs8, Id, ts, Body);
        var b = AsymmetricSigning.Sign(key.Pkcs8, Id, ts, Body);
        Assert.NotEqual(a, b); // ECDSA is randomised: nobody may compare signatures for equality, only verify them
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(key.Pkcs8, out _);
        Assert.Equal(64, Convert.FromBase64String(a["v1a,".Length..]).Length); // raw r||s, 32 + 32 bytes
    }
}

public class HeaderRulesTests
{
    private static List<string> Errors(string? authHeader, params CustomHeader[] headers) => HeaderRules.Validate(headers, authHeader).ToList();

    [Fact]
    public void Ordinary_headers_pass() => Assert.Empty(Errors(null, new CustomHeader("X-Target-Env", "staging", false), new CustomHeader("X-Gateway-Key", "abc", true)));

    [Theory]
    [InlineData("Host")]
    [InlineData("content-type")]
    [InlineData("Content-Length")]
    [InlineData("webhook-id")]
    [InlineData("Webhook-Anything")]
    [InlineData("traceparent")]
    [InlineData("Cookie")]
    public void Reserved_names_are_refused(string name) => Assert.Contains(Errors(null, new CustomHeader(name, "x", false)), e => e.Contains("reserved", StringComparison.Ordinal));

    [Theory]
    [InlineData("bad name")]
    [InlineData("bad:name")]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("é")]
    public void Malformed_names_are_refused(string name) => Assert.Contains(Errors(null, new CustomHeader(name, "x", false)), e => e.Contains("not a valid header name", StringComparison.Ordinal));

    [Fact]
    public void Line_breaks_in_values_are_refused_because_they_would_inject_headers() =>
        Assert.Contains(Errors(null, new CustomHeader("X-A", "ok\r\nX-Evil: 1", false)), e => e.Contains("control character", StringComparison.Ordinal));

    [Fact]
    public void Duplicates_the_count_limit_and_the_size_limit_are_enforced()
    {
        Assert.Contains(Errors(null, new CustomHeader("X-A", "1", false), new CustomHeader("x-a", "2", false)), e => e.Contains("twice", StringComparison.Ordinal));
        Assert.Contains(Errors(null, Enumerable.Range(0, 11).Select(i => new CustomHeader($"X-H{i}", "v", false)).ToArray()), e => e.Contains("At most 10", StringComparison.Ordinal));
        Assert.Contains(Errors(null, Enumerable.Range(0, 5).Select(i => new CustomHeader($"X-H{i}", new string('a', 900), false)).ToArray()), e => e.Contains("2048", StringComparison.Ordinal));
        Assert.Contains(Errors(null, new CustomHeader("X-A", new string('a', 1001), false)), e => e.Contains("1000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_header_may_not_collide_with_the_delivery_authentication_header()
    {
        Assert.Contains(Errors("Authorization", new CustomHeader("authorization", "x", false)), e => e.Contains("delivery authentication", StringComparison.Ordinal));
        Assert.Empty(Errors(null, new CustomHeader("Authorization", "x", false))); // allowed while no delivery authentication is configured
    }

    [Fact]
    public void A_secret_header_may_keep_its_stored_value_but_a_plain_one_needs_a_value()
    {
        Assert.Empty(Errors(null, new CustomHeader("X-Key", null, true)));
        Assert.Contains(Errors(null, new CustomHeader("X-Plain", null, false)), e => e.Contains("needs a value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("Host", false)]
    [InlineData("webhook-signature", false)]
    [InlineData("bad name", false)]
    public void The_delivery_authentication_header_name_is_validated(string name, bool valid) => Assert.Equal(valid, !HeaderRules.ValidateAuthHeaderName(name).Any());
}
