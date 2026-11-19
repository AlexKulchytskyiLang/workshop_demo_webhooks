using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Sending;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Webhooks.Api;

internal sealed record TestInput(string? EventType, string? Scenario, JsonElement? Data = null, string? ApiVersion = null);

/// <summary>
/// Trying an endpoint without waiting for real business: the verification <c>webhook.ping</c> and test events (with negative scenarios that prove the receiver is safe).
/// Both go through exactly the code that sends real deliveries (same signing, same egress guard), but they never create deliveries, never count in the
/// numbers and never feed the circuit breaker or auto-disable.
/// </summary>
internal static class EndpointTestingApi
{
    private static readonly string[] Scenarios = ["normal", "bad_signature", "old_timestamp", "duplicate"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/portal/endpoints").WithTags("Endpoint testing");
        g.MapPost("/{id:guid}/ping", Ping).RequireAuthorization(Permissions.EndpointsWrite).RequireRateLimiting(RateLimiting.Tests);
        g.MapPost("/{id:guid}/test", Test).RequireAuthorization(Permissions.EndpointsWrite).RequireRateLimiting(RateLimiting.Tests);
        app.MapGet("/api/portal/events/{type}/sample", Sample).RequireAuthorization(Permissions.EndpointsRead).WithTags("Endpoint testing");
    }

    /// <summary>The realistic sample the Playground starts from, and the facts a person needs to edit it safely.</summary>
    private static IResult Sample(string type, string? version, IEventCatalog catalog)
    {
        if (version is not null && !ApiVersions.IsSupported(version)) return Problems.Validation("invalid_api_version", $"Versions: {string.Join(", ", ApiVersions.Supported)}.");
        var info = catalog.Find(type);
        if (info is null) return Problems.NotFound("unknown_event", $"Unknown event type '{type}'.");
        return Results.Ok(new
        {
            info.Type, info.Description, info.Category, info.ContainsPii, info.ReferenceKeys, payload = info.Payload,
            aggregateId = TestSamples.AggregateId(type), data = JsonDocument.Parse(VersionProjection.Project(version ?? ApiVersions.Current, type, TestSamples.Data(type))).RootElement, version = version ?? ApiVersions.Current,
            note = info.ContainsPii ? "This event carries personal data, so it is always sent thin: only the reference keys below reach your endpoint." : null,
        });
    }

    /// <summary>The verification test: one signed <c>webhook.ping</c>. A 2xx marks the endpoint verified and active.</summary>
    private static async Task<IResult> Ping(Guid id, WebhooksDbContext db, RequestFactory requests, IWebhookSender sender, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();

        var now = clock.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var body = PayloadBuilder.Build(PayloadStyle.Fat, eventId, isTest: true, "webhook.ping", e.ApiVersion, now, $"ep_{e.Id:N}", 1,
            """{"message":"Hello from InvoiceHub. If you can read this, the endpoint is reachable."}""", []);
        var built = await requests.BuildAsync(e, PayloadBuilder.EventId(eventId, true), now, body, ct);
        if (built is null) return Problems.Conflict("no_secret", "The endpoint has no valid signing key.");

        var result = await sender.SendAsync(new SendRequest(new Uri(e.Url), body, built.Headers, TimeSpan.FromSeconds(e.TimeoutSeconds)), ct);
        var ok = result.StatusCode is >= 200 and < 300;
        if (ok)
        {
            var firstTime = e.VerifiedAt is null;
            e.VerifiedAt ??= now.UtcDateTime;
            e.LastTestPassedAt = now.UtcDateTime;
            if (e.Status == EndpointStatus.Pending) e.Status = EndpointStatus.Active;
            e.UpdatedAt = now.UtcDateTime;
            if (firstTime) await db.SaveAsync(audit, new AuditEntry("endpoint.verified", "endpoint", id.ToString(), e.Name, new { statusCode = result.StatusCode }), ct);
            else await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new
        {
            ok,
            statusCode = result.StatusCode,
            durationMs = (int)result.Duration.TotalMilliseconds,
            timedOut = result.TimedOut,
            error = result.Error,
            blockedBy = result.Blocked?.Code,
            responseSnippet = result.ResponseSnippet,
            requestId = built.WebhookId,
            hint = ok ? null : HintFor(result, built.SignatureCount),
        });
    }

    /// <summary>What a person should check next, written for the problem we actually saw.</summary>
    private static string HintFor(SendResult r, int signatures) => r switch
    {
        { Blocked: not null } => "We could not send: " + r.Blocked.Message,
        { TimedOut: true } => "Your server did not answer in time. Answer with 2xx quickly and do the real work afterwards.",
        { NetworkError: true } => "We could not connect. Check the address, that the server is running and that it is reachable from the internet.",
        { StatusCode: 401 or 403 } => "Your server rejected the request. Check that it verifies the signature with the secret (or public key) shown on the Security tab, and that its clock is correct.",
        { StatusCode: 404 } => "Your server answered 404: the path does not exist. Check the URL.",
        { StatusCode: >= 300 and < 400 } => "Your server answered with a redirect. We never follow redirects: use the final address.",
        { StatusCode: >= 500 } => "Your server failed to process the request (5xx).",
        _ => $"Your server answered {r.StatusCode}. We expect any 2xx.",
    };

