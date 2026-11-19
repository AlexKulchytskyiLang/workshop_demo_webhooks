using System.Text.Json;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InvoiceHub.Webhooks.Api;

/// <summary>
/// Payload versions (UX-SPEC D5): what exists, and what an upgrade would change, shown on a real sample of the event BEFORE anything is switched.
/// Upgrading is the tenant's decision: nothing moves on its own, and deliveries already created keep the version they were created with.
/// </summary>
internal static class VersionsApi
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portal/api-versions", () => Results.Ok(ApiVersions.All.Select(v => new
        {
            version = v.Version, status = v.Status, summary = v.Summary, sunsetOn = v.SunsetOn, changes = v.Changes, isDefault = v.Version == ApiVersions.Current,
        }))).RequireAuthorization(Permissions.EndpointsRead).WithTags("Versions");

        app.MapGet("/api/portal/api-versions/diff", Diff).RequireAuthorization(Permissions.EndpointsRead).WithTags("Versions");
    }

    private static IResult Diff(string from, string to, string type, IEventCatalog catalog)
    {
        if (!ApiVersions.IsSupported(from) || !ApiVersions.IsSupported(to)) return Problems.Validation("invalid_api_version", $"Versions: {string.Join(", ", ApiVersions.Supported)}.");
        if (!catalog.Exists(type)) return Problems.Validation("unknown_event", $"Unknown event type '{type}'.");

        var canonical = TestSamples.Data(type);
        return Results.Ok(new
        {
            type,
            from = new { version = from, data = Render(from, type, canonical), changes = ApiVersions.Find(from)!.Changes },
            to = new { version = to, data = Render(to, type, canonical), changes = ApiVersions.Find(to)!.Changes },
            deliveriesKeepTheirVersion = true,
        });
    }

    private static JsonElement Render(string version, string type, string canonical) => JsonDocument.Parse(VersionProjection.Project(version, type, canonical)).RootElement.Clone();

    internal static string PrettyJson(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Pretty);
}
