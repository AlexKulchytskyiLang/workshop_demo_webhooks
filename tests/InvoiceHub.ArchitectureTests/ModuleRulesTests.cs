using System.Reflection;
using System.Text.RegularExpressions;
using InvoiceHub.Audit;
using InvoiceHub.Billing;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.Compliance;
using InvoiceHub.Events;
using InvoiceHub.Jobs;
using InvoiceHub.Notifications;
using InvoiceHub.Tenancy;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Webhooks;
using NetArchTest.Rules;

namespace InvoiceHub.ArchitectureTests;

/// <summary>
/// The rules that keep the modular monolith modular. A module is an assembly named InvoiceHub.&lt;Module&gt; with a sibling
/// InvoiceHub.&lt;Module&gt;.Contracts. New modules are picked up as soon as they are listed in <see cref="Modules"/>.
/// </summary>
public partial class ModuleRulesTests
{
    private static readonly Assembly BuildingBlocks = typeof(Db).Assembly;

    /// <summary>All module implementation assemblies.</summary>
    private static IReadOnlyList<Assembly> Modules() =>
    [
        typeof(TenancyModule).Assembly,
        typeof(EventsModule).Assembly,
        typeof(WebhooksModule).Assembly,
        typeof(BillingModule).Assembly,
        typeof(AuditModule).Assembly,
        typeof(NotificationsModule).Assembly,
        typeof(JobsModule).Assembly,
        typeof(ComplianceModule).Assembly,
    ];

    /// <summary>Contracts assemblies: the only thing another module may reference.</summary>
    private static IReadOnlyList<Assembly> ContractsAssemblies() =>
    [
        typeof(ITenantDirectory).Assembly,
        typeof(InvoiceHub.Events.Contracts.IEventPublisher).Assembly,
        typeof(InvoiceHub.Webhooks.Contracts.DeliveryStatus).Assembly,
        typeof(InvoiceHub.Billing.Contracts.IInvoiceCommands).Assembly,
        typeof(InvoiceHub.Audit.Contracts.IAuditLog).Assembly,
        typeof(InvoiceHub.Jobs.Contracts.IJobCommands).Assembly,
        typeof(InvoiceHub.Compliance.Contracts.IKycCommands).Assembly,
    ];

    private static Assembly Host(string name) => Assembly.Load(name);

    private static string NameOf(Assembly a) => a.GetName().Name!;

    private static IEnumerable<string> InvoiceHubReferences(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("InvoiceHub.", StringComparison.Ordinal));

    [Fact]
    public void Positive_control_the_checker_can_see_real_dependencies()
    {
        // The Tenancy module really does use the shared kernel. A "must not depend" rule for it has to FAIL; if it passed,
        // the checker would be blind and every other rule in this file would pass vacuously.
        var result = Types.InAssembly(typeof(TenancyModule).Assembly).ShouldNot().HaveDependencyOnAny("InvoiceHub.BuildingBlocks").GetResult();
        Assert.False(result.IsSuccessful);

        // and the assembly-reference view used below sees them too
        Assert.Contains("InvoiceHub.BuildingBlocks", InvoiceHubReferences(typeof(BillingModule).Assembly));
        Assert.Contains("InvoiceHub.Events.Contracts", InvoiceHubReferences(typeof(BillingModule).Assembly));
    }

    [Fact]
    public void A_module_never_references_another_modules_implementation()
    {
        var implementations = Modules().Select(NameOf).ToHashSet();
        foreach (var module in Modules())
        {
            // exact assembly names: InvoiceHub.Events.Contracts is allowed, InvoiceHub.Events is not
            var offending = InvoiceHubReferences(module).Where(r => implementations.Contains(r) && r != NameOf(module)).ToList();
            Assert.True(offending.Count == 0, $"{NameOf(module)} references the implementation of {string.Join(", ", offending)}. Use the .Contracts assembly.");
        }
    }

    [Fact]
    public void A_module_reaches_the_rest_of_the_system_only_through_the_shared_kernel_and_contracts()
    {
        var allowed = ContractsAssemblies().Select(NameOf).Append("InvoiceHub.BuildingBlocks").ToHashSet();
        foreach (var module in Modules())
        {
            var own = NameOf(module); // the module's own Core / Contracts assemblies share its name prefix
            var offending = InvoiceHubReferences(module).Where(r => r != own && !r.StartsWith(own + ".", StringComparison.Ordinal) && !allowed.Contains(r)).ToList();
            Assert.True(offending.Count == 0, $"{own} depends on {string.Join(", ", offending)}.");
        }
    }