    /// <summary>A realistic test event with an optional negative scenario. The result carries a checklist, the request as sent (secrets masked) and the reply.</summary>
    private static async Task<IResult> Test(
        Guid id, TestInput input, WebhooksDbContext db, IEventCatalog catalog, RequestFactory requests, IWebhookSender sender, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();
        if (string.IsNullOrWhiteSpace(input.EventType) || !catalog.Exists(input.EventType)) return Problems.Validation("unknown_event", $"Unknown event type '{input.EventType}'.");
        var scenario = input.Scenario ?? "normal";
        if (!Scenarios.Contains(scenario)) return Problems.Validation("invalid_scenario", $"The scenario must be one of: {string.Join(", ", Scenarios)}.");

        var info = catalog.Find(input.EventType)!;
        var now = clock.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var style = info.ContainsPii || e.PayloadStyle == PayloadStyle.Thin ? PayloadStyle.Thin : PayloadStyle.Fat;
        var version = input.ApiVersion ?? e.ApiVersion;
        if (!ApiVersions.IsSupported(version)) return Problems.Validation("invalid_api_version", $"Versions: {string.Join(", ", ApiVersions.Supported)}.");
        var dataJson = TestSamples.Data(info.Type);
        if (input.Data is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } custom)
        {
            if (custom.ValueKind != JsonValueKind.Object) return Problems.Validation("invalid_data", "The event data must be a JSON object.");
            dataJson = custom.GetRawText();
            if (dataJson.Length > 64 * 1024) return Problems.Validation("data_too_large", "The test data can be at most 64 KB.");
        }
        // an edited body is edited in the version being tested, so it is NOT projected again; the sample the person started from already was
        var edited = input.Data is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        if (!edited) dataJson = VersionProjection.Project(version, info.Type, dataJson);
        var body = PayloadBuilder.Build(style, eventId, isTest: true, info.Type, version, now, TestSamples.AggregateId(info.Type), 1, dataJson, info.ReferenceKeys);

        var timestamp = scenario == "old_timestamp" ? now.AddMinutes(-6) : now; // correctly signed, but too old: a safe receiver refuses it
        var built = await requests.BuildAsync(e, PayloadBuilder.EventId(eventId, true), timestamp, body, ct);
        if (built is null) return Problems.Conflict("no_secret", "The endpoint has no valid signing key.");
        if (scenario == "bad_signature")
            built.Headers[WebhookSigning.SignatureHeader] = "v1," + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); // looks right, is wrong

        var send = new SendRequest(new Uri(e.Url), body, built.Headers, TimeSpan.FromSeconds(e.TimeoutSeconds));
        var first = await sender.SendAsync(send, ct);
        SendResult? second = scenario == "duplicate" ? await sender.SendAsync(send, ct) : null; // the same webhook-id again

        var checks = Checks(scenario, first, second, built, e.TimeoutSeconds, now, timestamp);
        var accepted = first.StatusCode is >= 200 and < 300;
        if (scenario == "normal" && accepted)
        {
            e.LastTestPassedAt = now.UtcDateTime; // what "resume" asks for
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new
        {
            ok = scenario is "bad_signature" or "old_timestamp" ? first.StatusCode is >= 400 and < 500 : accepted && (second is null || second.StatusCode is >= 200 and < 300),
            scenario,
            statusCode = first.StatusCode,
            durationMs = (int)first.Duration.TotalMilliseconds,
            error = first.Error,
            blockedBy = first.Blocked?.Code,
            responseSnippet = first.ResponseSnippet,
            secondStatusCode = second?.StatusCode,
            requestId = built.WebhookId,
            request = new { url = e.Url, headers = HeaderMasking.Apply(e, built.Headers), body = Encoding.UTF8.GetString(body) },
            checks,
        });
    }

    private static List<object> Checks(string scenario, SendResult r, SendResult? second, BuiltRequest built, int timeoutSeconds, DateTimeOffset now, DateTimeOffset sentAt)
    {
        var checks = new List<object>();
        void Add(string status, string text) => checks.Add(new { status, text });

        if (r.Blocked is not null)
        {
            Add("bad", "Not sent: " + r.Blocked.Message);
            return checks;
        }
        Add("ok", $"Signed with {built.SignatureCount} signature{(built.SignatureCount == 1 ? "" : "s")} in the webhook-signature header.");

        var age = now - sentAt;
        Add(scenario == "old_timestamp" ? "ok" : "ok", scenario == "old_timestamp"
            ? $"Sent with a timestamp {age.TotalMinutes:0} minutes old, on purpose. A safe receiver rejects anything older than 5 minutes."
            : "Fresh timestamp. Your server should reject anything older than 5 minutes.");

        if (r.TimedOut) { Add("bad", $"No answer within {timeoutSeconds} s."); return checks; }
        if (r.NetworkError) { Add("bad", "Could not connect: " + r.Error); return checks; }

        var code = r.StatusCode ?? 0;
        var latency = (int)r.Duration.TotalMilliseconds;
        switch (scenario)
        {
            case "bad_signature":
                Add(code is >= 400 and < 500 ? "ok" : "bad", code is >= 400 and < 500
                    ? $"Your endpoint rejected the tampered signature ({code}). Verification works."
                    : $"Your endpoint answered {code} to a request with a WRONG signature. It is not verifying signatures: anyone can post to it.");
                break;
            case "old_timestamp":
                Add(code is >= 400 and < 500 ? "ok" : "bad", code is >= 400 and < 500
                    ? $"Your endpoint rejected the old timestamp ({code}). Replay protection works."
                    : $"Your endpoint answered {code} to a request that is 6 minutes old. It does not check the timestamp, so a captured request can be replayed.");
                break;
            case "duplicate":
                Add(code is >= 200 and < 300 ? "ok" : "bad", $"First delivery answered {code} in {latency} ms.");
                var c2 = second?.StatusCode ?? 0;
                Add(c2 is >= 200 and < 300 ? "ok" : "warn", c2 is >= 200 and < 300
                    ? $"Second delivery with the same webhook-id was acknowledged ({c2}). Make sure it had no second effect: de-duplicate on webhook-id."
                    : $"Second delivery answered {c2}. A duplicate should be acknowledged with 2xx and ignored.");
                break;
            default:
                Add(code is >= 200 and < 300 ? "ok" : "bad", code is >= 200 and < 300
                    ? $"Responded {code} in {latency} ms (limit {timeoutSeconds} s)."
                    : $"Responded {code}. We expect any 2xx.");
                break;
        }

        if (r.ResponseSnippet is { Length: > 200 } big) Add("warn", $"The response body is {big.Length}+ characters. Tip: return an empty 2xx and process asynchronously.");
        if (latency > timeoutSeconds * 1000 / 2) Add("warn", $"Slow: {latency} ms is more than half of the {timeoutSeconds} s limit. Acknowledge first, process later.");
        return checks;
    }
}

