using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Receiver.Api;

/// <summary>
/// How this application reaches back into the platform: the pull API (<c>/v1/events</c>, <c>/v1/kyc/{id}</c>) with an API key it was given in the portal.
/// Webhooks tell it that something happened; pulling is how it fetches what a thin event leaves out, and how it checks it missed nothing.
/// </summary>
internal sealed class PullConfig(IOptions<ReceiverOptions> options, ReceiverDb db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile PullState _state = new(null, null);

    private sealed record PullState(string? ApiUrl, string? ApiKey);

    public string? ApiUrl => _state.ApiUrl ?? options.Value.PlatformApiUrl;

    public string? ApiKey => _state.ApiKey ?? options.Value.PlatformApiKey;

    public bool Configured => !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    public async Task LoadAsync(CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT json FROM dbo.trust_config WHERE id = 2", r => r.GetString(0), ct);
        if (rows.Count > 0 && JsonSerializer.Deserialize<PullRequest>(rows[0], Json) is { } saved)
            _state = new PullState(saved.ApiUrl, saved.ApiKey);
    }

    public async Task SetAsync(string? apiUrl, string? apiKey, CancellationToken ct)
    {
        _state = new PullState(apiUrl, apiKey);
        await db.ExecuteAsync("""
            MERGE dbo.trust_config AS t USING (SELECT 2 AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET json = @json
            WHEN NOT MATCHED THEN INSERT (id, json) VALUES (2, @json);
            """, ct, ("@json", JsonSerializer.Serialize(new PullRequest(apiUrl, apiKey), Json)));
    }

    /// <summary>The key is masked: what is shown is enough to recognise it, never to use it.</summary>
    public object Describe() => new
    {
        apiUrl = ApiUrl,
        apiKey = ApiKey is { Length: > 12 } k ? k[..8] + "…" + k[^4..] : ApiKey is null ? null : "…",
        configured = Configured,
    };
}

internal sealed record PullRequest(string? ApiUrl, string? ApiKey);

internal sealed record ReconcileRequest(int? Hours, DateTime? Since = null);

internal sealed class PlatformClient(PullConfig config, IHttpClientFactory http)
{
    private HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(config.ApiUrl!.TrimEnd('/') + "/"), path.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        return request;
    }

    /// <summary>The details of a verification, or null when the platform has no such verification for us.</summary>
    public async Task<JsonElement?> GetKycAsync(string verificationId, CancellationToken ct)
    {
        using var response = await http.CreateClient("pull").SendAsync(Request($"v1/kyc/{Uri.EscapeDataString(verificationId)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode(); // 401/403 (wrong or revoked key, missing scope) surface as errors the inbox retries and finally parks as dead
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone();
    }

    /// <summary>Every event since <paramref name="from"/>, oldest first, page by page.</summary>
    public async IAsyncEnumerable<JsonElement> EventsAsync(DateTime from, [EnumeratorCancellation] CancellationToken ct)
    {
        string? after = null;
        for (var page = 0; page < 50; page++)
        {
            var path = $"v1/events?limit=100&from={Uri.EscapeDataString(from.ToUniversalTime().ToString("O"))}" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
            using var response = await http.CreateClient("pull").SendAsync(Request(path), ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            foreach (var item in body.GetProperty("items").EnumerateArray()) yield return item.Clone();
            after = body.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (after is null) yield break;
        }
    }
}

/// <summary>
/// Claim-check receiver side (ASR-18): the event carries a small envelope with an expiring link. We download the file, hash it while it streams (it is never held in
/// memory) and compare with the checksum and size the platform promised. A link that has expired is recorded as such, not retried: the platform mints a fresh one on the next attempt.
/// </summary>
internal sealed class FileTransfers(IHttpClientFactory http, ReceiverDb db, TimeProvider clock, ILogger<FileTransfers> logger)
{
    public async Task<string> FetchAsync(string webhookId, string type, JsonElement data, CancellationToken ct)
    {
        if (!data.TryGetProperty("download", out var link) || link.ValueKind != JsonValueKind.Object || link.GetProperty("url").GetString() is not { } url) return "no_link";
        var expectedSha = data.TryGetProperty("sha256", out var s) ? s.GetString() : null;
        var expectedSize = data.TryGetProperty("size_bytes", out var z) && z.TryGetInt64(out var n) ? n : (long?)null;
        var started = clock.GetUtcNow();

        using var response = await http.CreateClient("files").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Gone)
        {
            await Record(webhookId, type, url, expectedSha, null, expectedSize, null, "link_expired", $"HTTP {(int)response.StatusCode}", started, ct);
            logger.LogWarning("The download link of {WebhookId} was refused ({Status}); the platform sends a fresh one with the next delivery", webhookId, (int)response.StatusCode);
            return "link_expired";
        }
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
            }
        }
        var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var ok = string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase) && (expectedSize is null || expectedSize == total);
        await Record(webhookId, type, url, expectedSha, actualSha, expectedSize, total, ok ? "verified" : "mismatch", ok ? null : "The checksum or the size is not what the platform announced", started, ct);
        return ok ? "file_verified" : "checksum_mismatch";
    }

    private Task Record(string webhookId, string type, string url, string? expectedSha, string? actualSha, long? expectedSize, long? actualSize, string verdict, string? error, DateTimeOffset started, CancellationToken ct) =>
        db.ExecuteAsync("""
            INSERT dbo.file_transfer (webhook_id, type, url_host, expected_sha256, actual_sha256, expected_size, actual_size, verdict, error, duration_ms)
            VALUES (@webhook, @type, @host, @esha, @asha, @esize, @asize, @verdict, @error, @ms)
            """, ct, ("@webhook", webhookId), ("@type", type), ("@host", new Uri(url).Authority), ("@esha", expectedSha), ("@asha", actualSha), ("@esize", expectedSize), ("@asize", actualSize),
            ("@verdict", verdict), ("@error", error), ("@ms", (int)(clock.GetUtcNow() - started).TotalMilliseconds));
}

