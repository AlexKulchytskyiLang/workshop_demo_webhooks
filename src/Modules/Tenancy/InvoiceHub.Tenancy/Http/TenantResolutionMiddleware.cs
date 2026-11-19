using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.Tenancy.Http;

/// <summary>
/// Turns the validated token into the request's tenant (principle P6). The tenant comes only from the token's signed
/// <c>tenant_id</c> claim. The optional <c>X-Selected-Tenant</c> header carries what the user picked on the portal's tenant screen:
/// if it names another tenant than the account belongs to, the request is rejected, so "pick Fabrikam, sign in as Contoso" never works.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string SelectedTenantHeader = "X-Selected-Tenant";
    public const string TenantItemKey = "invoicehub.tenant";

    public async Task InvokeAsync(HttpContext context, ITenantDirectory directory, ITenantScope scope)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantId = context.User.TenantId();
            if (tenantId is null)
            {
                await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "no_tenant", "No tenant", "This account does not belong to a tenant.");
                return;
            }

            var tenant = await directory.FindByIdAsync(tenantId.Value, context.RequestAborted);
            if (tenant is null || tenant.Status != TenantStatus.Active)
            {
                await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "tenant_inactive", "Tenant unavailable", "The tenant of this account is not active.");
                return;
            }

            if (context.Request.Headers.TryGetValue(SelectedTenantHeader, out var selected) &&
                !string.Equals(selected.ToString(), tenant.Slug, StringComparison.OrdinalIgnoreCase))
            {
                await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "tenant_mismatch", "Wrong tenant",
                                          $"You selected '{selected}', but this account belongs to '{tenant.Name}'.");
                return;
            }

            scope.SetTenant(tenantId.Value);
            // a disabled member's token stays valid until it expires; this makes "disable" take effect within seconds instead
            if (context.User.UserId() is { } sub && await context.RequestServices.GetRequiredService<IDisabledUsers>().IsDisabledAsync(sub, context.RequestAborted))
            {
                await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "account_disabled", "Account disabled", "This account was disabled by an administrator of your organisation.");
                return;
            }
            context.Items[TenantItemKey] = tenant;
        }

        await next(context);
    }
}

public static class TenantResolutionExtensions
{
    /// <summary>Place after UseAuthentication and before UseAuthorization.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) => app.UseMiddleware<TenantResolutionMiddleware>();
}
