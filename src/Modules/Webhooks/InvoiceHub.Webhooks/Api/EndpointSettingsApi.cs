using System.Security.Claims;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.Webhooks.Api;

internal sealed record AutoDisableInput(int Failures, int WithinHours, int Days);

internal sealed record DeliveryInput(string? Preset, RetryParameters? Custom, int? CircuitFailures, AutoDisableInput? AutoDisable, int? HoldDays, int? TimeoutSeconds, int? MaxInflight);

internal sealed record HeaderInput(string? Name, string? Value, bool Secret);

internal sealed record HeadersInput(List<HeaderInput>? Headers);

internal sealed record AuthInput(string? Type, string? HeaderName, string? Value);

internal sealed record SigningInput(string? Mode, int? OverlapHours);

internal sealed record RotateInput(int? OverlapHours, string? Algorithm);

internal sealed record CheckUrlInput(string? Url, Guid? EndpointId);

internal sealed record FilterTestInput(string? EventType, JsonElement? Filter);

internal sealed record PreviewInput(string? Preset, RetryParameters? Custom);

/// <summary>The tabs of an endpoint workspace that change settings: Delivery &amp; resilience, Security, Events &amp; filters (headers, filter test). Each change validates BEFORE it saves.</summary>
internal static class EndpointSettingsApi
{
    private static readonly int[] OverlapChoices = [0, 24, 168];
    private static readonly string[] RetryPresets = ["standard", "aggressive", "demo", "custom"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/portal/endpoints").WithTags("Endpoint settings");
        g.MapPut("/{id:guid}/delivery", SetDelivery).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapPut("/{id:guid}/headers", SetHeaders).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapPut("/{id:guid}/auth", SetAuth).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapPut("/{id:guid}/signing", SetSigning).RequireAuthorization(Permissions.SecretsReveal);
        g.MapPost("/{id:guid}/secret/rotate", Rotate).RequireAuthorization(Permissions.SecretsReveal);
        g.MapPost("/{id:guid}/secret/reveal", Reveal).RequireAuthorization(Permissions.SecretsReveal);
        g.MapPost("/check-url", CheckUrl).RequireAuthorization(Permissions.EndpointsWrite);
        g.MapPost("/filter-test", FilterTest).RequireAuthorization(Permissions.EndpointsWrite);

        app.MapGet("/api/portal/retry-presets", RetryPresetList).RequireAuthorization(Permissions.EndpointsRead).WithTags("Endpoint settings");
        app.MapPost("/api/portal/retry-preview", RetryPreview).RequireAuthorization(Permissions.EndpointsRead).WithTags("Endpoint settings");

        // receivers fetch the public keys without signing in: public data, rate limited, cacheable
        app.MapGet("/api/public/endpoints/{id:guid}/jwks.json", Jwks).AllowAnonymous().RequireRateLimiting(RateLimiting.Public).WithTags("Public");
    }

    // ---------------------------------------------------------------- delivery & resilience

