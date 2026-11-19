using System.Globalization;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Events.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Events;

/// <summary>
/// The pull API (<c>GET /v1/events</c>, API key with the <c>events:read</c> scope): the event stream, oldest first, so a receiver can reconcile what it holds against what
/// happened (the safety net under webhooks, ASR-01/R-02). Events of a personal-data class are returned thin, exactly as they are pushed: ids only, never the personal fields.
/// </summary>
internal static class PullApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app) => app.MapGet("/v1/events", List)
                                                            .RequireApiScope(ApiScopes.EventsRead)
                                                            .WithTags("Pull API");

    private static async Task<IResult> List(EventsDbContext db, IEventCatalog catalog, string? type, DateTime? from, string? after, int? limit, CancellationToken ct)
    {
        from = from.AsUtc();
        var take = Math.Clamp(limit ?? 50, 1, 100);
        if (type is not null && !catalog.Exists(type)) return Problems.Validation("unknown_event", $"Unknown event type '{type}'.");

        var query = db.Events.AsNoTracking().Where(x => !x.IsTest);
        if (type is not null) query = query.Where(x => x.Type == type);
        if (from is { } f) query = query.Where(x => x.OccurredAt >= f);
        if (after is not null)
        {
            if (!TryParseCursor(after, out var at, out var id)) return Problems.Validation("invalid_cursor", "The cursor is not valid.");
            query = query.Where(x => x.OccurredAt > at || (x.OccurredAt == at && x.Id.CompareTo(id) > 0));
        }

        var rows = await query.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        return Results.Ok(new
        {
            items = page.Select(e => new
            {
                id = $"evt_{e.Id:N}",
                type = e.Type,
                created_at = new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc)),
                aggregate_id = e.AggregateId,
                sequence = e.Sequence,
                data = Project(catalog.Find(e.Type), e.DataJson),
            }),
            next = rows.Count > take ? Cursor(page[^1]) : null,
        });
    }

    private static JsonElement Project(EventTypeInfo? info, string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        if (info?.Payload == "claim") return WithoutInternalFile(doc.RootElement);
        if (info?.ContainsPii != true) return doc.RootElement.Clone();
        var thin = new Dictionary<string, JsonElement>();
        foreach (var key in info.ReferenceKeys)
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v)) thin[key] = v.Clone();
        return JsonSerializer.SerializeToElement(thin, Json);
    }

    /// <summary>A big-result event keeps its storage reference inside the platform; the pull API shows size and checksum, and links are only minted per delivery attempt.</summary>
    private static JsonElement WithoutInternalFile(JsonElement data)
    {
        var copy = new Dictionary<string, JsonElement>();
        foreach (var p in data.EnumerateObject())
        {
            if (p.Name != "file") { copy[p.Name] = p.Value.Clone(); continue; }
            if (p.Value.TryGetProperty("size", out var size)) copy["size_bytes"] = size.Clone();
            if (p.Value.TryGetProperty("sha256", out var sha)) copy["sha256"] = sha.Clone();
            if (p.Value.TryGetProperty("content_type", out var type)) copy["content_type"] = type.Clone();
        }
        return JsonSerializer.SerializeToElement(copy, Json);
    }

    private static string Cursor(EventEntity e) => $"{e.OccurredAt.Ticks.ToString(CultureInfo.InvariantCulture)}_{e.Id:N}";

    private static bool TryParseCursor(string cursor, out DateTime at, out Guid id)
    {
        at = default;
        id = default;
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out id)) return false;
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
        at = new DateTime(ticks, DateTimeKind.Unspecified);
        return true;
    }
}
