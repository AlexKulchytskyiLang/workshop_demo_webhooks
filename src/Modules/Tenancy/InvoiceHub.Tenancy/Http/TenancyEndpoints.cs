using System.Security.Claims;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Persistence;
using InvoiceHub.Tenancy.Registration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Tenancy.Http;

public sealed record MeUser(Guid? Id, string? Email, string Name, string Role, IReadOnlyList<string> Roles);

public sealed record MeTenant(Guid Id, string Slug, string Name);

/// <summary>What the portal needs right after sign-in: who you are, which tenant, and what you may do (the portal gates its UI on this list).</summary>
public sealed record MeResponse(MeUser User, MeTenant Tenant, IReadOnlyCollection<string> Permissions);

internal static class TenancyEndpoints
{
    public const string PublicRateLimit = "public";
    public const string RegistrationRateLimit = "registration";

    public static void Map(IEndpointRouteBuilder app)
    {
        var publicApi = app.MapGroup("/api/public")
                           .WithTags("Tenancy (public)")
                           .AllowAnonymous();

        publicApi.MapGet("/tenants", async (ITenantDirectory directory, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=10";
            return Results.Ok(await directory.ListActiveAsync(ct));
        }).RequireRateLimiting(PublicRateLimit);

        publicApi.MapPost("/registrations", async (RegistrationRequest request, RegistrationService service, CancellationToken ct) =>
        {
            var outcome = await service.RegisterAsync(request, ct);
            return outcome switch
            {
                RegistrationOutcome.Created c => Results.Created($"/api/public/tenants/{c.Response.Slug}", c.Response),
                RegistrationOutcome.Invalid i => Results.ValidationProblem(i.Errors, extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" }),
                RegistrationOutcome.SlugTaken => Problems.Conflict("slug_taken", "This tenant address is already taken."),
                RegistrationOutcome.EmailTaken => Problems.Conflict("email_taken", "An account with this email already exists."),
                _ => Problems.Create(StatusCodes.Status503ServiceUnavailable, "registration_unavailable", "Registration unavailable",
                    "The identity service did not answer in time. Nothing was created; please try again."),
            };
        }).RequireRateLimiting(RegistrationRateLimit);

        app.MapGet("/api/me", async (ClaimsPrincipal user, HttpContext http, TenancyDbContext db, CancellationToken ct) =>
        {
            var tenant = (TenantInfo)http.Items[TenantResolutionMiddleware.TenantItemKey]!;
            var roles = user.TenantRoles();
            var primary = RolePermissions.Primary(roles);
            if (primary is null)
                return Problems.Forbidden("no_role", "This account has no role in the tenant.");

            if (user.UserId() is { } sub)
            {
                var now = DateTime.UtcNow;
                await db.UserProfiles.Where(u => u.KeycloakSub == sub).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now), ct);

            }

            return Results.Ok(new MeResponse(
                new MeUser(user.UserId(), user.FindFirstValue("email"), user.DisplayName(), primary, roles),
                new MeTenant(tenant.Id, tenant.Slug, tenant.Name),
                RolePermissions.For(roles)));
        }).RequireAuthorization()
          .WithTags("Tenancy");
    }
}
