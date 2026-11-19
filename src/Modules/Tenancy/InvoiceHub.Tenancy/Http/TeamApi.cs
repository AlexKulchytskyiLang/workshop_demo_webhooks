using System.Security.Claims;
using System.Security.Cryptography;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Keycloak;
using InvoiceHub.Tenancy.Persistence;
using InvoiceHub.Tenancy.Registration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.Tenancy.Http;

internal sealed record InviteInput(string? Email, string? FirstName, string? LastName, string? Role);

internal sealed record RoleInput(string? Role);

/// <summary>
/// Team and roles (Settings). Keycloak stays the source of truth for identity and roles; the profile table is the tenant's view of its members.
/// Guard rails: nobody can lock the tenant out (the last active owner cannot be demoted or disabled), only an owner can create or change an owner,
/// and you cannot change your own role or disable yourself.
/// </summary>
internal static class TeamApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/team")
                       .WithTags("Team")
                       .RequireAuthorization(Permissions.RolesManage);

        group.MapGet("/", List);
        group.MapPost("/", Invite);
        group.MapPut("/{id:guid}/role", ChangeRole);
        group.MapPost("/{id:guid}/disable", Disable);
        group.MapPost("/{id:guid}/enable", Enable);
    }

    private static async Task<IResult> List(ClaimsPrincipal user, TenancyDbContext db, CancellationToken ct)
    {
        var members = await db.UserProfiles.AsNoTracking().OrderBy(u => u.Role == Roles.Owner ? 0 : u.Role == Roles.Admin ? 1 : u.Role == Roles.Developer ? 2 : u.Role == Roles.Support ? 3 : 4).ThenBy(u => u.DisplayName).ToListAsync(ct);
        var me = user.UserId();
        return Results.Ok(new
        {
            items = members.Select(u => new
            {
                u.Id,
                u.Email,
                name = u.DisplayName,
                u.Role,
                active = u.DisabledAt is null,
                you = u.KeycloakSub == me,
                lastSeenAt = Utc(u.LastSeenAt),
                createdAt = Utc(u.CreatedAt),
                disabledAt = Utc(u.DisabledAt),
            }),
            roles = Roles.All.Select(r => new { role = r, permissions = RolePermissions.For([r]) }),
            canManageOwners = user.TenantRoles().Contains(Roles.Owner),
        });
    }

    private static async Task<IResult> Invite(
        InviteInput input, ClaimsPrincipal user, ITenantContext tenant, TenancyDbContext db, IKeycloakAdmin keycloak, ITenantDirectory directory, IAuditLog audit, TimeProvider clock, ILogger<InviteInput> logger, CancellationToken ct)
    {
        var email = input.Email?.Trim().ToLowerInvariant();
        if (!RegistrationRules.IsEmail(email))
            return Problems.Validation("invalid_email", "Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(input.FirstName) || input.FirstName.Length > 60 || string.IsNullOrWhiteSpace(input.LastName) || input.LastName.Length > 60)
            return Problems.Validation("invalid_name", "Enter a first and a last name (up to 60 characters each).");

        if (input.Role is null || !Roles.All.Contains(input.Role))
            return Problems.Validation("invalid_role", $"The role must be one of: {string.Join(", ", Roles.All)}.");
        if (input.Role == Roles.Owner && !user.TenantRoles().Contains(Roles.Owner))
            return Problems.Forbidden("owner_only", "Only an owner can add another owner.");
        if (await db.UserProfiles.CountAsync(ct) >= 50)
            return Problems.Conflict("team_limit", "A tenant can have at most 50 members.");

        var tenantId = tenant.TenantId!.Value;
        var info = await directory.FindByIdAsync(tenantId, ct);
        var password = TemporaryPassword();
        Guid keycloakId;
        try
        {
            keycloakId = await keycloak.CreateUserAsync(new NewKeycloakUser(email!, input.FirstName.Trim(), input.LastName.Trim(), password, tenantId, info!.Slug, TemporaryPassword: true), ct);
            await keycloak.AssignRealmRoleAsync(keycloakId, input.Role, ct);
        }
        catch (KeycloakConflictException)
        {
            return Problems.Conflict("email_taken", "An account with this email already exists.");
        }
        catch (KeycloakUnavailableException e)
        {
            logger.LogError(e, "Could not create the team member in Keycloak");
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, "identity_unavailable", "Identity service unavailable", "Nothing was created. Try again in a moment.");
        }

        var profile = new UserProfileEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            KeycloakSub = keycloakId,
            Email = email!,
            DisplayName = $"{input.FirstName.Trim()} {input.LastName.Trim()}",
            Role = input.Role,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.UserProfiles.Add(profile);

        try
        {
            await TenancyAudit.SaveAsync(db, audit, new AuditEntry("team.member_added", "user", profile.Id.ToString(), profile.DisplayName, new { email, role = input.Role }), ct);
        }
        catch
        {
            await TryDeleteAsync(keycloak, keycloakId, logger); // do not leave an account behind that the tenant cannot see
            throw;
        }

        return Results.Created($"/api/portal/team/{profile.Id}", new
        {
            profile.Id,
            profile.Email,
            name = profile.DisplayName,
            profile.Role,
            temporaryPassword = password,
            note = "Give this temporary password to the new member. They must choose their own at first sign-in. It is not shown again.",
        });
    }

    private static async Task<IResult> ChangeRole(Guid id, RoleInput input, ClaimsPrincipal user, TenancyDbContext db, IKeycloakAdmin keycloak, IAuditLog audit, CancellationToken ct)
    {
        if (input.Role is null || !Roles.All.Contains(input.Role))
            return Problems.Validation("invalid_role", $"The role must be one of: {string.Join(", ", Roles.All)}.");

        var member = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (member is null)
            return Problems.NotFound("member_not_found", "No such team member.");

        if (member.KeycloakSub == user.UserId())
            return Problems.Conflict("cannot_change_self", "You cannot change your own role. Ask another owner or admin.");
        if (member.Role == input.Role)
            return Results.NoContent();

        var iAmOwner = user.TenantRoles().Contains(Roles.Owner);
        if ((input.Role == Roles.Owner || member.Role == Roles.Owner) && !iAmOwner)
            return Problems.Forbidden("owner_only", "Only an owner can add or change an owner.");
        if (member.Role == Roles.Owner && member.DisabledAt is null && await ActiveOwnersAsync(db, ct) <= 1)
            return Problems.Conflict("last_owner", "This is the last owner. Make someone else an owner first, so the tenant is never left without one.");

        if (await TryKeycloakAsync(() => keycloak.ReplaceTenantRoleAsync(member.KeycloakSub, input.Role, ct)) is { } unavailable)
            return unavailable;

        var previous = member.Role;
        member.Role = input.Role;
        await TenancyAudit.SaveAsync(db, audit, new AuditEntry("team.role_changed", "user", id.ToString(), member.DisplayName, new { from = previous, to = input.Role }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Disable(Guid id, ClaimsPrincipal user, TenancyDbContext db, IKeycloakAdmin keycloak, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var member = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (member is null)
            return Problems.NotFound("member_not_found", "No such team member.");
        if (member.KeycloakSub == user.UserId())
            return Problems.Conflict("cannot_disable_self", "You cannot disable your own account.");
        if (member.DisabledAt is not null) return Results.NoContent();
        if (member.Role == Roles.Owner && !user.TenantRoles().Contains(Roles.Owner))
            return Problems.Forbidden("owner_only", "Only an owner can disable an owner.");
        if (member.Role == Roles.Owner && await ActiveOwnersAsync(db, ct) <= 1)
            return Problems.Conflict("last_owner", "This is the last owner. Make someone else an owner first.");

        if (await TryKeycloakAsync(() => keycloak.SetEnabledAsync(member.KeycloakSub, false, ct)) is { } unavailable)
            return unavailable;

        member.DisabledAt = clock.GetUtcNow().UtcDateTime;
        await TenancyAudit.SaveAsync(db, audit, new AuditEntry("team.member_disabled", "user", id.ToString(), member.DisplayName, new { role = member.Role }), ct);

        return Results.NoContent();
    }

    private static async Task<IResult> Enable(Guid id, TenancyDbContext db, IKeycloakAdmin keycloak, IAuditLog audit, CancellationToken ct)
    {
        var member = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (member is null)
            return Problems.NotFound("member_not_found", "No such team member.");
        if (member.DisabledAt is null)
            return Results.NoContent();

        if (await TryKeycloakAsync(() => keycloak.SetEnabledAsync(member.KeycloakSub, true, ct)) is { } unavailable)
            return unavailable;

        member.DisabledAt = null;
        await TenancyAudit.SaveAsync(db, audit, new AuditEntry("team.member_enabled", "user", id.ToString(), member.DisplayName, new { role = member.Role }), ct);

        return Results.NoContent();
    }

    private static Task<int> ActiveOwnersAsync(TenancyDbContext db, CancellationToken ct) => db.UserProfiles.CountAsync(u => u.Role == Roles.Owner && u.DisabledAt == null, ct);

    /// <summary>Null when Keycloak did the work; otherwise the 503 to return. Nothing is changed in SQL until Keycloak has agreed.</summary>
    private static async Task<IResult?> TryKeycloakAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (KeycloakUnavailableException)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, "identity_unavailable", "Identity service unavailable", "Nothing was changed. Try again in a moment.");
        }
    }

    private static async Task TryDeleteAsync(IKeycloakAdmin keycloak, Guid id, ILogger logger)
    {
        try
        {
            await keycloak.DeleteUserAsync(id, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not remove Keycloak user {UserId} after a failed save", id);
        }
    }

    /// <summary>16 characters with every class the realm's password rule asks for. Temporary: Keycloak forces a new one at first sign-in.</summary>
    internal static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", symbols = "!#$%*+-=?";
        var chars = new List<char>();
        void Pick(string set, int count)
        {
            for (var i = 0; i < count; i++)
                chars.Add(set[RandomNumberGenerator.GetInt32(set.Length)]);
        }
        Pick(upper, 4); Pick(lower, 6); Pick(digits, 4); Pick(symbols, 2);
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }

    private static DateTimeOffset? Utc(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc));
}
