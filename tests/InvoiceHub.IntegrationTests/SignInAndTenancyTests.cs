using System.Net;
using System.Net.Http.Json;

namespace InvoiceHub.IntegrationTests;

/// <summary>M1 exit criteria against the real stack: sign-in through Keycloak, tenant selection, registration.</summary>
[Collection(PlatformCollection.Name)]
public class SignInAndTenancyTests(PlatformFixture platform)
{
    private const string ContosoId = "0198a000-0000-7000-8000-000000000001";

    [Fact]
    public async Task The_platform_reports_ready_and_live()
    {
        Assert.Equal(HttpStatusCode.OK, (await platform.Api.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.Api.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task The_public_tenant_picker_lists_the_seeded_tenants_by_name_and_slug_only()
    {
        var response = await platform.Api.GetAsync("/api/public/tenants");
        response.EnsureSuccessStatusCode();
        var tenants = await platform.ReadJsonAsync(response);

        var slugs = tenants.EnumerateArray().Select(t => t.GetProperty("slug").GetString()).ToList();
        Assert.Contains("contoso", slugs);
        Assert.Contains("fabrikam", slugs);
        Assert.All(tenants.EnumerateArray(), t => Assert.Equal(["name", "slug"], t.EnumerateObject().Select(p => p.Name).Order().ToArray()));
    }

    [Fact]
    public async Task Calls_without_a_token_or_with_a_forged_token_are_unauthorised()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await platform.Api.GetAsync("/api/me")).StatusCode);
        var forged = (await platform.Api.SendAsync(platform.Get("/api/me", token: "eyJhbGciOiJub25lIn0.eyJ0ZW5hbnRfaWQiOiJ4In0.")));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task The_owner_of_contoso_signs_in_and_gets_the_tenant_the_role_and_every_permission()
    {
        var token = await platform.DemoTokenAsync("owner@contoso.test");
        var response = await platform.Api.SendAsync(platform.Get("/api/me", token, selectedTenant: "contoso"));
        response.EnsureSuccessStatusCode();
        var me = await platform.ReadJsonAsync(response);

        Assert.Equal("contoso", me.GetProperty("tenant").GetProperty("slug").GetString());
        Assert.Equal(ContosoId, me.GetProperty("tenant").GetProperty("id").GetString());
        Assert.Equal("owner", me.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal("owner@contoso.test", me.GetProperty("user").GetProperty("email").GetString());
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("secrets:reveal", permissions);
        Assert.Contains("roles:manage", permissions);
    }

    [Fact]
    public async Task A_viewer_gets_read_only_permissions()
    {
        var token = await platform.DemoTokenAsync("viewer@fabrikam.test");
        var me = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/me", token, "fabrikam")));
        Assert.Equal("fabrikam", me.GetProperty("tenant").GetProperty("slug").GetString());
        Assert.Equal(["endpoints:read"], me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    [Fact]
    public async Task Picking_fabrikam_and_signing_in_with_a_contoso_account_is_rejected_and_says_why()
    {
        var token = await platform.DemoTokenAsync("owner@contoso.test");
        var response = await platform.Api.SendAsync(platform.Get("/api/me", token, selectedTenant: "fabrikam"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await platform.ReadJsonAsync(response);
        Assert.Equal("tenant_mismatch", problem.GetProperty("code").GetString());
        Assert.Contains("Contoso Ltd", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_tenant_id_claim_cannot_be_swapped_by_sending_another_header()
    {
        // The header is only a consistency check against the signed claim; it can never select a tenant.
        var token = await platform.DemoTokenAsync("owner@fabrikam.test");
        var me = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/me", token)));
        Assert.Equal("fabrikam", me.GetProperty("tenant").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task A_new_tenant_registers_and_its_owner_signs_in_straight_away()
    {
        var slug = $"it-{Guid.NewGuid():N}"[..14];
        var email = $"boss@{slug}.test";
        var response = await platform.Api.PostAsJsonAsync("/api/public/registrations", new
        {
            tenantName = "Northwind Traders", slug, adminEmail = email, adminFirstName = "Nancy", adminLastName = "Davolio", password = "Str0ng!Passw0rd",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await platform.ReadJsonAsync(response);
        Assert.Equal(slug, created.GetProperty("slug").GetString());

        var tenants = await platform.ReadJsonAsync(await platform.Api.GetAsync("/api/public/tenants"));
        Assert.Contains(slug, tenants.EnumerateArray().Select(t => t.GetProperty("slug").GetString()));

        var token = await platform.TokenAsync(email, "Str0ng!Passw0rd");
        var me = await platform.ReadJsonAsync(await platform.Api.SendAsync(platform.Get("/api/me", token, slug)));
        Assert.Equal(slug, me.GetProperty("tenant").GetProperty("slug").GetString());
        Assert.Equal("owner", me.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(created.GetProperty("tenantId").GetString(), me.GetProperty("tenant").GetProperty("id").GetString());

        // and that user can not enter another tenant
        var other = await platform.Api.SendAsync(platform.Get("/api/me", token, "contoso"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Registering_a_taken_address_conflicts()
    {
        var response = await platform.Api.PostAsJsonAsync("/api/public/registrations", new
        {
            tenantName = "Another Contoso", slug = "contoso", adminEmail = "someone@elsewhere.test", adminFirstName = "A", adminLastName = "B", password = "Str0ng!Passw0rd",
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("slug_taken", (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Registering_with_a_taken_email_conflicts_and_leaves_no_half_created_tenant_behind()
    {
        var slug = $"it-{Guid.NewGuid():N}"[..14];
        var response = await platform.Api.PostAsJsonAsync("/api/public/registrations", new
        {
            tenantName = "Email Clash", slug, adminEmail = "owner@contoso.test", adminFirstName = "A", adminLastName = "B", password = "Str0ng!Passw0rd",
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_taken", (await platform.ReadJsonAsync(response)).GetProperty("code").GetString());

        // the reservation was rolled back: the address is free again and the tenant is not listed
        var tenants = await platform.ReadJsonAsync(await platform.Api.GetAsync("/api/public/tenants"));
        Assert.DoesNotContain(slug, tenants.EnumerateArray().Select(t => t.GetProperty("slug").GetString()));
        var retry = await platform.Api.PostAsJsonAsync("/api/public/registrations", new
        {
            tenantName = "Email Clash", slug, adminEmail = $"fresh@{slug}.test", adminFirstName = "A", adminLastName = "B", password = "Str0ng!Passw0rd",
        });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task An_invalid_registration_reports_every_field_problem_at_once()
    {
        var response = await platform.Api.PostAsJsonAsync("/api/public/registrations", new
        {
            tenantName = "", slug = "x", adminEmail = "nope", adminFirstName = "", adminLastName = "", password = "weak",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await platform.ReadJsonAsync(response);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        Assert.Equal(["adminEmail", "adminFirstName", "adminLastName", "password", "slug", "tenantName"],
            body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray());
    }
}
