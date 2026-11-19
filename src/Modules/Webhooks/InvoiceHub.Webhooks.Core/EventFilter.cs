using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InvoiceHub.Webhooks.Core;

public sealed record FilterCondition(string Path, string Op, JsonElement? Value);

/// <summary>
/// The subscription filter (UX-SPEC D4): a small, typed, sandboxed language. A filter is an OR of groups; a group is an AND of conditions.
/// Evaluated at fan-out time on the canonical event, never on a pinned projection, so a version upgrade never changes WHAT a tenant receives.
/// No scripting, no functions, bounded size: it cannot loop, allocate wildly or reach anything outside the event.
/// </summary>
public sealed class EventFilter
{
    public const int MaxConditions = 10;
    public const int MaxPathLength = 100;

    private static readonly HashSet<string> Operators = ["eq", "ne", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "exists"];

    public static readonly EventFilter MatchAll = new([]);

    public IReadOnlyList<IReadOnlyList<FilterCondition>> Groups { get; }

    private EventFilter(IReadOnlyList<IReadOnlyList<FilterCondition>> groups) => Groups = groups;

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>Parses <c>{"groups":[[{"path":"data.amount","op":"gte","value":1000}, ...], ...]}</c>. Null or empty means "match everything".</summary>
    public static (EventFilter? Filter, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (MatchAll, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
                return (null, "A filter must be an object with a \"groups\" array.");

            var groups = new List<IReadOnlyList<FilterCondition>>();
            var total = 0;
            foreach (var groupElement in groupsElement.EnumerateArray())
            {
                if (groupElement.ValueKind != JsonValueKind.Array) return (null, "Each group must be an array of conditions.");
                var group = new List<FilterCondition>();
                foreach (var c in groupElement.EnumerateArray())
                {
                    if (++total > MaxConditions) return (null, $"At most {MaxConditions} conditions are allowed.");
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || !c.TryGetProperty("op", out var o) || o.ValueKind != JsonValueKind.String)
                        return (null, "Each condition needs a \"path\" and an \"op\".");

                    var path = p.GetString()!;
                    var op = o.GetString()!;
                    if (path.Length is 0 or > MaxPathLength || path.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
                        return (null, $"Invalid field path '{Truncate(path)}'.");
                    if (!Operators.Contains(op)) return (null, $"Unknown operator '{Truncate(op)}'.");

                    JsonElement? value = c.TryGetProperty("value", out var v) ? v.Clone() : null;
                    if (op != "exists" && value is null) return (null, $"Operator '{op}' needs a value.");
                    if (op is "in" or "nin" && value is { ValueKind: not JsonValueKind.Array }) return (null, $"Operator '{op}' needs an array value.");
                    group.Add(new FilterCondition(path, op, value));
                }
                if (group.Count > 0) groups.Add(group);
            }
            return (new EventFilter(groups), null);
        }
        catch (JsonException)
        {
            return (null, "The filter is not valid JSON.");
        }
    }

    /// <summary>Does the event match? <paramref name="root"/> is the canonical event: <c>{"type":..,"aggregate_id":..,"sequence":..,"data":{..}}</c>.</summary>
    public bool Matches(JsonElement root)
    {
        if (Groups.Count == 0) return true;
        return Groups.Any(group => group.All(c => Evaluate(c, root)));
    }

    /// <summary>Read-only expression for the portal (what the builder compiled to).</summary>
    public string ToExpression()
    {
        if (Groups.Count == 0) return "(all events)";
        var parts = Groups.Select(g => string.Join(" AND ", g.Select(Describe)));
        return Groups.Count == 1 ? parts.First() : string.Join(" OR ", parts.Select(p => $"({p})"));
    }

