using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InvoiceHub.Webhooks.Core;

public static class SigningAlgorithms
{
    public const string Hmac = "hmac";
    public const string Es256 = "es256";
}

/// <summary>A key we sign with right now. HMAC: the UTF-8 bytes of the <c>whsec_</c> secret. ES256: the PKCS#8 private key.</summary>
public sealed record SigningCredential(string Algorithm, string KeyId, byte[] Material);

/// <summary>The two headers that carry our signatures. <c>webhook-key-id</c> lists the ids of the asymmetric keys, in the order of the <c>v1a</c> signatures.</summary>
public sealed record SignedHeaders(string Signature, string? KeyIds);

public sealed record Es256KeyPair(string KeyId, byte[] Pkcs8, string PublicJwk);

/// <summary>
/// ES256 (ECDSA over P-256 with SHA-256) for tenants who want to verify without holding a secret: we sign with a private key, they verify with the public
/// key we publish as a JWKS. The signature is the raw 64-byte r||s form (IEEE P1363) in standard base64, listed as <c>v1a,&lt;base64&gt;</c>, over exactly the
/// same <c>id.timestamp.body</c> content as the HMAC signature. (Standard Webhooks reserves <c>v1a</c> for Ed25519, which .NET does not ship; this is the
/// asymmetric option the BCL provides.)
/// </summary>
public static class Es256Keys
{
    public static Es256KeyPair Generate(string keyId)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new Es256KeyPair(keyId, ecdsa.ExportPkcs8PrivateKey(), PublicJwk(keyId, ecdsa.ExportParameters(false)));
    }

    public static string PublicJwk(string keyId, ECParameters publicKey) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["use"] = "sig",
            ["alg"] = "ES256",
            ["kid"] = keyId,
            ["x"] = Base64Url(publicKey.Q.X!),
            ["y"] = Base64Url(publicKey.Q.Y!),
        });

    public static string PublicJwk(string keyId, ReadOnlySpan<byte> pkcs8)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return PublicJwk(keyId, ecdsa.ExportParameters(false));
    }

    /// <summary>Reads a JWKS document the way a receiver does. Keys that are not P-256 signing keys are ignored.</summary>
    public static IReadOnlyList<(string KeyId, ECParameters Key)> ParseJwks(string json)
    {
        var keys = new List<(string, ECParameters)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("keys", out var array) || array.ValueKind != JsonValueKind.Array) return keys;
        foreach (var k in array.EnumerateArray())
        {
            if (Text(k, "kty") != "EC" || Text(k, "crv") != "P-256" || Text(k, "x") is not { } x || Text(k, "y") is not { } y) continue;
            keys.Add((Text(k, "kid") ?? "", new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = FromBase64Url(x), Y = FromBase64Url(y) },
            }));
        }
        return keys;
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class AsymmetricSigning
{
    public const string Prefix = "v1a,";

    public static string Sign(ReadOnlySpan<byte> pkcs8, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        var signature = ecdsa.SignData(Content(id, timestamp, body), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Prefix + Convert.ToBase64String(signature);
    }

    /// <summary>Reference verifier for receivers (the demo receiver uses it): any <c>v1a</c> signature that any published key verifies is enough.</summary>
    public static WebhookSigning.VerifyResult Verify(IEnumerable<ECParameters> publicKeys, string? id, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatureHeader) || !long.TryParse(timestampHeader, out var timestamp)) return WebhookSigning.VerifyResult.Malformed;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > WebhookSigning.Tolerance) return WebhookSigning.VerifyResult.TimestampOutOfTolerance;

        var content = Content(id, timestamp, body);
        foreach (var entry in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entry.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            byte[] signature;
            try { signature = Convert.FromBase64String(entry[Prefix.Length..]); }
            catch (FormatException) { continue; }

            foreach (var key in publicKeys)
            {
                using var ecdsa = ECDsa.Create(key);
                if (ecdsa.VerifyData(content, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return WebhookSigning.VerifyResult.Valid;
            }
        }
        return WebhookSigning.VerifyResult.BadSignature;
    }

    private static byte[] Content(string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        return content;
    }
}