/// <summary>Believable sample data for the test events. Anything without a hand-made sample gets a clearly labelled generic one.</summary>
internal static class TestSamples
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string AggregateId(string type) => type.Split('.')[0] switch
    {
        "invoice" => "inv_test_0001",
        "payment" => "pay_test_0001",
        "subscription" => "sub_test_0001",
        "kyc" => "kyc_test_0001",
        _ => "obj_test_0001",
    };

    public static string Data(string type)
    {
        var due = DateTime.UtcNow.Date.AddDays(30);
        object data = type switch
        {
            "invoice.generated" or "invoice.paid" or "invoice.overdue" => new
            {
                invoice_id = "inv_test_0001", number = "INV-TEST-0001", amount = 7250.00m, currency = "USD",
                status = type == "invoice.paid" ? "paid" : type == "invoice.overdue" ? "overdue" : "open",
                customer = new { name = "Fabrikam Inc", country = "US" }, due_at = due,
            },
            "payment.completed" => new { payment_id = "pay_test_0001", invoice_id = "inv_test_0001", amount = 7250.00m, currency = "USD", method = "card" },
            "payment.failed" => new { payment_id = "pay_test_0001", invoice_id = "inv_test_0001", amount = 7250.00m, currency = "USD", failure_reason = "card_declined" },
            "subscription.renewed" => new { subscription_id = "sub_test_0001", plan = "business-annual", period_start = DateTime.UtcNow.Date, period_end = DateTime.UtcNow.Date.AddYears(1), amount = 2400.00m, currency = "USD" },
            "catalog.products.imported" => new { import_id = "imp_test_0001", vendor_id = "ven_042", source = "edi:832", products_added = 128, products_updated = 14, rejected = 3 },
            "export.completed" => new { export_id = "exp_test_0001", rows = 120_000, size_bytes = 52_428_800 },
            "webhook.endpoint.auto_disabled" => new { endpoint_id = "ep_test_0001", name = "Example endpoint", reason = "auto_disabled", consecutive_failures = 100 },
            _ when type.StartsWith("kyc.", StringComparison.Ordinal) || type.StartsWith("sanctions.", StringComparison.Ordinal) || type.StartsWith("document.", StringComparison.Ordinal)
                => new { verification_id = "kyc_test_0001" },
            _ => new { example = true, message = $"Sample data for {type}" },
        };
        return JsonSerializer.Serialize(data, Json);
    }
}