    private static async Task<IResult> SetDelivery(Guid id, DeliveryInput input, HttpRequest request, WebhooksDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();

        var preset = input.Preset ?? e.RetryPreset;
        if (!RetryPresets.Contains(preset)) return Problems.Validation("invalid_retry_preset", "The retry preset must be standard, aggressive, demo or custom.");

        string? customJson = null;
        if (preset == "custom")
        {
            var custom = input.Custom ?? (RetryParameters.TryParse(e.RetryJson, out var stored) ? stored : null);
            if (custom is null) return Problems.Validation("invalid_retry", "Custom retries need their settings.");
            var errors = custom.Validate().ToList();
            if (errors.Count > 0) return Problems.Validation("invalid_retry", string.Join(" ", errors));
            customJson = custom.ToJson();
        }

        var circuit = input.CircuitFailures ?? e.CircuitFailures;
        if (circuit is < CircuitBreaker.MinThreshold or > CircuitBreaker.MaxThreshold)
            return Problems.Validation("invalid_circuit", $"The circuit breaker opens after {CircuitBreaker.MinThreshold} to {CircuitBreaker.MaxThreshold} consecutive failures.");

        var ad = input.AutoDisable ?? new AutoDisableInput(e.AutodisableFailures, e.AutodisableHours, e.AutodisableDays);
        if (ad.Failures is < 5 or > 10_000) return Problems.Validation("invalid_auto_disable", "Auto-disable needs between 5 and 10,000 consecutive failures.");
        if (ad.WithinHours is < 1 or > 168) return Problems.Validation("invalid_auto_disable", "The auto-disable time window must be between 1 hour and 7 days.");
        if (ad.Days is < 1 or > 30) return Problems.Validation("invalid_auto_disable", "Auto-disable after continuous failure needs between 1 and 30 days.");
        if (ad.Failures <= circuit) return Problems.Validation("invalid_auto_disable", "Auto-disable must need more failures than the circuit breaker, otherwise the breaker never gets to act first.");

        var hold = input.HoldDays ?? e.HoldDays;
        if (hold is < 1 or > 30) return Problems.Validation("invalid_hold", "Held events are kept for 1 to 30 days.");
        var timeout = input.TimeoutSeconds ?? e.TimeoutSeconds;
        if (timeout is < 1 or > 30) return Problems.Validation("invalid_timeout", "The timeout must be between 1 and 30 seconds.");
        var inflight = input.MaxInflight ?? e.MaxInflight;
        if (inflight is < 1 or > 10) return Problems.Validation("invalid_concurrency", "Concurrent requests must be between 1 and 10.");

        e.RetryPreset = preset;
        e.RetryJson = customJson;
        e.CircuitFailures = circuit;
        e.AutodisableFailures = ad.Failures;
        e.AutodisableHours = ad.WithinHours;
        e.AutodisableDays = ad.Days;
        e.HoldDays = hold;
        e.TimeoutSeconds = timeout;
        e.MaxInflight = inflight;
        e.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveAsync(audit, new AuditEntry("endpoint.delivery_changed", "endpoint", id.ToString(), e.Name,
            new { preset, circuitFailures = circuit, autoDisable = ad, holdDays = hold, timeoutSeconds = timeout, maxInflight = inflight }), ct);
        return Results.Ok(EndpointViews.Delivery(e));
    }

    private static IResult RetryPresetList() => Results.Ok(new
    {
        presets = new[]
        {
            Describe("standard", "Standard", "Retries for up to 72 hours with growing delays (5 s, 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 8 h, 12 h). Right for most receivers."),
            Describe("aggressive", "Fast recovery", "8 attempts in the first hour, first retry after 1 second. For receivers that are either up or clearly down."),
            Describe("demo", "Workshop demo", "The same shape compressed to 10 minutes, so you can watch retries happen."),
        },
        customDefault = RetryParameters.Default,
        backoffs = RetryParameters.Backoffs,
        maxWindowSeconds = RetryParameters.MaxWindowSeconds,
    });

    private static object Describe(string id, string name, string description)
    {
        var policy = RetryPolicy.FromName(id);
        return new { id, name, description, windowSeconds = (int)policy.Window.TotalSeconds, attempts = policy.PreviewAttempts().Count };
    }

    /// <summary>The schedule shown next to the settings is computed by the same code that delivers, so the preview cannot disagree with reality.</summary>
    private static IResult RetryPreview(PreviewInput input)
    {
        var preset = input.Preset ?? "standard";
        if (!RetryPresets.Contains(preset)) return Problems.Validation("invalid_retry_preset", "The retry preset must be standard, aggressive, demo or custom.");

        var errors = new List<string>();
        RetryPolicy policy;
        if (preset == "custom")
        {
            var custom = input.Custom ?? RetryParameters.Default;
            errors.AddRange(custom.Validate());
            policy = errors.Count == 0 ? custom.ToPolicy() : RetryPolicy.Standard;
        }
        else
        {
            policy = RetryPolicy.FromName(preset);
        }

        var attempts = errors.Count > 0 ? [] : policy.PreviewAttempts().Select(a => new { number = a.Number, delaySeconds = (int)a.Delay.TotalSeconds, atSeconds = (int)a.At.TotalSeconds }).ToList();
        return Results.Ok(new
        {
            valid = errors.Count == 0,
            errors,
            attempts,
            windowSeconds = (int)policy.Window.TotalSeconds,
            maxAttempts = policy.MaxAttempts,
            jitterPercent = (int)(policy.Jitter * 100),
        });
    }

