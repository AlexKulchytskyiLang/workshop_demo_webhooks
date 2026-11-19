using System.Globalization;
using System.Text.Json.Nodes;
using InvoiceHub.BuildingBlocks.Storage;

namespace InvoiceHub.Webhooks.Delivery;

/// <summary>
/// The claim-check envelope (ASR-18). A big result (a 50 MB export, an import report) is stored once; its event carries an INTERNAL file reference in the canonical data.
/// What a receiver is sent instead is a small envelope: size, checksum, content type and a download link that is minted again for every attempt and expires.
/// The internal reference (container, blob name) never leaves the platform.
/// </summary>
internal static class ClaimCheckEnvelope
{
    /// <summary>The data a receiver gets. Events without a file reference (a test event, for example) pass through unchanged.</summary>
    public static string Apply(string dataJson, IClaimCheckStore? store, TimeSpan validFor)
    {
        if (!TryRead(dataJson, out var node, out var blob)) return dataJson;
        Describe(node, blob);
        if (store is not null)
        {
            var link = store.CreateDownloadLink(blob, validFor, Path.GetFileName(blob.Name));
            node["download"] = new JsonObject
            {
                ["url"] = link.Url.ToString(),
                ["expires_at"] = link.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
        }
        return node.ToJsonString();
    }

    /// <summary>The same envelope for display in the delivery log: the live link is replaced by a note, so no working link is ever shown or stored.</summary>
    public static string Redact(string dataJson, TimeSpan validFor)
    {
        if (!TryRead(dataJson, out var node, out var blob)) return dataJson;
        Describe(node, blob);
        node["download"] = new JsonObject { ["url"] = $"(a new link is minted for every attempt, valid for {validFor.TotalMinutes:0} minutes)", ["expires_at"] = null };
        return node.ToJsonString();
    }

    private static void Describe(JsonObject node, BlobRef blob)
    {
        node.Remove("file");
        node["size_bytes"] = blob.Size;
        node["sha256"] = blob.Sha256;
        node["content_type"] = blob.ContentType;
    }

    private static bool TryRead(string dataJson, out JsonObject node, out BlobRef blob)
    {
        node = null!;
        blob = null!;
        if (JsonNode.Parse(dataJson) is not JsonObject root || root["file"] is not JsonObject file) return false;
        var container = file["container"]?.GetValue<string>();
        var name = file["name"]?.GetValue<string>();
        if (container is null || name is null) return false;
        node = root;
        blob = new BlobRef(container, name, file["size"]?.GetValue<long>() ?? 0, file["sha256"]?.GetValue<string>() ?? "", file["content_type"]?.GetValue<string>() ?? "application/octet-stream");
        return true;
    }
}
