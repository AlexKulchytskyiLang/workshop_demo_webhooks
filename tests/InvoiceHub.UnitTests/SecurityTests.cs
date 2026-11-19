using System.Security.Claims;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;

namespace InvoiceHub.UnitTests;

public class RolePermissionsTests
{
    [Fact]
    public void Owner_and_admin_hold_every_permission()
    {
        Assert.Equal(Permissions.All.Order().ToArray(), RolePermissions.For([Roles.Owner]));
        Assert.Equal(Permissions.All.Order().ToArray(), RolePermissions.For([Roles.Admin]));
    }

    [Fact]
    public void Viewer_can_only_read_endpoints() =>
        Assert.Equal([Permissions.EndpointsRead], RolePermissions.For([Roles.Viewer]));

    [Fact]
    public void Support_sees_payloads_and_replays_single_events_but_never_secrets()
    {
        var p = RolePermissions.For([Roles.Support]);
        Assert.Contains(Permissions.PayloadsView, p);
        Assert.Contains(Permissions.ReplaySingle, p);
        Assert.DoesNotContain(Permissions.SecretsReveal, p);
        Assert.DoesNotContain(Permissions.ReplayRun, p);
        Assert.DoesNotContain(Permissions.EndpointsWrite, p);
    }

    [Fact]
    public void Developer_manages_endpoints_and_secrets_but_cannot_delete_or_manage_roles()
    {
        var p = RolePermissions.For([Roles.Developer]);
        Assert.Contains(Permissions.EndpointsWrite, p);
        Assert.Contains(Permissions.SecretsReveal, p);
        Assert.DoesNotContain(Permissions.EndpointsDelete, p);
        Assert.DoesNotContain(Permissions.RolesManage, p);
        Assert.DoesNotContain(Permissions.AuditView, p);
    }

    [Fact]
    public void Unknown_roles_grant_nothing() =>
        Assert.Empty(RolePermissions.For(["offline_access", "uma_authorization", "default-roles-invoicehub"]));

    [Fact]
    public void The_primary_role_is_the_most_powerful_one() =>
        Assert.Equal(Roles.Admin, RolePermissions.Primary([Roles.Viewer, Roles.Admin, Roles.Support]));

    [Fact]
    public void Keycloaks_default_roles_are_not_tenant_roles()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("roles", "offline_access"), new Claim("roles", Roles.Support), new Claim("roles", "default-roles-invoicehub")], "test"));
        Assert.Equal([Roles.Support], user.TenantRoles());
        Assert.True(user.HasPermission(Permissions.PayloadsView));
        Assert.False(user.HasPermission(Permissions.SecretsReveal));
    }

    [Fact]
    public void The_tenant_comes_from_the_signed_claim_only()
    {
        var id = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Auth.TenantClaim, id.ToString())], "test"));
        Assert.Equal(id, user.TenantId());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity([new Claim(Auth.TenantClaim, "not-a-guid")], "test")).TenantId());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity("test")).TenantId());
    }
}

public class TenantContextTests
{
    [Fact]
    public void A_new_context_has_no_tenant_and_is_not_system()
    {
        var ctx = new TenantContext();
        Assert.Null(ctx.TenantId);
        Assert.False(ctx.IsSystem);
    }

    [Fact]
    public void Use_switches_tenant_and_restores_the_previous_one()
    {
        var ctx = new TenantContext();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        ctx.SetTenant(a);
        using (ctx.Use(b)) Assert.Equal(b, ctx.TenantId);
        Assert.Equal(a, ctx.TenantId);
    }

    [Fact]
    public void System_mode_clears_the_tenant_and_a_tenant_clears_system_mode()
    {
        var ctx = new TenantContext();
        ctx.SetTenant(Guid.NewGuid());
        ctx.SetSystem();
        Assert.True(ctx.IsSystem);
        Assert.Null(ctx.TenantId);
        ctx.SetTenant(Guid.NewGuid());
        Assert.False(ctx.IsSystem);
    }

    [Fact]
    public void Use_restores_system_mode_too()
    {
        var ctx = new TenantContext();
        ctx.SetSystem();
        using (ctx.Use(Guid.NewGuid())) Assert.False(ctx.IsSystem);
        Assert.True(ctx.IsSystem);
    }
}