    // ---------------------------------------------------------------- custom headers and delivery authentication

    private static async Task<IResult> SetHeaders(Guid id, HeadersInput input, WebhooksDbContext db, HeaderStore store, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();

        var headers = (input.Headers ?? []).Select(h => new CustomHeader(h.Name ?? "", h.Value, h.Secret)).ToList();
        var errors = HeaderRules.Validate(headers, e.AuthType == "header" ? e.AuthHeaderName : null).ToList();
        if (errors.Count > 0) return Problems.Validation("invalid_headers", string.Join(" ", errors));

        e.HeadersJson = store.Write(id, headers, e.HeadersJson);
        e.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveAsync(audit, new AuditEntry("endpoint.headers_changed", "endpoint", id.ToString(), e.Name,
            new { names = headers.Select(h => h.Name), secret = headers.Where(h => h.Secret).Select(h => h.Name) }), ct); // names only, never values
        return Results.Ok(new { headers = HeaderStore.ToDto(e.HeadersJson) });
    }

    private static async Task<IResult> SetAuth(Guid id, AuthInput input, WebhooksDbContext db, EndpointSecrets secrets, HeaderStore store, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();

        switch (input.Type)
        {
            case "none":
                e.AuthType = "none";
                e.AuthHeaderName = null;
                e.AuthValueEnc = null;
                break;

            case "header":
                var nameErrors = HeaderRules.ValidateAuthHeaderName(input.HeaderName).ToList();
                if (nameErrors.Count > 0) return Problems.Validation("invalid_auth_header", string.Join(" ", nameErrors));
                if (string.IsNullOrEmpty(input.Value) && e.AuthValueEnc is not { Length: > 0 }) return Problems.Validation("invalid_auth_value", "Enter the key or token we should send.");
                if (input.Value is not null && (input.Value.Length > HeaderRules.MaxValueLength || input.Value.Any(char.IsControl)))
                    return Problems.Validation("invalid_auth_value", "The value is too long or contains a control character.");
                if (HeaderStore.Read(e.HeadersJson).Any(h => string.Equals(h.Name, input.HeaderName, StringComparison.OrdinalIgnoreCase)))
                    return Problems.Validation("invalid_auth_header", $"'{input.HeaderName}' is also a custom header. Remove it there first.");

                e.AuthType = "header";
                e.AuthHeaderName = input.HeaderName;
                if (!string.IsNullOrEmpty(input.Value)) e.AuthValueEnc = secrets.ProtectValue(input.Value, EndpointSecrets.AuthContext(id));
                break;

            default:
                return Problems.Validation("invalid_auth_type", "Delivery authentication must be none or header. OAuth client credentials and mutual TLS are planned.");
        }

        e.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveAsync(audit, new AuditEntry("endpoint.auth_changed", "endpoint", id.ToString(), e.Name, new { type = e.AuthType, headerName = e.AuthHeaderName, valueChanged = !string.IsNullOrEmpty(input.Value) }), ct);
        return Results.Ok(EndpointViews.Auth(e));
    }

    // ---------------------------------------------------------------- message signing: keys, rotation, reveal