    private static bool Evaluate(FilterCondition c, JsonElement root)
    {
        var found = TryResolve(root, c.Path, out var actual);
        if (c.Op == "exists") return found == (c.Value is not { ValueKind: JsonValueKind.False });
        if (!found) return c.Op is "ne" or "nin"; // a missing field is "not equal" to anything

        var expected = c.Value!.Value;
        return c.Op switch
        {
            "eq" => JsonEquals(actual, expected),
            "ne" => !JsonEquals(actual, expected),
            "gt" => Compare(actual, expected) is { } r && r > 0,
            "gte" => Compare(actual, expected) is { } r && r >= 0,
            "lt" => Compare(actual, expected) is { } r && r < 0,
            "lte" => Compare(actual, expected) is { } r && r <= 0,
            "in" => expected.EnumerateArray().Any(e => JsonEquals(actual, e)),
            "nin" => !expected.EnumerateArray().Any(e => JsonEquals(actual, e)),
            "contains" => actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String
                          && actual.GetString()!.Contains(expected.GetString()!, StringComparison.OrdinalIgnoreCase),
            "startsWith" => actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String
                            && actual.GetString()!.StartsWith(expected.GetString()!, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
        }
        return true;
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDecimal() == b.GetDecimal();
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    private static int? Compare(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDecimal().CompareTo(b.GetDecimal());
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
        {
            // ISO dates compare chronologically, everything else as ordinal text
            if (DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var da)
                && DateTimeOffset.TryParse(b.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var db)) return da.CompareTo(db);
            return string.CompareOrdinal(a.GetString(), b.GetString());
        }
        return null;
    }

    private static string Describe(FilterCondition c)
    {
        var op = c.Op switch { "eq" => "=", "ne" => "!=", "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=", "in" => "in", "nin" => "not in", var o => o };
        return c.Op == "exists" ? $"{c.Path} exists" : $"{c.Path} {op} {c.Value!.Value.GetRawText()}";
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "...";
}

/// <summary>Builds the signed request body. The body is created once per delivery attempt and signed exactly as sent (raw bytes).</summary>
public static class PayloadBuilder
{
    public const string DefaultApiVersion = "2026-11-01";

    public static string EventId(Guid eventId, bool isTest) => isTest ? $"evt_test_{eventId:N}" : $"evt_{eventId:N}";

    /// <param name="style"><c>fat</c> carries the whole data object; <c>thin</c> keeps only the reference keys (ids) so personal data never leaves the platform (ASR-12).</param>
    public static byte[] Build(string style, Guid eventId, bool isTest, string type, string apiVersion, DateTimeOffset createdAt, string aggregateId, long sequence,
        string dataJson, IReadOnlyList<string> referenceKeys)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            using var data = JsonDocument.Parse(dataJson);
            w.WriteStartObject();
            w.WriteString("id", EventId(eventId, isTest));
            w.WriteString("type", type);
            w.WriteString("api_version", apiVersion);
            w.WriteString("created_at", createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("aggregate_id", aggregateId);
            w.WriteNumber("sequence", sequence);
            w.WriteBoolean("test", isTest);
            w.WriteString("payload_style", style);
            w.WritePropertyName("data");
            if (style == "thin") WriteReferences(w, data.RootElement, referenceKeys);
            else data.RootElement.WriteTo(w);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteReferences(Utf8JsonWriter w, JsonElement data, IReadOnlyList<string> keys)
    {
        w.WriteStartObject();
        foreach (var key in keys)
        {
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var v)) { w.WritePropertyName(key); v.WriteTo(w); }
        }
        w.WriteEndObject();
    }

    /// <summary>The object the filter is evaluated on.</summary>
    public static string CanonicalForFilter(string type, string aggregateId, long sequence, string dataJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":").Append(JsonSerializer.Serialize(type))
          .Append(",\"aggregate_id\":").Append(JsonSerializer.Serialize(aggregateId))
          .Append(",\"sequence\":").Append(sequence.ToString(CultureInfo.InvariantCulture))
          .Append(",\"data\":").Append(dataJson).Append('}');
        return sb.ToString();
    }
}
