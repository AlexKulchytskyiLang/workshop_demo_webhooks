using System.Security.Claims;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.UnitTests;

public class TenantResolutionMiddlewareTests
{
    private static readonly TenantInfo Contoso = new(Guid.Parse("0198a000-0000-7000-8000-000000000001"), "contoso", "Contoso Ltd", TenantStatus.Active);

    private sealed class FakeDirectory(params TenantInfo[] tenants) : ITenantDirectory
    {
        public Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(tenants.FirstOrDefault(t => t.Id == id));
        public Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken ct = default) => Task.FromResult(tenants.FirstOrDefault(t => t.Slug == slug));
        public Task<IReadOnlyList<TenantSummary>> ListActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantSummary>>(tenants.Select(t => new TenantSummary(t.Slug, t.Name)).ToList());
    }

    private static async Task<(DefaultHttpContext Context, TenantContext Scope, bool NextCalled)> RunAsync(
        ClaimsPrincipal user, string? selectedTenant = null, params TenantInfo[] directory)
    {
        var context = new DefaultHttpContext { User = user, RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        if (selectedTenant is not null) context.Request.Headers[TenantResolutionMiddleware.SelectedTenantHeader] = selectedTenant;
        var scope = new TenantContext();
        var called = false;
        var middleware = new TenantResolutionMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new FakeDirectory(directory), scope);
        return (context, scope, called);
    }

    private static ClaimsPrincipal SignedIn(Guid? tenant) => new(new ClaimsIdentity(
        tenant is null ? [] : [new Claim(Auth.TenantClaim, tenant.Value.ToString())], authenticationType: "test"));

    private static async Task<string> BodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    [Fact]
    public async Task An_anonymous_request_passes_through_without_a_tenant()
    {
        var (_, scope, called) = await RunAsync(new ClaimsPrincipal(new ClaimsIdentity()), directory: Contoso);
        Assert.True(called);
        Assert.Null(scope.TenantId);
    }

    [Fact]
    public async Task A_signed_in_user_gets_the_tenant_of_the_token()
    {
        var (context, scope, called) = await RunAsync(SignedIn(Contoso.Id), "contoso", Contoso);
        Assert.True(called);
        Assert.Equal(Contoso.Id, scope.TenantId);
        Assert.Same(Contoso, context.Items[TenantResolutionMiddleware.TenantItemKey]);
    }

    [Fact]
    public async Task The_selected_tenant_check_ignores_case()
    {
        var (_, _, called) = await RunAsync(SignedIn(Contoso.Id), "CONTOSO", Contoso);
        Assert.True(called);
    }

    [Fact]
    public async Task Picking_another_tenant_than_the_account_belongs_to_is_forbidden_and_names_both()
    {
        var (context, scope, called) = await RunAsync(SignedIn(Contoso.Id), "fabrikam", Contoso);
        Assert.False(called);
        Assert.Null(scope.TenantId);
        Assert.Equal(403, context.Response.StatusCode);
        var body = await BodyAsync(context);
        Assert.Contains("tenant_mismatch", body);
        Assert.Contains("fabrikam", body);
        Assert.Contains("Contoso Ltd", body);
    }

    [Fact]
    public async Task A_token_without_a_tenant_claim_is_forbidden()
    {
        var (context, _, called) = await RunAsync(SignedIn(null), directory: Contoso);
        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Contains("no_tenant", await BodyAsync(context));
    }

    [Fact]
    public async Task A_token_for_an_unknown_tenant_is_forbidden()
    {
        var (context, _, called) = await RunAsync(SignedIn(Guid.NewGuid()), directory: Contoso);
        Assert.False(called);
        Assert.Contains("tenant_inactive", await BodyAsync(context));
    }

    [Fact]
    public async Task A_suspended_tenant_is_locked_out()
    {
        var suspended = Contoso with { Status = TenantStatus.Suspended };
        var (context, _, called) = await RunAsync(SignedIn(suspended.Id), directory: suspended);
        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
    }
}
