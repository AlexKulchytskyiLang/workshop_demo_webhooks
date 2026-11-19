using System.Security.Cryptography;
using System.Text.Json;
using InvoiceHub.Webhooks.Core;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Receiver.Api;

/// <summary>
/// What this application trusts, the way a real integration would configure it: shared secrets (HMAC, pasted from the sender's portal into the app's
/// configuration) and/or the sender's public key set (JWKS, fetched from a URL and cached). Changing it takes effect at once and survives a restart.
/// A request is accepted when ANY trusted secret or ANY published key verifies it, which is what makes key rotation and algorithm migration seamless.
/// </summary>
internal sealed class TrustStore(IOptions<ReceiverOptions> options, ReceiverDb db, IHttpClientFactory http, ILogger<TrustStore> logger)
{
    private static readonly TimeSpan JwksTtl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private volatile TrustState _state = new([], null);
    private (string Url, DateTimeOffset At, IReadOnlyList<ECParameters> Keys)? _jwks;

    private sealed record TrustState(IReadOnlyList<string> Secrets, string? JwksUrl);

    public async Task LoadAsync(CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT json FROM dbo.trust_config WHERE id = 1", r => r.GetString(0), ct);
        if (rows.Count == 0)
            return;

        var saved = JsonSerializer.Deserialize<TrustRequest>(rows[0], Json);
        if (saved is not null)
            _state = new TrustState(saved.Secrets ?? [], saved.JwksUrl);
    }

    public async Task SetAsync(IReadOnlyList<string> secrets, string? jwksUrl, CancellationToken ct)
    {
        _state = new TrustState(secrets.Distinct().ToList(), jwksUrl);
        _jwks = null;
        await db.ExecuteAsync("""
            MERGE dbo.trust_config AS t USING (SELECT 1 AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET json = @json
            WHEN NOT MATCHED THEN INSERT (id, json) VALUES (1, @json);
            """, ct, ("@json", JsonSerializer.Serialize(new TrustRequest([.. secrets], jwksUrl), Json)));
    }

    /// <summary>Shown to the person setting it up: the secrets are masked, never echoed.</summary>
    public object Describe() => new
    {
        secrets = AllSecrets().Select(s => s.Length > 12 ? s[..6] + "…" + s[^4..] : "…").ToList(),
        jwksUrl = _state.JwksUrl,
        configured = AllSecrets().Any() || _state.JwksUrl is not null,
    };

    private IEnumerable<string> AllSecrets() => options.Value.Secrets.Concat(_state.Secrets).Distinct();

    public async Task<WebhookSigning.VerifyResult> VerifyAsync(string? id, string? timestamp, string? signature,
                                                               byte[] body, DateTimeOffset now, CancellationToken ct)
    {
        var symmetric = WebhookSigning.Verify(AllSecrets(), id, timestamp, signature, body, now);
        if (symmetric == WebhookSigning.VerifyResult.Valid)
            return symmetric;
        if (_state.JwksUrl is not { } url || signature is null || !signature.Contains("v1a,", StringComparison.Ordinal))
            return symmetric;

        var keys = await KeysAsync(url, force: false, ct);
        var asymmetric = AsymmetricSigning.Verify(keys.Select(k => k), id, timestamp, signature, body, now);
        if (asymmetric == WebhookSigning.VerifyResult.BadSignature)
        {
            // an unknown key id usually means the sender rotated: refresh the key set once and try again
            keys = await KeysAsync(url, force: true, ct);
            asymmetric = AsymmetricSigning.Verify(keys, id, timestamp, signature, body, now);
        }
        return asymmetric == WebhookSigning.VerifyResult.Valid ? asymmetric : symmetric == WebhookSigning.VerifyResult.BadSignature ? asymmetric : symmetric;
    }

    private async Task<IReadOnlyList<ECParameters>> KeysAsync(string url, bool force, CancellationToken ct)
    {
        if (!force && _jwks is { } cached && cached.Url == url && DateTimeOffset.UtcNow - cached.At < JwksTtl)
            return cached.Keys;

        try
        {
            var json = await http.CreateClient("jwks").GetStringAsync(url, ct);
            var keys = Es256Keys.ParseJwks(json).Select(k => k.Key).ToList();
            _jwks = (url, DateTimeOffset.UtcNow, keys);
            return keys;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(e, "Could not fetch the public keys from {Url}", url);
            return _jwks?.Keys ?? [];
        }
    }
}