/// <summary>Thin events carry only an id. This is the follow-up: pull the details with the API key and keep them in the tenant's own system.</summary>
internal sealed class KycPuller(PlatformClient platform, PullConfig config, ReceiverDb db)
{
    public async Task<string> PullAsync(JsonElement data, CancellationToken ct)
    {
        if (!data.TryGetProperty("verification_id", out var idElement) || idElement.GetString() is not { } id) return "no_reference";
        if (!config.Configured) return "pull_not_configured";

        var details = await platform.GetKycAsync(id, ct);
        if (details is null) return "not_found";
        var d = details.Value;
        var subject = d.GetProperty("subject");
        await db.ExecuteAsync("""
            MERGE dbo.kyc_result AS t USING (SELECT @id AS verification_id) AS s ON t.verification_id = s.verification_id
            WHEN MATCHED THEN UPDATE SET decision = @decision, risk_score = @risk, subject_name = @name, document_number = @doc, country = @country, fetched_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (verification_id, decision, risk_score, subject_name, document_number, country) VALUES (@id, @decision, @risk, @name, @doc, @country);
            """, ct, ("@id", id), ("@decision", d.GetProperty("decision").GetString()), ("@risk", d.GetProperty("risk_score").GetInt32()), ("@name", subject.GetProperty("name").GetString()),
            ("@doc", subject.GetProperty("document_number").GetString()), ("@country", subject.GetProperty("country").GetString()));
        return "kyc_pulled";
    }
}

/// <summary>
/// "Did I miss anything?" The safety net under webhooks (R-02): walk the platform's event stream for a time window and compare it with the inbox. Whatever is missing is put into
/// the inbox as if it had arrived (marked as coming from a pull) and processed the normal way, so the same idempotency and sequence guard apply.
/// </summary>
internal sealed class Reconciler(PlatformClient platform, PullConfig config, ReceiverDb db, InboxSignal signal, TimeProvider clock)
{
    public sealed record Result(int Checked, int Missing, int Recovered, string? Problem);

    public async Task<Result> RunAsync(int hours, DateTime? since, CancellationToken ct)
    {
        if (!config.Configured) return new Result(0, 0, 0, "The pull API is not configured: set the platform address and an API key with the events:read scope.");

        var from = since?.ToUniversalTime() ?? clock.GetUtcNow().UtcDateTime.AddHours(-Math.Clamp(hours, 1, 720));
        int seen = 0, missing = 0, recovered = 0;
        try
        {
            await foreach (var e in platform.EventsAsync(from, ct))
            {
                seen++;
                var id = e.GetProperty("id").GetString()!;
                var known = await db.QueryAsync("SELECT 1 FROM dbo.inbox_message WHERE webhook_id = @id", r => r.GetInt32(0), ct, ("@id", id));

                if (known.Count > 0)
                    continue;

                missing++;
                var envelope = JsonSerializer.Serialize(new
                {
                    id, type = e.GetProperty("type").GetString(), api_version = "pull",
                    created_at = e.GetProperty("created_at"),aggregate_id = e.GetProperty("aggregate_id").GetString(),
                    sequence = e.GetProperty("sequence").GetInt64(), test = false,
                    payload_style = "pull", data = e.GetProperty("data"),
                });
                var added = await db.ExecuteAsync("""
                    IF NOT EXISTS (SELECT 1 FROM dbo.inbox_message WHERE webhook_id = @id)
                      INSERT dbo.inbox_message (webhook_id, type, aggregate_id, sequence, api_version, is_test, body, source)
                      VALUES (@id, @type, @aggregate, @sequence, 'pull', 0, @body, 'pull')
                    """, ct, ("@id", id), ("@type", e.GetProperty("type").GetString()), ("@aggregate", e.GetProperty("aggregate_id").GetString()), ("@sequence", e.GetProperty("sequence").GetInt64()), ("@body", envelope));
                if (added > 0) recovered++;
            }
        }
        catch (HttpRequestException ex)
        {
            return new Result(seen, missing, recovered, $"The pull API answered with an error ({ex.StatusCode?.ToString() ?? "no answer"}). Check the key and its scope.");
        }

        if (recovered > 0)
            signal.Notify();

        return new Result(seen, missing, recovered, null);
    }
}
