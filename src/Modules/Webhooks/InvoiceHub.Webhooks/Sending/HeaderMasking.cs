using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;

namespace InvoiceHub.Webhooks.Sending;

/// <summary>
/// What the portal and the delivery log may show of a request: the signature is shortened, the delivery credential and every secret custom header are hidden.
/// The log stores the masked form, never the original, so a credential cannot leak out of it (ASR-24).
/// </summary>
internal static class HeaderMasking
{
    public const string Mask = "••••••••";

    public static Dictionary<string, string> Apply(EndpointEntity endpoint, IReadOnlyDictionary<string, string> headers)
    {
        var secretNames = HeaderStore.Read(endpoint.HeadersJson).Where(h => h.Secret).Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            var sensitive = (endpoint.AuthType == "header" && string.Equals(name, endpoint.AuthHeaderName, StringComparison.OrdinalIgnoreCase))
                || secretNames.Contains(name)
                || name.Contains("key", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, SignatureHeaderBuilder.KeyIdHeader, StringComparison.OrdinalIgnoreCase)
                || name.Contains("token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || name.Equals("authorization", StringComparison.OrdinalIgnoreCase);
            shown[name] = name == WebhookSigning.SignatureHeader
                ? string.Join(' ', value.Split(' ').Select(s => s.Length > 14 ? s[..14] + "..." : s))
                : sensitive ? Mask : value;
        }
        return shown;
    }
}
