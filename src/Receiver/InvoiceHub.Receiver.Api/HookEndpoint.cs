using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InvoiceHub.Webhooks.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Receiver.Api;

public sealed class ReceiverOptions
{
    public const string Section = "Receiver";

    public string Name { get; set; } = "Contoso ERP";

    /// <summary>The path segment of the webhook URL: <c>/hooks/{EndpointKey}</c>.</summary>
    public string EndpointKey { get; set; } = "contoso-erp";

    /// <summary>Signing secrets we accept: the current one and, during a rotation, the previous one. The sender signs with all valid ones.</summary>
    public List<string> Secrets { get; set; } = [];

    public int MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>Where the platform's pull API lives, and the API key this application was given in the portal (scopes events:read, kyc:read). Can also be set at run time.</summary>
    public string? PlatformApiUrl { get; set; }

    public string? PlatformApiKey { get; set; }
}

/// <summary>Failure injection for the demo: lets the presenter make this receiver sick on demand and watch the platform cope.</summary>
public sealed class Chaos
{
    private volatile ChaosState _state = new("ok", 0);

    public ChaosState Current => _state;

    public void Set(string mode, int delaySeconds) => _state = new ChaosState(mode, delaySeconds);

    public static readonly string[] Modes = ["ok", "fail500", "slow", "gone", "drop", "unauthorized", "throttle"];
}

public sealed record ChaosState(string Mode, int DelaySeconds);

/// <summary>Signals the processor that there is new work (it also polls, so a lost signal costs seconds, not events).</summary>
internal sealed class InboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _channel.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* poll interval elapsed */ }
    }
}

/// <summary>
/// The receiving side of the contract, done the way the docs tell a tenant to do it:
/// read the RAW body (the signature covers bytes, not a re-serialised model), verify the signature in constant time with a 5 minute timestamp
/// tolerance (current and previous secret both accepted), write to the inbox with a unique key on <c>webhook-id</c> (a duplicate is a harmless 200),
/// answer fast, and do the real work afterwards. A bad signature is a 401 and is never stored as an event.
/// </summary>
internal static class HookEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/hooks/{key}", HandleAsync).DisableAntiforgery();

    private static async Task<IResult> HandleAsync(string key, HttpRequest request, IOptions<ReceiverOptions> options,
                                                   ReceiverDb db, Chaos chaos, InboxSignal signal, TrustStore trust,
                                                   TimeProvider clock, ILogger<ReceiverOptions> logger, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var o = options.Value;

        var webhookId = request.Headers[WebhookSigning.IdHeader].ToString();

        async Task<IResult> Respond(int status, string verdict, string? detail, IResult result)
        {
            await Log(db, webhookId.Length == 0 ? null : webhookId, verdict, status, detail, started);
            return result;
        }

        // ---- chaos: the receiver pretends to be unwell ----
        var state = chaos.Current;
        switch (state.Mode)
        {
            case "fail500": return await Respond(500, "chaos_500", "Injected failure", Results.StatusCode(500));
            case "unauthorized": return await Respond(401, "chaos_401", "Injected 401", Results.StatusCode(401));
            case "gone": return await Respond(410, "chaos_410", "Injected 410 Gone", Results.StatusCode(410));
            case "throttle":
                request.HttpContext.Response.Headers.RetryAfter = "5";
                return await Respond(429, "chaos_429", "Injected 429, Retry-After 5", Results.StatusCode(429));
            case "drop":
                // accept the connection and never answer: the sender's timeout is what saves it
                try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { /* the sender gave up */ }
                request.HttpContext.Abort();
                return Results.Empty;
            case "slow":
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, state.DelaySeconds)), ct); }
                catch (OperationCanceledException) { return Results.Empty; } // the sender timed out first: nothing was processed, it will retry
                break;
        }

        // ---- raw body, bounded ----
        if (request.ContentLength > o.MaxBodyBytes)
            return await Respond(413, "rejected_malformed", "Body too large", Results.StatusCode(413));

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > o.MaxBodyBytes)
            return await Respond(413, "rejected_malformed", "Body too large", Results.StatusCode(413));
        var body = buffer.ToArray();

        // ---- verify ----
        var verdict = await trust.VerifyAsync(webhookId, request.Headers[WebhookSigning.TimestampHeader], request.Headers[WebhookSigning.SignatureHeader], body, clock.GetUtcNow(), ct);
        switch (verdict)
        {
            case WebhookSigning.VerifyResult.BadSignature:
                return await Respond(401, "rejected_signature", "Signature does not match any known secret", Results.Unauthorized());
            case WebhookSigning.VerifyResult.TimestampOutOfTolerance:
                return await Respond(401, "rejected_timestamp", "Timestamp outside the 5 minute tolerance (possible replay)", Results.Unauthorized());
            case WebhookSigning.VerifyResult.Malformed:
                return await Respond(400, "rejected_malformed", "Missing or malformed webhook headers", Results.BadRequest());
        }

        // ---- parse just enough to route ----
        string type, aggregate, apiVersion;
        long? sequence;
        bool isTest;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            type = root.GetProperty("type").GetString() ?? throw new JsonException("type");
            aggregate = root.TryGetProperty("aggregate_id", out var a) ? a.GetString() ?? "" : "";
            apiVersion = root.TryGetProperty("api_version", out var v) ? v.GetString() ?? "" : "";
            sequence = root.TryGetProperty("sequence", out var s) && s.TryGetInt64(out var n) ? n : null;
            isTest = root.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return await Respond(400, "rejected_malformed", "The body is not a valid event", Results.BadRequest());
        }

        // ---- idempotent inbox write ----
        try
        {
            await db.ExecuteAsync("""
                INSERT dbo.inbox_message (webhook_id, type, aggregate_id, sequence, api_version, is_test, body, headers, trace_parent)
                VALUES (@id, @type, @aggregate, @sequence, @version, @test, @body, @headers, @trace)
                """, ct, ("@id", webhookId), ("@type", type), ("@aggregate", aggregate), ("@sequence", sequence), ("@version", apiVersion), ("@test", isTest), ("@body", Encoding.UTF8.GetString(body)),
                ("@headers", JsonSerializer.Serialize(request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()))), ("@trace", request.Headers.TraceParent.ToString() is { Length: > 0 } tp ? tp : null));
            await Log(db, webhookId, "accepted", 200, type, started);
            signal.Notify();
        }
        catch (SqlException e) when (ReceiverDb.IsDuplicateKey(e))
        {
            // the sender is at-least-once: the same event again is normal, and it must have no second effect
            await db.ExecuteAsync("UPDATE dbo.inbox_message SET duplicate_count += 1, last_duplicate_at = SYSUTCDATETIME() WHERE webhook_id = @id", ct, ("@id", webhookId));
            await Log(db, webhookId, "duplicate", 200, type, started);
            logger.LogInformation("Duplicate delivery of {WebhookId} acknowledged", webhookId);
        }

        return Results.Ok(new { received = true });
    }

    private static Task<int> Log(ReceiverDb db, string? webhookId, string verdict, int status, string? detail, long started) =>
        db.ExecuteAsync("INSERT dbo.request_log (webhook_id, verdict, http_status, detail, duration_ms) VALUES (@id, @verdict, @status, @detail, @ms)", CancellationToken.None,
            ("@id", webhookId), ("@verdict", verdict), ("@status", status), ("@detail", detail), ("@ms", (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
}
