using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace InvoiceHub.Tenancy.Seeding;

public sealed record SeedTenant(Guid Id, string Slug, string Name);

public sealed record SeedUser(Guid Id, string Email, string FirstName, string LastName, string Tenant, string Role);

/// <summary>A fixed, DEMO-ONLY API key, so the demo receivers can be configured from the same file the database is seeded from. Real keys are created in the portal and shown once.</summary>
public sealed record SeedApiKey(string Tenant, string Name, string Key, IReadOnlyList<string> Scopes);

public sealed record SeedData(string DemoPassword, IReadOnlyList<SeedTenant> Tenants, IReadOnlyList<SeedUser> Users, IReadOnlyList<SeedApiKey>? ApiKeys = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The demo tenants and users. The same file (infra/seed/seed.json) generates the Keycloak realm, so the two cannot drift.</summary>
    public static SeedData Load()
    {
        using var stream = typeof(SeedData).Assembly.GetManifestResourceStream("InvoiceHub.Tenancy.Seed.seed.json")
            ?? throw new InvalidOperationException("seed.json is not embedded.");
        return JsonSerializer.Deserialize<SeedData>(stream, Json)!;
    }
}

/// <summary>Seeds the demo tenants and the profiles of their Keycloak users. Idempotent. Runs with the admin login, so it sets the tenant context itself.</summary>
public static class TenancySeeder
{
    public static async Task ApplyAsync(string adminConnectionString, ILogger logger, CancellationToken ct = default)
    {
        var seed = SeedData.Load();
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(ct);

        foreach (var tenant in seed.Tenants)
        {
            await using var command = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM tenancy.tenant WHERE id = @id)
                  INSERT tenancy.tenant (id, slug, name, status) VALUES (@id, @slug, @name, 'active');
                """, connection);
            command.Parameters.AddWithValue("@id", tenant.Id);
            command.Parameters.AddWithValue("@slug", tenant.Slug);
            command.Parameters.AddWithValue("@name", tenant.Name);
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var user in seed.Users)
        {
            var tenant = seed.Tenants.Single(t => t.Slug == user.Tenant);
            await using var command = new SqlCommand(
                """
                EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;
                IF NOT EXISTS (SELECT 1 FROM tenancy.user_profile WHERE keycloak_sub = @sub)
                  INSERT tenancy.user_profile (id, tenant_id, keycloak_sub, email, display_name, role)
                  VALUES (@id, @tenant, @sub, @email, @name, @role);
                """, connection);
            command.Parameters.AddWithValue("@tenant", tenant.Id);
            command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("@sub", user.Id);
            command.Parameters.AddWithValue("@email", user.Email);
            command.Parameters.AddWithValue("@name", $"{user.FirstName} {user.LastName}");
            command.Parameters.AddWithValue("@role", user.Role);
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var key in seed.ApiKeys ?? [])
        {
            var tenant = seed.Tenants.Single(t => t.Slug == key.Tenant);
            var parts = key.Key.Split('_', 4); // ihk _ tenant _ key id _ secret
            var keyId = Guid.ParseExact(parts[2], "N");
            await using var command = new SqlCommand(
                """
                EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;
                IF NOT EXISTS (SELECT 1 FROM tenancy.api_key WHERE id = @id)
                  INSERT tenancy.api_key (id, tenant_id, name, prefix, key_hash, scopes, created_by)
                  VALUES (@id, @tenant, @name, @prefix, @hash, @scopes, 'seed');
                """, connection);
            command.Parameters.AddWithValue("@tenant", tenant.Id);
            command.Parameters.AddWithValue("@id", keyId);
            command.Parameters.AddWithValue("@name", key.Name);
            command.Parameters.AddWithValue("@prefix", $"ihk_{keyId:N}"[..12]);
            command.Parameters.Add("@hash", System.Data.SqlDbType.Binary, 32).Value = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(parts[3]));
            command.Parameters.AddWithValue("@scopes", string.Join(',', key.Scopes));
            await command.ExecuteNonQueryAsync(ct);
        }

        logger.LogInformation("Seeded {Tenants} tenants, {Users} user profiles and {Keys} demo API keys", seed.Tenants.Count, seed.Users.Count, seed.ApiKeys?.Count ?? 0);
    }
}
