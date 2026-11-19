using System.Text.Json;
using InvoiceHub.Webhooks.Core;

namespace InvoiceHub.Webhooks.Secrets;

/// <summary>One stored custom header. For a secret header <see cref="Value"/> is the base64 ciphertext, otherwise the plain value.</summary>
internal sealed record StoredHeader(string Name, string Value, bool Secret);

/// <summary>
/// Custom request headers (UX-SPEC D11): static name/value pairs, some of them secret. Secret values are encrypted at rest, come back masked from the API,
/// and are decrypted only at the moment a request is built. Sending a secret header again WITHOUT a value keeps the stored one.
/// </summary>
internal sealed class HeaderStore(EndpointSecrets secrets)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<StoredHeader> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Select(e => new StoredHeader(e.GetProperty("name").GetString() ?? "", e.GetProperty("value").GetString() ?? "", e.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.True))
                .Where(h => h.Name.Length > 0)
                .ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return []; // never let an unreadable value stop deliveries
        }
    }

    /// <summary>The JSON to store for the incoming headers; secrets are encrypted, and a secret sent without a value keeps its stored ciphertext.</summary>
    public string? Write(Guid endpointId, IReadOnlyList<CustomHeader> incoming, string? existingJson)
    {
        if (incoming.Count == 0) return null;
        var existing = Read(existingJson).ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
        var stored = incoming.Select(h =>
        {
            var name = h.Name.Trim();
            if (!h.Secret) return new StoredHeader(name, h.Value ?? "", false);
            if (h.Value is null) return existing.TryGetValue(name, out var keep) && keep.Secret ? keep with { Name = name } : new StoredHeader(name, "", true);
            return new StoredHeader(name, Convert.ToBase64String(secrets.ProtectValue(h.Value, EndpointSecrets.HeaderContext(endpointId, name))), true);
        }).ToList();
        return JsonSerializer.Serialize(stored.Select(h => new { name = h.Name, value = h.Value, secret = h.Secret }), Json);
    }

    /// <summary>What the portal may see: secret values are never returned.</summary>
    public static IEnumerable<object> ToDto(string? json) =>
        Read(json).Select(h => new { name = h.Name, value = h.Secret ? null : h.Value, secret = h.Secret, hasValue = h.Value.Length > 0 });

    /// <summary>The headers to put on a request: decrypted, and with anything unsafe already filtered out when they were saved.</summary>
    public Dictionary<string, string> ToRequestHeaders(Guid endpointId, string? json)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in Read(json))
        {
            if (h.Value.Length == 0) continue;
            try
            {
                headers[h.Name] = h.Secret ? secrets.UnprotectValue(Convert.FromBase64String(h.Value), EndpointSecrets.HeaderContext(endpointId, h.Name)) : h.Value;
            }
            catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
            {
                // a value that cannot be decrypted is skipped rather than failing every delivery
            }
        }
        return headers;
    }
}