    private static async Task<IResult> SetSigning(
        Guid id, SigningInput input, ClaimsPrincipal user, WebhooksDbContext db, EndpointSecrets secrets, IAuditLog audit, TimeProvider clock, ILogger<SigningInput> logger, CancellationToken ct)
    {
        if (RequireStepUp(user, clock) is { } stepUp) return stepUp;
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();
        if (input.Mode is not ("hmac" or "es256" or "both")) return Problems.Validation("invalid_signing", "Signing must be hmac, es256 or both.");
        var overlap = input.OverlapHours ?? 24;
        if (!OverlapChoices.Contains(overlap)) return Problems.Validation("invalid_overlap", "The overlap must be 0 (immediately), 24 or 168 hours.");

        var now = clock.GetUtcNow().UtcDateTime;
        var wanted = input.Mode switch { "hmac" => new[] { SigningAlgorithms.Hmac }, "es256" => [SigningAlgorithms.Es256], _ => [SigningAlgorithms.Hmac, SigningAlgorithms.Es256] };
        var valid = await ValidKeysAsync(db, id, now, ct);

        foreach (var algorithm in wanted.Where(a => valid.All(k => k.Algorithm != a))) AddKey(db, secrets, e, algorithm, now); // a new algorithm gets its first key
        foreach (var old in valid.Where(k => !wanted.Contains(k.Algorithm))) Expire(db, old, now.AddHours(overlap)); // a dropped algorithm lives on for the overlap

        e.Signing = input.Mode;
        e.UpdatedAt = now;
        await db.SaveAsync(audit, new AuditEntry("endpoint.signing_changed", "endpoint", id.ToString(), e.Name, new { mode = input.Mode, overlapHours = overlap }), ct);
        logger.LogInformation("Signing mode of endpoint {EndpointId} changed to {Mode} by {Actor}", id, input.Mode, EndpointViews.Actor(user));
        return Results.Ok(new { signing = e.Signing, keys = EndpointViews.Keys(await db.Secrets.AsNoTracking().Where(s => s.EndpointId == id).ToListAsync(ct), now) });
    }

    private static async Task<IResult> Rotate(
        Guid id, RotateInput input, ClaimsPrincipal user, WebhooksDbContext db, EndpointSecrets secrets, IAuditLog audit, TimeProvider clock, ILogger<RotateInput> logger, CancellationToken ct)
    {
        if (RequireStepUp(user, clock) is { } stepUp) return stepUp;
        var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();
        var overlap = input.OverlapHours ?? 24;
        if (!OverlapChoices.Contains(overlap)) return Problems.Validation("invalid_overlap", "The overlap must be 0 (immediately), 24 or 168 hours.");

        var now = clock.GetUtcNow().UtcDateTime;
        var algorithms = e.Signing switch { "hmac" => new[] { SigningAlgorithms.Hmac }, "es256" => [SigningAlgorithms.Es256], _ => [SigningAlgorithms.Hmac, SigningAlgorithms.Es256] };
        if (input.Algorithm is not null)
        {
            if (!algorithms.Contains(input.Algorithm)) return Problems.Validation("invalid_algorithm", $"This endpoint does not sign with {input.Algorithm}.");
            algorithms = [input.Algorithm];
        }

        var valid = await ValidKeysAsync(db, id, now, ct);
        foreach (var algorithm in algorithms)
        {
            // the new key signs from now on; the old one keeps signing alongside it for the overlap, so a receiver that has not switched yet still verifies
            foreach (var old in valid.Where(k => k.Algorithm == algorithm)) Expire(db, old, now.AddHours(overlap));
            AddKey(db, secrets, e, algorithm, now);
        }
        e.UpdatedAt = now;
        await db.SaveAsync(audit, new AuditEntry("secret.rotated", "endpoint", id.ToString(), e.Name, new { algorithms, overlapHours = overlap }), ct);
        logger.LogInformation("Signing keys of endpoint {EndpointId} rotated by {Actor} with {Overlap} h overlap", id, EndpointViews.Actor(user), overlap);
        return Results.Ok(new { keys = EndpointViews.Keys(await db.Secrets.AsNoTracking().Where(s => s.EndpointId == id).ToListAsync(ct), now) });
    }

