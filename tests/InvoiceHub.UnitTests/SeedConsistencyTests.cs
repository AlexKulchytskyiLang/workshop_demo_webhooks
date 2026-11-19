using System.Text.Json;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Tenancy.Seeding;

namespace InvoiceHub.UnitTests;

/// <summary>The database seed and the Keycloak realm are generated from one file. These tests fail if someone edits one of them by hand.</summary>
public class SeedConsistencyTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InvoiceHub.slnx"))) dir = dir.Parent;
        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("InvoiceHub.slnx not found above the test output."), .. parts]);
    }

    private static JsonElement Realm() => JsonDocument.Parse(File.ReadAllText(RepoFile("infra", "keycloak", "realms", "invoicehub-realm.json"))).RootElement;

    [Fact]
    public void Every_seeded_user_exists_in_the_realm_with_the_same_id_tenant_and_role()
    {
        var seed = SeedData.Load();
        var users = Realm().GetProperty("users").EnumerateArray().ToDictionary(u => u.TryGetProperty("email", out var e) ? e.GetString()! : u.GetProperty("username").GetString()!, u => u);

        foreach (var s in seed.Users)
        {
            Assert.True(users.TryGetValue(s.Email, out var u), $"{s.Email} is missing from the realm. Run infra/keycloak/generate_realm.py.");
            Assert.Equal(s.Id.ToString(), u.GetProperty("id").GetString());
            var tenant = seed.Tenants.Single(t => t.Slug == s.Tenant);
            Assert.Equal(tenant.Id.ToString(), u.GetProperty("attributes").GetProperty("tenant_id")[0].GetString());
            Assert.Equal(tenant.Slug, u.GetProperty("attributes").GetProperty("tenant_slug")[0].GetString());
            Assert.Equal(s.Role, u.GetProperty("realmRoles")[0].GetString());
        }
    }

    [Fact]
    public void The_realm_defines_exactly_the_application_roles() =>
        Assert.Equal(Roles.All.Order().ToArray(), Realm().GetProperty("roles").GetProperty("realm").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).Order().ToArray());

    [Fact]
    public void The_realm_declares_the_tenant_attributes_in_the_user_profile()
    {
        // Spike 0: Keycloak silently drops attributes that the user profile does not declare.
        var config = Realm().GetProperty("components").GetProperty("org.keycloak.userprofile.UserProfileProvider")[0]
            .GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!;
        var names = JsonDocument.Parse(config).RootElement.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("tenant_id", names);
        Assert.Contains("tenant_slug", names);
    }

    [Fact]
    public void The_portal_client_is_public_with_pkce_and_no_password_grant()
    {
        var portal = Realm().GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == "portal");
        Assert.True(portal.GetProperty("publicClient").GetBoolean());
        Assert.True(portal.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(portal.GetProperty("directAccessGrantsEnabled").GetBoolean());
        Assert.False(portal.GetProperty("implicitFlowEnabled").GetBoolean());
        Assert.Equal("S256", portal.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
    }

    [Fact]
    public void Tokens_carry_the_tenant_the_roles_and_the_api_audience()
    {
        var portal = Realm().GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == "portal");
        var mappers = portal.GetProperty("protocolMappers").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("tenant_id", mappers);
        Assert.Contains("roles", mappers);
        Assert.Contains("api-audience", mappers);
    }

    [Fact]
    public void Seeded_ids_are_unique()
    {
        var seed = SeedData.Load();
        Assert.Equal(seed.Users.Count, seed.Users.Select(u => u.Id).Distinct().Count());
        Assert.Equal(seed.Users.Count, seed.Users.Select(u => u.Email).Distinct().Count());
    }
}
