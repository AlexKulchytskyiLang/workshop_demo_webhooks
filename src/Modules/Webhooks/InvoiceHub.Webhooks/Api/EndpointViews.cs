using System.Security.Claims;
using System.Text.Json;
using InvoiceHub.Webhooks.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;
using Microsoft.AspNetCore.Http;

namespace InvoiceHub.Webhooks.Api;

internal sealed record EndpointInput(
    string? Name, string? Url, string? Description, string[]? Events, string? PayloadStyle, string? ApiVersion,
    string? RetryPreset, int? TimeoutSeconds, int? MaxInflight, string? Environment = null, string? Signing = null);

internal sealed record SubscriptionInput(string? EventType, JsonElement? Filter);

internal sealed record SubscriptionsInput(List<SubscriptionInput>? Subscriptions);

/// <summary>The JSON the portal receives about an endpoint. One place, so every screen agrees on health, backlog and key information.</summary>
internal static class EndpointViews
{
    public static readonly string[] Environments = ["production", "staging", "development"];

    public static object Summary(EndpointEntity e, DateTime now, int subscriptions, IReadOnlyDictionary<string, int> backlog, string jwksUrl) => new
    {
        id = e.Id,
        name = e.Name,
        url = e.Url,
        description = e.Description,
        environment = e.Environment,
        status = e.Status,
        health = HealthOf(e, now),
        disabledReason = e.DisabledReason,
        payloadStyle = e.PayloadStyle,
        apiVersion = e.ApiVersion,
        signing = e.Signing,
        retryPreset = e.RetryPreset,
        timeoutSeconds = e.TimeoutSeconds,
        maxInflight = e.MaxInflight,
        verifiedAt = Utc(e.VerifiedAt),
        consecutiveFailures = e.ConsecutiveFailures,
        circuitFailures = e.CircuitFailures,
        autoDisableFailures = e.AutodisableFailures,
        circuitOpenUntil = e.CircuitOpenUntil is { } c && c > now ? Utc(c) : null,
        lastSuccessAt = Utc(e.LastSuccessAt),
        lastFailureAt = Utc(e.LastFailureAt),
        subscriptions,
        backlog = new
        {
            pending = backlog.GetValueOrDefault(DeliveryStatus.Pending) + backlog.GetValueOrDefault(DeliveryStatus.InFlight),
            retrying = backlog.GetValueOrDefault(DeliveryStatus.RetryScheduled),
            held = backlog.GetValueOrDefault(DeliveryStatus.Held),
        },
        jwksUrl,
        createdAt = Utc(e.CreatedAt),
    };

    /// <summary>The status model of UX-SPEC D3: stored status plus what the counters say right now.</summary>
    public static string HealthOf(EndpointEntity e, DateTime now) => e.Status switch
    {
        EndpointStatus.Pending => "pending_verification",
        EndpointStatus.Paused => "paused",
        EndpointStatus.Disabled => e.DisabledReason == "auto_disabled" ? "auto_disabled" : "disabled",
        _ when e.CircuitOpenUntil > now => "circuit_open",
        _ when e.ConsecutiveFailures > 0 => "degraded",
        _ => "healthy",
    };

    public static object Delivery(EndpointEntity e)
    {
        var custom = RetryParameters.TryParse(e.RetryJson, out var parsed) ? parsed : null;
        return new
        {
            preset = e.RetryPreset,
            custom,
            circuitFailures = e.CircuitFailures,
            autoDisable = new { failures = e.AutodisableFailures, withinHours = e.AutodisableHours, days = e.AutodisableDays },
            holdDays = e.HoldDays,
            timeoutSeconds = e.TimeoutSeconds,
            maxInflight = e.MaxInflight,
        };
    }

    public static object Auth(EndpointEntity e) => new { type = e.AuthType, headerName = e.AuthHeaderName, hasValue = e.AuthValueEnc is { Length: > 0 } };

    /// <summary>Which keys exist and until when. Never the keys themselves.</summary>
    public static IEnumerable<object> Keys(IEnumerable<EndpointSecretEntity> rows, DateTime now) =>
        rows.OrderByDescending(r => r.ValidFrom).Select(r => new
        {
            keyId = r.KeyId,
            algorithm = r.Algorithm,
            validFrom = Utc(r.ValidFrom),
            validUntil = Utc(r.ValidUntil),
            current = r.ValidFrom <= now && (r.ValidUntil == null || r.ValidUntil > now),
        });

    public static string JwksUrl(HttpRequest request, Guid endpointId) =>
        $"{request.Scheme}://{request.Host}/api/public/endpoints/{endpointId}/jwks.json";

    public static DateTimeOffset? Utc(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));

    public static string Actor(ClaimsPrincipal user) => user.FindFirstValue("preferred_username") ?? user.FindFirstValue("sub") ?? "unknown";
}
