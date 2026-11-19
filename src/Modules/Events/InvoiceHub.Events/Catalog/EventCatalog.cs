using System.Text.Json;
using InvoiceHub.Events.Contracts;

namespace InvoiceHub.Events.Catalog;

/// <summary>The event catalogue (42 events in 7 categories), loaded once from the shared catalogue file.</summary>
internal sealed class EventCatalog : IEventCatalog
{
    private readonly Dictionary<string, EventTypeInfo> _byType;

    public IReadOnlyList<CatalogCategory> Categories { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Bundles { get; }

    public IReadOnlyDictionary<string, CatalogTemplate> Templates { get; }

    private EventCatalog(IReadOnlyList<CatalogCategory> categories, IReadOnlyDictionary<string, IReadOnlyList<string>> bundles, IReadOnlyDictionary<string, CatalogTemplate> templates)
    {
        Categories = categories;
        Bundles = bundles;
        Templates = templates;
        _byType = categories.SelectMany(c => c.Events).ToDictionary(e => e.Type, StringComparer.Ordinal);
    }

    public EventTypeInfo? Find(string type) => _byType.GetValueOrDefault(type);

    public bool Exists(string type) => _byType.ContainsKey(type);

    public static EventCatalog LoadEmbedded()
    {
        using var stream = typeof(EventCatalog).Assembly.GetManifestResourceStream("InvoiceHub.Events.Catalog.event-catalog.json")
            ?? throw new InvalidOperationException("event-catalog.json is not embedded.");
        return Parse(JsonDocument.Parse(stream).RootElement);
    }

    internal static EventCatalog Parse(JsonElement root)
    {
        var categories = root.GetProperty("categories").EnumerateArray().Select(c =>
        {
            var name = c.GetProperty("name").GetString()!;
            var pii = c.GetProperty("pii").GetBoolean();
            var events = c.GetProperty("events").EnumerateArray().Select(e => new EventTypeInfo(
                e.GetProperty("type").GetString()!,
                e.GetProperty("description").GetString()!,
                e.GetProperty("payload").GetString()!,
                pii,
                name,
                e.GetProperty("referenceKeys").EnumerateArray().Select(k => k.GetString()!).ToList())).ToList();
            return new CatalogCategory(name, pii, events);
        }).ToList();

        var bundles = root.GetProperty("bundles").EnumerateObject()
            .ToDictionary(p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(x => x.GetString()!).ToList());

        var templates = root.GetProperty("templates").EnumerateObject().ToDictionary(p => p.Name, p => new CatalogTemplate(
            p.Value.TryGetProperty("name", out var n) ? n.GetString() : null,
            p.Value.TryGetProperty("desc", out var d) ? d.GetString() : null,
            p.Value.TryGetProperty("events", out var ev) ? ev.EnumerateArray().Select(x => x.GetString()!).ToList() : []));

        return new EventCatalog(categories, bundles, templates);
    }
}
