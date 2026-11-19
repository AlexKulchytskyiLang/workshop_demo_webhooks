using Microsoft.Data.SqlClient;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// ASR-11 / principle P6 against the real database: even when the API forgets a WHERE clause or is compromised,
/// SQL returns only the tenant's rows. Each query runs as the application's own SQL user (EXECUTE AS), not as the admin.
/// </summary>
[Collection(PlatformCollection.Name)]
public class RowLevelSecurityTests(PlatformFixture platform)
{
    private static readonly Guid Contoso = Guid.Parse("0198a000-0000-7000-8000-000000000001");
    private static readonly Guid Fabrikam = Guid.Parse("0198a000-0000-7000-8000-000000000002");
    private const string CountProfiles = "SELECT COUNT(*) FROM tenancy.user_profile";

    [Fact]
    public async Task The_api_login_with_no_tenant_context_sees_no_rows_at_all() =>
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", CountProfiles));

    [Fact]
    public async Task The_api_login_sees_exactly_the_five_users_of_its_own_tenant() =>
        Assert.Equal(5, await platform.ScalarAsAsync<int>("api_user", CountProfiles, tenant: Contoso));

    [Fact]
    public async Task A_query_without_any_where_clause_cannot_read_the_other_tenant()
    {
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", $"SELECT COUNT(*) FROM tenancy.user_profile WHERE tenant_id = '{Fabrikam}'", tenant: Contoso));
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", $"SELECT COUNT(*) FROM tenancy.user_profile WHERE email LIKE '%@fabrikam.test'", tenant: Contoso));
    }

    [Fact]
    public async Task The_api_login_can_not_switch_on_system_mode()
    {
        // setting the key is allowed (anyone can write their session context) but the predicate ignores it for non-workers
        Assert.Equal(0, await platform.ScalarAsAsync<int>("api_user", CountProfiles, system: true));
    }

    [Fact]
    public async Task The_worker_login_sees_every_tenant_only_in_explicit_system_mode()
    {
        Assert.True(await platform.ScalarAsAsync<int>("worker_user", CountProfiles, system: true) >= 10);
        Assert.Equal(0, await platform.ScalarAsAsync<int>("worker_user", CountProfiles));
        Assert.Equal(5, await platform.ScalarAsAsync<int>("worker_user", CountProfiles, tenant: Fabrikam));
    }

    [Fact]
    public async Task Writing_a_row_for_another_tenant_is_blocked_by_the_database()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => platform.ScalarAsAsync<int>(
            "api_user",
            $"INSERT tenancy.user_profile (id, tenant_id, keycloak_sub, email, display_name, role) VALUES (NEWID(), '{Fabrikam}', NEWID(), 'x@x.test', 'X', 'viewer'); SELECT 1",
            tenant: Contoso));
        Assert.Equal(33504, ex.Number); // block predicate violation
    }

    [Fact]
    public async Task Updating_another_tenants_rows_changes_nothing()
    {
        var changed = await platform.ScalarAsAsync<int>("api_user",
            $"UPDATE tenancy.user_profile SET display_name = 'hacked' WHERE tenant_id = '{Fabrikam}'; SELECT @@ROWCOUNT", tenant: Contoso);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task The_application_logins_cannot_read_the_security_schema_objects_or_the_migration_journal()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => platform.ScalarAsAsync<int>("api_user", "SELECT COUNT(*) FROM dbo.schema_journal"));
        Assert.Equal(229, ex.Number); // SELECT permission denied
    }
}
