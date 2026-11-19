using System.Text.Json.Nodes;

namespace InvoiceHub.Webhooks.Core;

/// <summary>One dated version of the event payloads (UX-SPEC D5). Within a version fields are only ever ADDED; anything else gets a new date.</summary>
/// <param name="Status"><c>older</c> (still supported, will end), <c>current</c>, or <c>preview</c> (can still change, not for production).</param>
/// <param name="Changes">What differs from the current version, in plain words, for the upgrade screen.</param>
public sealed record ApiVersionInfo(string Version, string Status, string Summary, string? SunsetOn, IReadOnlyList<string> Changes);

public static class ApiVersions
{
    public const string Older = "2026-06-01";
    public const string Current = "2026-11-01";
    public const string Preview = "2027-02-01";

    public static readonly IReadOnlyList<ApiVersionInfo> All =
    [
        new(Older, "older", "The first public shape. Still supported.", "2027-06-01",
        [
            "The customer is two flat fields (customer_name, customer_country) instead of a customer object.",
            "Invoices have no paid_at field.",
        ]),
        new(Current, "current", "The shape new integrations should use.", null, []),
        new(Preview, "preview", "Money is grouped. Try it in the Playground; it can still change before it is released.", null,
        [
            "amount and currency move into one total object (total.amount, total.currency), on invoices and payments.",
            "customer.name is called customer.display_name.",
        ]),
    ];

    public static IReadOnlyList<string> Supported => All.Select(v => v.Version).ToArray();

    public static bool IsSupported(string? version) => All.Any(v => v.Version == version);

    public static ApiVersionInfo? Find(string? version) => All.FirstOrDefault(v => v.Version == version);
}

/// <summary>
/// Turns the canonical event (always written in the current shape) into the shape an endpoint pinned. It runs when the delivery is BUILT, so a delivery that is already
/// queued keeps the version it was created with, and filters (which run on the canonical event) never change meaning when an endpoint upgrades.
/// </summary>
public static class VersionProjection
{
    public static string Project(string version, string eventType, string canonicalDataJson)
    {
        if (version == ApiVersions.Current || !ApiVersions.IsSupported(version)) return canonicalDataJson;
        if (JsonNode.Parse(canonicalDataJson) is not JsonObject node) return canonicalDataJson;

        var invoice = eventType.StartsWith("invoice.", StringComparison.Ordinal);
        var payment = eventType.StartsWith("payment.", StringComparison.Ordinal);

        if (version == ApiVersions.Older && invoice)
        {
            if (node["customer"] is JsonObject customer)
            {
                node.Remove("customer");
                node["customer_name"] = customer["name"]?.DeepClone();
                node["customer_country"] = customer["country"]?.DeepClone();
            }
            node.Remove("paid_at");
        }
        else if (version == ApiVersions.Preview && (invoice || payment))
        {
            GroupMoney(node);
            if (invoice && node["customer"] is JsonObject c && c["name"] is { } name)
            {
                c.Remove("name");
                c["display_name"] = name.DeepClone();
            }
        }
        return node.ToJsonString();
    }

    private static void GroupMoney(JsonObject node)
    {
        if (node["amount"] is null && node["currency"] is null) return;
        var total = new JsonObject { ["amount"] = node["amount"]?.DeepClone(), ["currency"] = node["currency"]?.DeepClone() };
        node.Remove("amount");
        node.Remove("currency");
        node["total"] = total;
    }
}
