using System.Security.Cryptography;
using System.Text;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Webhooks.Secrets;

/// <summary>
/// Signing keys are stored encrypted (AES-GCM, authenticated with the endpoint id so a ciphertext copied to another endpoint will not decrypt).
/// An endpoint can hold several valid keys at once: that is key rotation and algorithm migration (ASR-09). For HMAC the plaintext is the shared secret,
/// for ES256 it is the PKCS#8 private key, which is never shown to anyone, not even the owner.
/// </summary>
internal sealed class EndpointSecrets(WebhooksDbContext db, ISecretProtector protector)
{
    public static string NewKeyId() => "k_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public EndpointSecretEntity CreateHmac(Guid tenantId, Guid endpointId, DateTime now, string? secret = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        EndpointId = endpointId,
        SecretEnc = protector.Protect(Encoding.UTF8.GetBytes(secret ?? WebhookSigning.GenerateSecret()), KeyContext(endpointId)),
        KeyId = NewKeyId(),
        Algorithm = SigningAlgorithms.Hmac,
        ValidFrom = now,
        CreatedAt = now,
    };

    /// <summary>A new ES256 key pair: the private half encrypted into the secret row, the public half into the JWKS table.</summary>
    public (EndpointSecretEntity Secret, PublicKeyEntity Public) CreateEs256(Guid tenantId, Guid endpointId, DateTime now)
    {
        var keyId = NewKeyId();
        var pair = Es256Keys.Generate(keyId);
        var secret = new EndpointSecretEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EndpointId = endpointId,
            SecretEnc = protector.Protect(pair.Pkcs8, KeyContext(endpointId)),
            KeyId = keyId,
            Algorithm = SigningAlgorithms.Es256,
            ValidFrom = now,
            CreatedAt = now,
        };
        return (secret, new PublicKeyEntity { EndpointId = endpointId, Kid = keyId, JwkJson = pair.PublicJwk, ValidFrom = now });
    }

    /// <summary>The shared secret of an HMAC key (the only kind that can be revealed).</summary>
    public string DecryptHmac(EndpointSecretEntity secret) =>
        secret.Algorithm == SigningAlgorithms.Hmac
            ? Encoding.UTF8.GetString(protector.Unprotect(secret.SecretEnc, KeyContext(secret.EndpointId)))
            : throw new InvalidOperationException("Private keys are never revealed.");

    /// <summary>Every key valid right now, newest first: what the sender signs with.</summary>
    public async Task<IReadOnlyList<SigningCredential>> LoadCredentialsAsync(Guid endpointId, DateTime now, CancellationToken ct)
    {
        var rows = await db.Secrets.AsNoTracking()
            .Where(s => s.EndpointId == endpointId && s.ValidFrom <= now && (s.ValidUntil == null || s.ValidUntil > now))
            .OrderByDescending(s => s.ValidFrom)
            .ToListAsync(ct);
        return rows.Select(r => new SigningCredential(r.Algorithm, r.KeyId, protector.Unprotect(r.SecretEnc, KeyContext(r.EndpointId)))).ToList();
    }

    // header values and the delivery credential are protected the same way, with their own context
    public byte[] ProtectValue(string value, string context) => protector.Protect(Encoding.UTF8.GetBytes(value), context);

    public string UnprotectValue(byte[] blob, string context) => Encoding.UTF8.GetString(protector.Unprotect(blob, context));

    public static string KeyContext(Guid endpointId) => $"endpoint:{endpointId:N}";

    public static string AuthContext(Guid endpointId) => $"auth:{endpointId:N}";

    public static string HeaderContext(Guid endpointId, string name) => $"header:{endpointId:N}:{name.ToLowerInvariant()}";
}

internal static class SecretFactory
{
    public static string Generate() => WebhookSigning.GenerateSecret();
}
