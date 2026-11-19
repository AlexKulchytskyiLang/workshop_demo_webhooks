using System.Globalization;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;

namespace InvoiceHub.Webhooks.Sending;

/// <summary>What goes on the wire next to the body.</summary>
/// <param name="Headers">All request headers, including the signature(s), delivery credential and custom headers.</param>
internal sealed record BuiltRequest(Dictionary<string, string> Headers, long Timestamp, string WebhookId, int SignatureCount);

/// <summary>
/// Builds the headers of one request, the same way for a real delivery, a verification ping and a test event, so what the portal shows and tests
/// is exactly what production sends: custom headers first, then the delivery credential, then the standard webhook headers (which always win).
/// </summary>
internal sealed class RequestFactory(EndpointSecrets secrets, HeaderStore headers)
{
    public const string UserAgent = "InvoiceHub-Webhooks/1.0";

    /// <returns>Null when the endpoint has no valid signing key (a configuration problem the caller reports).</returns>
    public async Task<BuiltRequest?> BuildAsync(EndpointEntity endpoint, string webhookId, DateTimeOffset timestamp, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var credentials = await secrets.LoadCredentialsAsync(endpoint.Id, DateTime.UtcNow, ct);
        if (credentials.Count == 0) return null;

        var unix = timestamp.ToUnixTimeSeconds();
        var signed = SignatureHeaderBuilder.Build(credentials, webhookId, unix, body.Span);

        var result = headers.ToRequestHeaders(endpoint.Id, endpoint.HeadersJson);
        if (endpoint.AuthType == "header" && endpoint.AuthHeaderName is { Length: > 0 } name && endpoint.AuthValueEnc is { Length: > 0 } blob)
            result[name] = secrets.UnprotectValue(blob, EndpointSecrets.AuthContext(endpoint.Id));

        result[WebhookSigning.IdHeader] = webhookId;
        result[WebhookSigning.TimestampHeader] = unix.ToString(CultureInfo.InvariantCulture);
        result[WebhookSigning.SignatureHeader] = signed.Signature;
        if (signed.KeyIds is not null) result[SignatureHeaderBuilder.KeyIdHeader] = signed.KeyIds;
        result["User-Agent"] = UserAgent;

        return new BuiltRequest(result, unix, webhookId, signed.Signature.Split(' ').Length);
    }
}