    /// <summary>Shows the current HMAC secret. Needs the right permission AND a fresh sign-in (step-up): a stolen or idle session cannot do it. Private keys are never shown.</summary>
    private static async Task<IResult> Reveal(Guid id, ClaimsPrincipal user, WebhooksDbContext db, EndpointSecrets secrets, IAuditLog audit, TimeProvider clock, ILogger<AuthInput> logger, CancellationToken ct)
    {
        if (RequireStepUp(user, clock) is { } stepUp) return stepUp;
        var now = clock.GetUtcNow().UtcDateTime;
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return EndpointsApi.NotFound();

        var current = await db.Secrets.AsNoTracking()
            .Where(s => s.EndpointId == id && s.Algorithm == SigningAlgorithms.Hmac && s.ValidFrom <= now && (s.ValidUntil == null || s.ValidUntil > now))
            .OrderByDescending(s => s.ValidFrom).FirstOrDefaultAsync(ct);
        if (current is null) return Problems.Conflict("no_secret", "This endpoint has no shared secret. ES256 private keys are never shown; use the public keys (JWKS) to verify.");

        // no trace, no secret: the line is written first, and a failure to write it fails the request
        await audit.WriteAsync(null, new AuditEntry("secret.revealed", "endpoint", id.ToString(), e.Name, new { keyId = current.KeyId, stepUp = true }), ct);
        logger.LogInformation("Signing secret of endpoint {EndpointId} revealed by {Actor}", id, EndpointViews.Actor(user));
        return Results.Ok(new { secret = secrets.DecryptHmac(current), keyId = current.KeyId });
    }

    private static IResult? RequireStepUp(ClaimsPrincipal user, TimeProvider clock) =>
        user.IsFresh(clock.GetUtcNow())
            ? null
            : Problems.Create(StatusCodes.Status403Forbidden, "step_up_required", "Sign in again to continue", "This action needs a fresh sign-in (within the last 5 minutes).");

    private static Task<List<EndpointSecretEntity>> ValidKeysAsync(WebhooksDbContext db, Guid id, DateTime now, CancellationToken ct) =>
        db.Secrets.Where(s => s.EndpointId == id && s.ValidFrom <= now && (s.ValidUntil == null || s.ValidUntil > now)).ToListAsync(ct);

    private static void AddKey(WebhooksDbContext db, EndpointSecrets secrets, EndpointEntity e, string algorithm, DateTime now)
    {
        if (algorithm == SigningAlgorithms.Hmac)
        {
            db.Secrets.Add(secrets.CreateHmac(e.TenantId, e.Id, now));
            return;
        }
        var (secret, publicKey) = secrets.CreateEs256(e.TenantId, e.Id, now);
        db.Secrets.Add(secret);
        db.PublicKeys.Add(publicKey);
    }

    private static void Expire(WebhooksDbContext db, EndpointSecretEntity key, DateTime until)
    {
        key.ValidUntil = until;
        if (key.Algorithm != SigningAlgorithms.Es256) return;
        var published = db.PublicKeys.Local.FirstOrDefault(p => p.EndpointId == key.EndpointId && p.Kid == key.KeyId)
            ?? db.PublicKeys.FirstOrDefault(p => p.EndpointId == key.EndpointId && p.Kid == key.KeyId);
        if (published is not null) published.ValidUntil = until;
    }

    private static async Task<IResult> Jwks(Guid id, WebhooksDbContext db, TimeProvider clock, HttpResponse response, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var keys = await db.PublicKeys.AsNoTracking().Where(k => k.EndpointId == id && k.ValidFrom <= now && (k.ValidUntil == null || k.ValidUntil > now))
            .OrderByDescending(k => k.ValidFrom).Select(k => k.JwkJson).ToListAsync(ct);
        response.Headers.CacheControl = "public, max-age=60";
        return Results.Content($$"""{"keys":[{{string.Join(",", keys)}}]}""", "application/jwk-set+json");
    }

    // ---------------------------------------------------------------- checks while typing

