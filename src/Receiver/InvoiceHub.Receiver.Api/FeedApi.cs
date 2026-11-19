using Microsoft.Extensions.Options;

namespace InvoiceHub.Receiver.Api;

/// <summary>Read-only view of what the receiver holds, plus the chaos switch. The receiver UI (and the integration tests) use it.</summary>
internal static class FeedApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/stats", async (ReceiverDb db, Chaos chaos, IOptions<ReceiverOptions> options, CancellationToken ct) =>
        {
            var counts = await db.QueryAsync("""
                SELECT
                  (SELECT COUNT(*) FROM dbo.inbox_message),
                  (SELECT COUNT(*) FROM dbo.inbox_message WHERE status = 'processed'),
                  (SELECT COUNT(*) FROM dbo.inbox_message WHERE status = 'received'),
                  (SELECT COUNT(*) FROM dbo.inbox_message WHERE status = 'dead'),
                  (SELECT ISNULL(SUM(duplicate_count), 0) FROM dbo.inbox_message),
                  (SELECT COUNT(*) FROM dbo.inbox_message WHERE outcome = 'stale_ignored'),
                  (SELECT COUNT(*) FROM dbo.request_log WHERE verdict LIKE 'rejected%'),
                  (SELECT COUNT(*) FROM dbo.request_log WHERE verdict LIKE 'chaos%'),
                  (SELECT COUNT(*) FROM dbo.invoice_mirror),
                  (SELECT COUNT(*) FROM dbo.file_transfer),
                  (SELECT COUNT(*) FROM dbo.file_transfer WHERE verdict = 'verified'),
                  (SELECT COUNT(*) FROM dbo.kyc_result),
                  (SELECT COUNT(*) FROM dbo.inbox_message WHERE source = 'pull')
                """,
                r => new { Received = r.GetInt32(0), Processed = r.GetInt32(1), Waiting = r.GetInt32(2), Dead = r.GetInt32(3), Duplicates = r.GetInt32(4), StaleIgnored = r.GetInt32(5), Rejected = r.GetInt32(6), ChaosHits = r.GetInt32(7), Invoices = r.GetInt32(8), Files = r.GetInt32(9), FilesVerified = r.GetInt32(10), Kyc = r.GetInt32(11), Recovered = r.GetInt32(12) }, ct);
            return Results.Ok(new { name = options.Value.Name, counts = counts[0], chaos = chaos.Current, modes = Chaos.Modes });
        });

        g.MapGet("/inbox", async (ReceiverDb db, int? limit, CancellationToken ct) =>
            Results.Ok(await db.QueryAsync("""
                SELECT TOP (@limit) id, webhook_id, type, aggregate_id, sequence, status, outcome, attempts, duplicate_count, received_at, processed_at, is_test, api_version, trace_parent, source
                FROM dbo.inbox_message ORDER BY id DESC
                """,
                r => new
                {
                    id = r.GetInt64(0), webhookId = r.GetString(1), type = r.GetString(2), aggregateId = r.IsDBNull(3) ? null : r.GetString(3),
                    sequence = r.IsDBNull(4) ? (long?)null : r.GetInt64(4), status = r.GetString(5), outcome = r.IsDBNull(6) ? null : r.GetString(6),
                    attempts = r.GetInt32(7), duplicates = r.GetInt32(8), receivedAt = r.GetDateTime(9), processedAt = r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10), test = r.GetBoolean(11),
                    apiVersion = r.IsDBNull(12) ? null : r.GetString(12), traceParent = r.IsDBNull(13) ? null : r.GetString(13), source = r.GetString(14),
                }, ct, ("@limit", Math.Clamp(limit ?? 50, 1, 500)))));

        g.MapGet("/inbox/{webhookId}", async (string webhookId, ReceiverDb db, CancellationToken ct) =>
        {
            var rows = await db.QueryAsync("SELECT webhook_id, type, status, outcome, body, headers FROM dbo.inbox_message WHERE webhook_id = @id",
                r => new
                {
                    webhookId = r.GetString(0), type = r.GetString(1), status = r.GetString(2), outcome = r.IsDBNull(3) ? null : r.GetString(3), body = r.GetString(4),
                    headers = r.IsDBNull(5) ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonDocument.Parse(r.GetString(5)).RootElement.Clone(),
                }, ct, ("@id", webhookId));
            return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows[0]);
        });

        g.MapGet("/invoices", async (ReceiverDb db, int? limit, CancellationToken ct) =>
            Results.Ok(await db.QueryAsync("""
                SELECT TOP (@limit) invoice_id, number, customer, amount, currency, status, sequence, updated_at FROM dbo.invoice_mirror ORDER BY updated_at DESC
                """,
                r => new
                {
                    invoiceId = r.GetString(0), number = r.IsDBNull(1) ? null : r.GetString(1), customer = r.IsDBNull(2) ? null : r.GetString(2),
                    amount = r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3), currency = r.IsDBNull(4) ? null : r.GetString(4), status = r.GetString(5), sequence = r.GetInt64(6), updatedAt = r.GetDateTime(7),
                }, ct, ("@limit", Math.Clamp(limit ?? 100, 1, 1000)))));

        g.MapGet("/requests", async (ReceiverDb db, int? limit, CancellationToken ct) =>
            Results.Ok(await db.QueryAsync("""
                SELECT TOP (@limit) id, at, webhook_id, verdict, http_status, detail, duration_ms FROM dbo.request_log ORDER BY id DESC
                """,
                r => new
                {
                    id = r.GetInt64(0), at = r.GetDateTime(1), webhookId = r.IsDBNull(2) ? null : r.GetString(2), verdict = r.GetString(3),
                    httpStatus = r.GetInt32(4), detail = r.IsDBNull(5) ? null : r.GetString(5), durationMs = r.GetInt32(6),
                }, ct, ("@limit", Math.Clamp(limit ?? 100, 1, 1000)))));

        // what this application trusts: shared secrets pasted into its configuration, and/or the sender's public key set
        g.MapGet("/trust", (TrustStore trust) => Results.Ok(trust.Describe()));

        g.MapPut("/trust", async (TrustRequest request, TrustStore trust, CancellationToken ct) =>
        {
            if ((request.Secrets ?? []).Any(s => !s.StartsWith("whsec_", StringComparison.Ordinal))) return Results.Problem(statusCode: 400, title: "Secrets start with whsec_.");
            if (request.JwksUrl is { Length: > 0 } && !Uri.TryCreate(request.JwksUrl, UriKind.Absolute, out _)) return Results.Problem(statusCode: 400, title: "The JWKS address is not a valid URL.");
            await trust.SetAsync(request.Secrets ?? [], string.IsNullOrWhiteSpace(request.JwksUrl) ? null : request.JwksUrl, ct);
            return Results.Ok(trust.Describe());
        });

        // files fetched through claim-check links, verified against the announced checksum
        g.MapGet("/files", async (ReceiverDb db, int? limit, CancellationToken ct) =>
            Results.Ok(await db.QueryAsync("""
                SELECT TOP (@limit) id, webhook_id, type, url_host, expected_sha256, actual_sha256, expected_size, actual_size, verdict, error, duration_ms, fetched_at FROM dbo.file_transfer ORDER BY id DESC
                """,
                r => new
                {
                    id = r.GetInt64(0), webhookId = r.GetString(1), type = r.GetString(2), host = r.GetString(3), expectedSha256 = r.IsDBNull(4) ? null : r.GetString(4), actualSha256 = r.IsDBNull(5) ? null : r.GetString(5),
                    expectedSize = r.IsDBNull(6) ? (long?)null : r.GetInt64(6), actualSize = r.IsDBNull(7) ? (long?)null : r.GetInt64(7), verdict = r.GetString(8), error = r.IsDBNull(9) ? null : r.GetString(9),
                    durationMs = r.GetInt32(10), fetchedAt = r.GetDateTime(11),
                }, ct, ("@limit", Math.Clamp(limit ?? 50, 1, 500)))));

        g.MapGet("/kyc", async (ReceiverDb db, int? limit, CancellationToken ct) =>
            Results.Ok(await db.QueryAsync("SELECT TOP (@limit) verification_id, decision, risk_score, subject_name, document_number, country, fetched_at FROM dbo.kyc_result ORDER BY fetched_at DESC",
                r => new
                {
                    verificationId = r.GetString(0), decision = r.GetString(1), riskScore = r.GetInt32(2), name = r.IsDBNull(3) ? null : r.GetString(3), document = r.IsDBNull(4) ? null : r.GetString(4),
                    country = r.IsDBNull(5) ? null : r.GetString(5), fetchedAt = r.GetDateTime(6),
                }, ct, ("@limit", Math.Clamp(limit ?? 50, 1, 500)))));

        g.MapGet("/subscriptions", async (ReceiverDb db, CancellationToken ct) =>
            Results.Ok(await db.QueryAsync("SELECT TOP (100) subscription_id, plan_name, amount, currency, renewed_at FROM dbo.subscription_mirror ORDER BY renewed_at DESC",
                r => new { subscriptionId = r.GetString(0), plan = r.IsDBNull(1) ? null : r.GetString(1), amount = r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2), currency = r.IsDBNull(3) ? null : r.GetString(3), renewedAt = r.GetDateTime(4) }, ct)));

        // how long events took from the platform's commit to this inbox (push deliveries only): the number the tenant actually feels
        g.MapGet("/latency", async (ReceiverDb db, DateTime? since, CancellationToken ct) =>
        {
            var rows = await db.QueryAsync("""
                SELECT DATEDIFF_BIG(MILLISECOND, TRY_CONVERT(datetime2, JSON_VALUE(body, '$.created_at'), 127), received_at)
                FROM dbo.inbox_message
                WHERE source = 'push' AND is_test = 0 AND ISJSON(body) = 1 AND (@since IS NULL OR received_at >= @since)
                """, r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ct, ("@since", since?.ToUniversalTime()));
            var ms = rows.Where(v => v is not null).Select(v => v!.Value).Order().ToArray();
            double Pct(double p) => ms.Length == 0 ? 0 : ms[Math.Min(ms.Length - 1, (int)Math.Ceiling(p * ms.Length) - 1)];
            return Results.Ok(new { count = ms.Length, p50 = Pct(0.50), p95 = Pct(0.95), p99 = Pct(0.99), max = ms.Length == 0 ? 0 : ms[^1] });
        });

        // how this application reaches the platform's pull API, and the "did I miss anything?" check
        g.MapGet("/pull-config", (PullConfig pull) => Results.Ok(pull.Describe()));

        g.MapPut("/pull-config", async (PullRequest request, PullConfig pull, CancellationToken ct) =>
        {
            if (request.ApiUrl is { Length: > 0 } && !Uri.TryCreate(request.ApiUrl, UriKind.Absolute, out _)) return Results.Problem(statusCode: 400, title: "The platform address is not a valid URL.");
            if (request.ApiKey is { Length: > 0 } && !request.ApiKey.StartsWith("ihk_", StringComparison.Ordinal)) return Results.Problem(statusCode: 400, title: "API keys start with ihk_.");
            await pull.SetAsync(string.IsNullOrWhiteSpace(request.ApiUrl) ? null : request.ApiUrl, string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey, ct);
            return Results.Ok(pull.Describe());
        });

        g.MapPost("/reconcile", async (ReconcileRequest request, Reconciler reconciler, CancellationToken ct) =>
        {
            var result = await reconciler.RunAsync(request.Hours ?? 24, request.Since, ct);
            return result.Problem is null ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });

        g.MapGet("/chaos", (Chaos chaos) => Results.Ok(new { state = chaos.Current, modes = Chaos.Modes }));

        g.MapPut("/chaos", (ChaosRequest request, Chaos chaos) =>
        {
            if (!Chaos.Modes.Contains(request.Mode)) return Results.Problem(statusCode: 400, title: $"Mode must be one of: {string.Join(", ", Chaos.Modes)}.");
            chaos.Set(request.Mode, Math.Clamp(request.DelaySeconds ?? 15, 1, 120));
            return Results.Ok(new { state = chaos.Current });
        });

        // test helper: forget everything (a fresh demo run without restarting the receiver)
        g.MapPost("/reset", async (ReceiverDb db, Chaos chaos, CancellationToken ct) =>
        {
            await db.ExecuteAsync("DELETE dbo.inbox_message; DELETE dbo.request_log; DELETE dbo.invoice_mirror; DELETE dbo.payment_log; DELETE dbo.file_transfer; DELETE dbo.kyc_result; DELETE dbo.subscription_mirror;", ct);
            chaos.Set("ok", 0);
            return Results.NoContent();
        });
    }
}

internal sealed record ChaosRequest(string Mode, int? DelaySeconds);

internal sealed record TrustRequest(List<string>? Secrets, string? JwksUrl);