    [Fact]
    public void Contracts_assemblies_are_plain_and_do_not_pull_in_infrastructure()
    {
        foreach (var contracts in ContractsAssemblies())
        {
            var result = Types.InAssembly(contracts)
                .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Microsoft.Data.SqlClient", "Azure.")
                .GetResult();
            Assert.True(result.IsSuccessful, $"{NameOf(contracts)} depends on infrastructure: {Names(result)}");
            Assert.DoesNotContain(InvoiceHubReferences(contracts), r => Modules().Select(NameOf).Contains(r));
        }
    }

    [Theory]
    [InlineData(typeof(TenancyModule), @"^InvoiceHub\.Tenancy\.(Persistence|Keycloak|Directory)")]
    [InlineData(typeof(EventsModule), @"^InvoiceHub\.Events\.(Persistence|Catalog)")]
    [InlineData(typeof(WebhooksModule), @"^InvoiceHub\.Webhooks\.(Persistence|Sending|Secrets|Delivery|Api)")]
    public void Module_internals_stay_internal(Type moduleType, string internalNamespaces)
    {
        // Persistence, transport and handler plumbing are implementation details. Option classes are public because configuration binds to them.
        var leaked = Types.InAssembly(moduleType.Assembly)
            .That().ResideInNamespaceMatching(internalNamespaces)
            .And().DoNotHaveNameEndingWith("Options")
            .Should().NotBePublic().GetResult();
        Assert.True(leaked.IsSuccessful, $"Public implementation types: {Names(leaked)}");
    }

    [Fact]
    public void The_billing_module_exposes_nothing_but_its_entry_point()
    {
        var publicTypes = typeof(BillingModule).Assembly.GetExportedTypes().Select(t => t.Name).ToList();
        Assert.Equal(["BillingModule"], publicTypes);
    }

    [Theory]
    [InlineData(typeof(AuditModule), "AuditModule")]
    [InlineData(typeof(NotificationsModule), "NotificationsModule")]
    [InlineData(typeof(JobsModule), "JobsModule")]
    [InlineData(typeof(ComplianceModule), "ComplianceModule")]
    public void Small_modules_expose_nothing_but_their_entry_point(Type module, string entryPoint)
    {
        Assert.Equal([entryPoint], module.Assembly.GetExportedTypes().Select(t => t.Name).ToList());
    }

    [Fact]
    public void Hosts_are_composition_roots_and_hold_no_data_access()
    {
        foreach (var host in new[] { typeof(InvoiceHub.Api.SecurityHeadersMiddleware).Assembly, Host("InvoiceHub.Delivery"), Host("InvoiceHub.Simulator") })
        {
            var result = Types.InAssembly(host)
                .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "InvoiceHub.Tenancy.Persistence", "InvoiceHub.Events.Persistence", "InvoiceHub.Webhooks.Persistence")
                .GetResult();
            Assert.True(result.IsSuccessful, $"{NameOf(host)} touches data access directly: {Names(result)}");
        }
    }

    [Fact]
    public void The_shared_kernel_knows_no_module()
    {
        var offending = InvoiceHubReferences(BuildingBlocks).Where(r => r != NameOf(BuildingBlocks)).ToList();
        Assert.True(offending.Count == 0, $"BuildingBlocks depends on {string.Join(", ", offending)}");
    }

    [Fact]
    public void The_message_broker_adapter_knows_only_the_shared_kernel()
    {
        // ASR-16: the broker is replaceable. The adapter implements BuildingBlocks ports and must not know any module.
        var adapter = Host("InvoiceHub.Messaging.ServiceBus");
        var offending = InvoiceHubReferences(adapter).Where(r => r != "InvoiceHub.BuildingBlocks").ToList();
        Assert.True(offending.Count == 0, $"The Service Bus adapter depends on {string.Join(", ", offending)}");
    }

    [Fact]
    public void The_blob_storage_adapter_knows_only_the_shared_kernel()
    {
        // ASR-18 / ASR-16: the storage service is replaceable behind IClaimCheckStore; the adapter implements a BuildingBlocks port and knows no module
        var adapter = Host("InvoiceHub.Storage.AzureBlob");
        var offending = InvoiceHubReferences(adapter).Where(r => r != "InvoiceHub.BuildingBlocks").ToList();
        Assert.True(offending.Count == 0, $"The blob adapter depends on {string.Join(", ", offending)}");
    }

    [Fact]
    public void No_module_talks_to_the_cloud_storage_sdk_directly()
    {
        // modules use the claim-check port; only the adapter references Azure.Storage
        foreach (var module in Modules())
            Assert.DoesNotContain(module.GetReferencedAssemblies().Select(r => r.Name!), r => r.StartsWith("Azure.Storage", StringComparison.Ordinal));
    }

    [Fact]
    public void Webhooks_core_is_pure_logic_with_no_framework_or_infrastructure()
    {
        // signing, retry, SSRF, circuit breaker, filters: testable with a fake clock because they touch nothing but the base class library
        var core = typeof(InvoiceHub.Webhooks.Core.WebhookSigning).Assembly;
        var offending = core.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n is not ("netstandard" or "mscorlib")).ToList();
        Assert.True(offending.Count == 0, $"Webhooks.Core depends on {string.Join(", ", offending)}");
    }

    [Fact]
    public void The_receiver_is_an_independent_application()
    {
        // it is the tenant's system: it may use the public signing helper (as a customer uses the open-source library) and nothing else of the platform
        var receiver = Host("InvoiceHub.Receiver.Api");
        var offending = InvoiceHubReferences(receiver).Where(r => r is not ("InvoiceHub.Webhooks.Core" or "InvoiceHub.ServiceDefaults")).ToList();
        Assert.True(offending.Count == 0, $"The receiver depends on platform internals: {string.Join(", ", offending)}");
    }

    private static string Names(NetArchTest.Rules.TestResult result) => string.Join(", ", (result.FailingTypes ?? []).Select(t => t.FullName));
}