    /// <summary>The URL checklist of the Create screen: each line is a real check with its reason (UX-SPEC "URL validation: pending, ok, error").</summary>
    private static async Task<IResult> CheckUrl(CheckUrlInput input, WebhooksDbContext db, SsrfGuard guard, CancellationToken ct)
    {
        var checks = new List<object>();
        object Check(string id, string status, string text) => new { id, status, text };

        if (string.IsNullOrWhiteSpace(input.Url) || !Uri.TryCreate(input.Url.Trim(), UriKind.Absolute, out var uri))
        {
            checks.Add(Check("format", "error", "Enter a full address such as https://erp.example.com/hooks/invoicehub."));
            return Results.Ok(new { ok = false, checks });
        }

        var shape = guard.CheckShape(uri);
        if (shape is not null)
        {
            checks.Add(Check("https", "error", shape.Message));
            return Results.Ok(new { ok = false, checks });
        }
        checks.Add(Check("https", "ok", uri.Scheme == Uri.UriSchemeHttps ? "HTTPS. The certificate and TLS version are checked when we send the verification request." : "Plain HTTP, allowed here only because this is a local demo receiver."));

        var decision = await guard.EvaluateAsync(uri, ct);
        if (!decision.Allowed)
        {
            checks.Add(Check("address", "error", decision.Message));
            return Results.Ok(new { ok = false, checks });
        }
        checks.Add(Check("address", "ok", decision.DevException
            ? $"Resolves to {decision.Address}. Allowed by the demo egress policy (Development only)."
            : $"Resolves to a public address ({decision.Address}). Private, loopback and link-local ranges are blocked."));
        checks.Add(Check("redirects", "ok", "We never follow redirects. Answer with 2xx directly."));

        var duplicate = await db.Endpoints.AsNoTracking().Where(e => e.Url == input.Url.Trim() && e.Id != input.EndpointId).Select(e => e.Name).FirstOrDefaultAsync(ct);
        if (duplicate is not null) checks.Add(Check("duplicate", "warn", $"Another endpoint, “{duplicate}”, already uses this URL. Events would reach it twice."));

        checks.Add(Check("ownership", "todo", "After you create it we send a signed webhook.ping. Answer with 2xx and the endpoint goes live."));
        return Results.Ok(new { ok = true, checks });
    }

    /// <summary>"Test against your latest events": the real filter engine, on the tenant's real recent events, before anything is saved.</summary>
    private static async Task<IResult> FilterTest(FilterTestInput input, IEventReader events, IEventCatalog catalog, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.EventType) || !catalog.Exists(input.EventType)) return Problems.Validation("unknown_event", $"Unknown event type '{input.EventType}'.");

        var raw = input.Filter is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } f ? f.GetRawText() : null;
        var (filter, error) = EventFilter.Parse(raw);
        if (filter is null) return Results.Ok(new { valid = false, error, expression = (string?)null, rows = Array.Empty<object>(), matched = 0, total = 0 });

        var recent = await events.RecentAsync(input.EventType, 20, ct);
        var rows = recent.Select(ev =>
        {
            using var canonical = JsonDocument.Parse(PayloadBuilder.CanonicalForFilter(ev.Type, ev.AggregateId, ev.Sequence, ev.DataJson));
            using var data = JsonDocument.Parse(ev.DataJson);
            return new { eventId = ev.Id, occurredAt = ev.OccurredAt, aggregateId = ev.AggregateId, summary = Summarize(data.RootElement), matched = filter.Matches(canonical.RootElement) };
        }).ToList();

        return Results.Ok(new { valid = true, error = (string?)null, expression = filter.ToExpression(), rows, matched = rows.Count(r => r.matched), total = rows.Count });
    }

    /// <summary>A few readable top-level values of an event for the test table. Objects and arrays are skipped; PII classes carry only ids anyway.</summary>
    private static Dictionary<string, string> Summarize(JsonElement data)
    {
        var summary = new Dictionary<string, string>();
        if (data.ValueKind != JsonValueKind.Object) return summary;
        foreach (var p in data.EnumerateObject())
        {
            if (summary.Count >= 4) break;
            if (p.Name.EndsWith("_id", StringComparison.Ordinal)) continue; // ids repeat the event column
            if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                summary[p.Name] = p.Value.ToString().Length > 40 ? p.Value.ToString()[..40] : p.Value.ToString();
        }
        return summary;
    }
}
