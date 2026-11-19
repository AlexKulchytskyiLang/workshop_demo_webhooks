using System.Security.Cryptography;
using System.Text;

namespace InvoiceHub.Webhooks.Core;

/// <summary>
/// Standard Webhooks signing (standardwebhooks.com). Headers: <c>webhook-id</c>, <c>webhook-timestamp</c> (unix seconds),
/// <c>webhook-signature</c> = space-separated list of <c>v1,&lt;base64 HMAC-SHA256&gt;</c>.
/// The signed content is <c>{id}.{timestamp}.{body}</c>. Several signatures in one header make key rotation overlap seamless:
/// the receiver accepts the request if ANY of them verifies (ASR-08, ASR-09).
/// </summary>
public static class WebhookSigning
{
    public const string SecretPrefix = "whsec_";
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    /// <summary>Receivers must reject timestamps further than this from their own clock (replay protection).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>A new random signing secret: 32 random bytes, base64, with the conventional prefix.</summary>
    public static string GenerateSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static byte[] SecretBytes(string secret)
    {
        var text = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        return Convert.FromBase64String(text);
    }

    public static string Sign(string secret, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(SecretBytes(secret), content));
    }

    /// <summary>The <c>webhook-signature</c> header value for a set of secrets. Newest first, as receivers (and humans reading logs) expect.</summary>
    public static string SignatureHeaderValue(IEnumerable<string> secretsNewestFirst, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var signatures = new List<string>();
        foreach (var secret in secretsNewestFirst) signatures.Add(Sign(secret, id, timestamp, body));
        return string.Join(' ', signatures);
    }

    public enum VerifyResult { Valid, BadSignature, TimestampOutOfTolerance, Malformed }

    /// <summary>Reference verifier (the demo receiver uses it): constant-time compare, timestamp tolerance, any matching signature wins.</summary>
    public static VerifyResult Verify(IEnumerable<string> secrets, string? id, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatureHeader) || !long.TryParse(timestampHeader, out var timestamp))
            return VerifyResult.Malformed;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age.Duration() > Tolerance) return VerifyResult.TimestampOutOfTolerance;

        var presented = signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var secret in secrets)
        {
            var expected = Encoding.ASCII.GetBytes(Sign(secret, id, timestamp, body));
            foreach (var candidate in presented)
            {
                var bytes = Encoding.ASCII.GetBytes(candidate);
                if (bytes.Length == expected.Length && CryptographicOperations.FixedTimeEquals(bytes, expected)) return VerifyResult.Valid;
            }
        }
        return VerifyResult.BadSignature;
    }
}

public static class SignatureHeaderBuilder
{
    public const string KeyIdHeader = "webhook-key-id";

    /// <summary>
    /// One signature per valid key, ready for the <c>webhook-signature</c> header. HMAC signatures (<c>v1</c>) come first, newest key first, so receivers that only
    /// read the first entry (some library versions do, Spike 0) still verify; asymmetric signatures (<c>v1a</c>) follow. During a rotation or an algorithm
    /// migration both old and new keys are listed: the receiver accepts the request if any one verifies, so nothing breaks while it switches.
    /// </summary>
    public static SignedHeaders Build(IEnumerable<SigningCredential> credentialsNewestFirst, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var all = credentialsNewestFirst.ToList();
        var signatures = new List<string>();
        foreach (var c in all.Where(c => c.Algorithm == SigningAlgorithms.Hmac))
            signatures.Add(WebhookSigning.Sign(System.Text.Encoding.UTF8.GetString(c.Material), id, timestamp, body));

        var asymmetric = all.Where(c => c.Algorithm == SigningAlgorithms.Es256).ToList();
        foreach (var c in asymmetric) signatures.Add(AsymmetricSigning.Sign(c.Material, id, timestamp, body));

        return new SignedHeaders(string.Join(' ', signatures), asymmetric.Count == 0 ? null : string.Join(' ', asymmetric.Select(c => c.KeyId)));
    }
}