/// <summary>Static checks over the embedded SQL scripts: naming, journalling, schema ownership and the tenant isolation convention (principle P6).</summary>
public partial class SqlScriptRulesTests
{
    private static IEnumerable<(string Assembly, string Name, string Sql)> Scripts()
    {
        foreach (var assembly in new[] { typeof(Db).Assembly, typeof(TenancyModule).Assembly, typeof(EventsModule).Assembly, typeof(WebhooksModule).Assembly, typeof(BillingModule).Assembly, typeof(AuditModule).Assembly, typeof(NotificationsModule).Assembly, typeof(JobsModule).Assembly, typeof(ComplianceModule).Assembly })
        {
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                yield return (assembly.GetName().Name!, resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..], reader.ReadToEnd());
            }
        }
    }

    [GeneratedRegex(@"^\d{3}_[a-z0-9_]+\.sql$")]
    private static partial Regex ScriptName();

    [GeneratedRegex(@"CREATE\s+TABLE\s+(?<table>[\w\.\[\]]+)\s*\((?<body>.*?)\n\);", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    [Fact]
    public void Scripts_exist_and_follow_the_numbering_convention()
    {
        var scripts = Scripts().ToList();
        Assert.NotEmpty(scripts);
        foreach (var s in scripts) Assert.True(ScriptName().IsMatch(s.Name), $"{s.Assembly}/{s.Name} must be named NNN_snake_case.sql");
        foreach (var group in scripts.GroupBy(s => s.Assembly))
            Assert.Equal(group.Select(g => g.Name).Order(StringComparer.Ordinal), group.Select(g => g.Name));
    }

    [Fact]
    public void Every_module_has_scripts()
    {
        var assemblies = Scripts().Select(s => s.Assembly).Distinct().ToList();
        foreach (var expected in new[] { "InvoiceHub.BuildingBlocks", "InvoiceHub.Tenancy", "InvoiceHub.Events", "InvoiceHub.Webhooks", "InvoiceHub.Billing", "InvoiceHub.Audit", "InvoiceHub.Notifications", "InvoiceHub.Jobs", "InvoiceHub.Compliance" })
            Assert.Contains(expected, assemblies);
    }

    [Fact]
    public void Every_table_with_a_tenant_id_is_covered_by_the_row_level_security_policy()
    {
        var all = string.Join("\n", Scripts().Select(s => s.Sql));
        var tenantTables = Scripts()
            .SelectMany(s => CreateTable().Matches(s.Sql))
            .Where(m => Regex.IsMatch(m.Groups["body"].Value, @"\btenant_id\b", RegexOptions.IgnoreCase))
            .Select(m => m.Groups["table"].Value.Replace("[", "").Replace("]", "").ToLowerInvariant())
            .ToList();

        Assert.NotEmpty(tenantTables); // the convention must have something to check
        Assert.Contains("webhooks.delivery", tenantTables); // and it must see the tables that matter most
        Assert.Contains("events.outbox", tenantTables);
        Assert.Contains("billing.invoice", tenantTables);
        foreach (var table in tenantTables)
        {
            Assert.True(
                Regex.IsMatch(all, $@"ADD\s+FILTER\s+PREDICATE\s+security\.fn_tenant_predicate\(tenant_id\)\s+ON\s+{Regex.Escape(table)}\b", RegexOptions.IgnoreCase),
                $"{table} has a tenant_id column but no FILTER PREDICATE of security.fn_tenant_predicate.");
            Assert.True(
                Regex.IsMatch(all, $@"ADD\s+BLOCK\s+PREDICATE\s+security\.fn_tenant_predicate\(tenant_id\)\s+ON\s+{Regex.Escape(table)}\s+AFTER\s+INSERT", RegexOptions.IgnoreCase),
                $"{table} has no BLOCK PREDICATE AFTER INSERT (a tenant could otherwise write rows for another tenant).");
            Assert.True(
                Regex.IsMatch(all, $@"ADD\s+BLOCK\s+PREDICATE\s+security\.fn_tenant_predicate\(tenant_id\)\s+ON\s+{Regex.Escape(table)}\s+AFTER\s+UPDATE", RegexOptions.IgnoreCase),
                $"{table} has no BLOCK PREDICATE AFTER UPDATE (a tenant could otherwise move its rows to another tenant).");
        }
    }

    [Fact]
    public void A_module_creates_and_references_only_its_own_schema()
    {
        // no foreign keys across modules: modules meet through contracts and events, never through each other's tables
        foreach (var s in Scripts().Where(s => s.Assembly != "InvoiceHub.BuildingBlocks"))
        {
            var schema = s.Assembly["InvoiceHub.".Length..].ToLowerInvariant();
            foreach (Match m in Regex.Matches(s.Sql, @"(?:CREATE\s+(?:TABLE|SEQUENCE|INDEX\s+\w+\s+ON)|REFERENCES|ON)\s+(?<schema>[a-z_]+)\.[a-z_]+", RegexOptions.IgnoreCase))
            {
                var found = m.Groups["schema"].Value.ToLowerInvariant();
                Assert.True(found == schema, $"{s.Assembly}/{s.Name} touches schema '{found}' (its own is '{schema}'): {m.Value}");
            }
        }
    }

    [Fact]
    public void The_predicate_only_honours_system_mode_for_the_worker_role()
    {
        var security = Scripts().Single(s => s.Name == "001_security.sql").Sql;
        Assert.Contains("IS_MEMBER(N'worker_role') = 1", security);
        Assert.Contains("SESSION_CONTEXT(N'tenant_id')", security);
    }

    [Fact]
    public void Only_the_audit_module_has_any_sql_on_the_audit_table()
    {
        // other modules write audit lines through IAuditLog (same transaction), never with their own SQL
        foreach (var s in Scripts().Where(s => s.Assembly != "InvoiceHub.Audit"))
            Assert.DoesNotMatch(@"audit\.[a-z_]+", s.Sql);
    }

    [Fact]
    public void Personal_data_columns_exist_only_in_the_compliance_schema()
    {
        // ASR-12: the identity details of a verification live in ONE table; an event or any other table never gets a name, a date of birth or a document number column
        foreach (var s in Scripts().Where(s => s.Assembly != "InvoiceHub.Compliance"))
            Assert.DoesNotMatch(@"\b(subject_name|date_of_birth|document_number)\b", s.Sql);
    }

    [Fact]
    public void The_audit_log_is_append_only_for_both_application_logins()
    {
        // ASR-19: nobody holding an application login can change or remove history, whatever a bug or an injection tries
        var audit = Scripts().Single(s => s.Name == "001_audit.sql").Sql;
        foreach (var role in new[] { "api_role", "worker_role" })
        {
            Assert.Matches($@"DENY\s+UPDATE,\s*DELETE\s+ON\s+OBJECT::audit\.entry\s+TO\s+{role}", audit);
            Assert.DoesNotMatch($@"GRANT\s+[^;]*(UPDATE|DELETE)[^;]*audit\.entry\s+TO\s+{role}", audit);
        }
        Assert.DoesNotMatch(@"GRANT\s+[^;]*SCHEMA::audit", audit); // a schema-wide grant would be the way to undo the intent by accident
    }

    [Fact]
    public void Scripts_never_grant_the_api_role_access_to_the_security_schema()
    {
        foreach (var s in Scripts())
            Assert.DoesNotMatch(@"GRANT\s+[^;]*SCHEMA::security", s.Sql);
    }
}
