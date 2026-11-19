using System.Text.RegularExpressions;

namespace InvoiceHub.Webhooks.Core;

/// <summary>A static header the tenant wants on every request (UX-SPEC D11). A secret value is encrypted at rest and never shown again; null means "keep what is stored".</summary>
public sealed record CustomHeader(string Name, string? Value, bool Secret);

public static partial class HeaderRules
{
    public const int MaxHeaders = 10;
    public const int MaxValueLength = 1000;
    public const int MaxTotalBytes = 2048;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-type", "content-length", "content-encoding", "transfer-encoding", "connection", "user-agent", "traceparent", "tracestate",
        "webhook-id", "webhook-timestamp", "webhook-signature", "webhook-key-id", "proxy-authorization", "cookie", "set-cookie", "expect", "upgrade", "te", "trailer",
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,49}$")]
    private static partial Regex NamePattern();

    /// <param name="deliveryAuthHeader">The header the delivery authentication already uses (for example Authorization): a custom header may not collide with it.</param>
    public static IEnumerable<string> Validate(IReadOnlyList<CustomHeader> headers, string? deliveryAuthHeader)
    {
        if (headers.Count > MaxHeaders) yield return $"At most {MaxHeaders} custom headers are allowed.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var h in headers)
        {
            var name = h.Name?.Trim() ?? "";
            if (!NamePattern().IsMatch(name)) { yield return $"'{Short(name)}' is not a valid header name (letters, digits and hyphens, up to 50 characters)."; continue; }
            if (Reserved.Contains(name) || name.StartsWith("webhook-", StringComparison.OrdinalIgnoreCase)) yield return $"'{name}' is reserved and cannot be set.";
            if (deliveryAuthHeader is not null && string.Equals(name, deliveryAuthHeader, StringComparison.OrdinalIgnoreCase)) yield return $"'{name}' is already used by the delivery authentication.";
            if (!seen.Add(name)) yield return $"'{name}' is listed twice.";
            if (h.Value is null)
            {
                if (!h.Secret) yield return $"'{name}' needs a value.";
                continue;
            }
            if (h.Value.Length > MaxValueLength) yield return $"The value of '{name}' is longer than {MaxValueLength} characters.";
            if (h.Value.Any(char.IsControl)) yield return $"The value of '{name}' contains a control character (line breaks are not allowed).";
            total += name.Length + h.Value.Length;
        }
        if (total > MaxTotalBytes) yield return $"The custom headers are larger than {MaxTotalBytes} bytes in total.";
    }

    /// <summary>The header carrying the delivery credential (an API key or bearer token). Unlike custom headers it may be Authorization.</summary>
    public static IEnumerable<string> ValidateAuthHeaderName(string? name)
    {
        if (name is null || !NamePattern().IsMatch(name)) yield return "The authentication header name must use letters, digits and hyphens (up to 50 characters).";
        else if (Reserved.Contains(name) || name.StartsWith("webhook-", StringComparison.OrdinalIgnoreCase)) yield return $"'{name}' is reserved and cannot carry the credential.";
    }

    private static string Short(string s) => s.Length <= 30 ? s : s[..30] + "...";
}
