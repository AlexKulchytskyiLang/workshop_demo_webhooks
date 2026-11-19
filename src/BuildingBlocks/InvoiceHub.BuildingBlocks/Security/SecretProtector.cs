using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.BuildingBlocks.Security;

public sealed class SecretProtectorOptions
{
    public const string Section = "Secrets";

    /// <summary>Master secret (at least 32 characters). The AES key is derived from it with HKDF. Production: Key Vault with per-tenant data keys.</summary>
    [Required, MinLength(32)]
    public string MasterKey { get; set; } = "";
}

/// <summary>
/// Reversible encryption for signing secrets (HMAC secrets must be stored reversibly: we sign with them).
/// AES-256-GCM; the context (for example the endpoint id) is authenticated, so a ciphertext copied to another row will not decrypt.
/// Layout: version(1) | nonce(12) | tag(16) | ciphertext.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, string context);

    byte[] Unprotect(ReadOnlySpan<byte> blob, string context);
}

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmSecretProtector(string masterKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterKey);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(masterKey), 32, salt: null, info: "invoicehub-secrets-v1"u8.ToArray());
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string context)
    {
        var blob = new byte[1 + NonceSize + TagSize + plaintext.Length];
        blob[0] = Version;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), Encoding.UTF8.GetBytes(context));
        return blob;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> blob, string context)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != Version)
            throw new CryptographicException("Unknown secret format.");
        var plaintext = new byte[blob.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(blob.Slice(1, NonceSize), blob[(1 + NonceSize + TagSize)..], blob.Slice(1 + NonceSize, TagSize), plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }
}

public static class SecretProtectorExtensions
{
    public static IServiceCollection AddSecretProtector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecretProtectorOptions>().Bind(configuration.GetSection(SecretProtectorOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ISecretProtector>(sp =>
            new AesGcmSecretProtector(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecretProtectorOptions>>().Value.MasterKey));
        return services;
    }
}
