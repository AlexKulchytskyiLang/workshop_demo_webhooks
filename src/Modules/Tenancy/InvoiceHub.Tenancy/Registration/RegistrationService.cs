using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Keycloak;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.Tenancy.Registration;

internal abstract record RegistrationOutcome
{
    public sealed record Created(RegistrationResponse Response) : RegistrationOutcome;
    public sealed record Invalid(Dictionary<string, string[]> Errors) : RegistrationOutcome;
    public sealed record SlugTaken : RegistrationOutcome;
    public sealed record EmailTaken : RegistrationOutcome;
    public sealed record Unavailable : RegistrationOutcome;
}

/// <summary>
/// Tenant registration as a small saga with compensation, because it spans two systems (SQL and Keycloak) that cannot share a transaction:
/// 1. reserve the tenant row (pending), 2. create the owner in Keycloak and give the role, 3. activate the tenant and store the profile.
/// Any failure after step 1 undoes what was done, so a half-registered tenant never stays visible or blocks the slug.
/// </summary>
internal sealed class RegistrationService(
    TenancyDbContext db,
    IKeycloakAdmin keycloak,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<RegistrationService> logger)
{
    public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, CancellationToken ct)
    {
        var errors = RegistrationRules.Validate(request);
        if (errors.Count > 0) return new RegistrationOutcome.Invalid(errors);

        var tenantId = Guid.CreateVersion7();
        var slug = RegistrationRules.NormalizeSlug(request.Slug);
        var name = request.TenantName!.Trim();
        var email = request.AdminEmail!.Trim().ToLowerInvariant();
        var first = request.AdminFirstName!.Trim();
        var last = request.AdminLastName!.Trim();

        // 1. reserve
        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = slug, Name = name, Status = TenantStatus.Pending, CreatedAt = clock.GetUtcNow().UtcDateTime });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return new RegistrationOutcome.SlugTaken();
        }

        // 2. identity
        Guid? keycloakUser = null;
        try
        {
            keycloakUser = await keycloak.CreateUserAsync(new NewKeycloakUser(email, first, last, request.Password!, tenantId, slug), ct);
            await keycloak.AssignRealmRoleAsync(keycloakUser.Value, Roles.Owner, ct);

            // 3. activate + profile (own scope so the connection carries the new tenant's context)
            await using (var scope = scopes.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantScope>().SetTenant(tenantId);
                var tenantDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
                tenantDb.UserProfiles.Add(new UserProfileEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    KeycloakSub = keycloakUser.Value,
                    Email = email,
                    DisplayName = $"{first} {last}",
                    Role = Roles.Owner,
                    CreatedAt = clock.GetUtcNow().UtcDateTime,
                });
                await tenantDb.SaveChangesAsync(ct);
                await tenantDb.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TenantStatus.Active), ct);
            }

            logger.LogInformation("Registered tenant {Slug} ({TenantId})", slug, tenantId);
            return new RegistrationOutcome.Created(new RegistrationResponse(tenantId, slug, name));
        }
        catch (KeycloakConflictException)
        {
            await CompensateAsync(tenantId, keycloakUser);
            return new RegistrationOutcome.EmailTaken();
        }
        catch (Exception e) when (e is KeycloakUnavailableException or DbUpdateException or InvalidOperationException)
        {
            logger.LogError(e, "Registration of {Slug} failed; compensating", slug);
            await CompensateAsync(tenantId, keycloakUser);
            return new RegistrationOutcome.Unavailable();
        }
    }

    private async Task CompensateAsync(Guid tenantId, Guid? keycloakUser)
    {
        // Compensation must not be cancelled by the caller giving up, and must not throw over the original problem.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            if (keycloakUser is not null) await keycloak.DeleteUserAsync(keycloakUser.Value, cts.Token);
        }
        catch (Exception e) { logger.LogError(e, "Could not delete Keycloak user {UserId} while compensating tenant {TenantId}", keycloakUser, tenantId); }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantScope>().SetTenant(tenantId);
            var tenantDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            await tenantDb.UserProfiles.Where(u => u.TenantId == tenantId).ExecuteDeleteAsync(cts.Token);
            await tenantDb.Tenants.Where(t => t.Id == tenantId && t.Status == TenantStatus.Pending).ExecuteDeleteAsync(cts.Token);
        }
        catch (Exception e) { logger.LogError(e, "Could not remove the pending tenant {TenantId}", tenantId); }
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
