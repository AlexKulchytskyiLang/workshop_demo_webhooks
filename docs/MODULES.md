# Adding a module

The platform is a modular monolith: one codebase, several hosts, modules that own their data and expose only a contract. The Tenancy module is the reference. Copy its shape.

## Checklist

1. **Projects** under `src/Modules/<Name>/`
   - `InvoiceHub.<Name>.Contracts`: plain DTOs, interfaces and event shapes other modules may use. No EF Core, no ASP.NET, no implementation types (architecture test).
   - `InvoiceHub.<Name>`: the implementation. References `BuildingBlocks` and its own Contracts. Never references another module's implementation.
2. **Database**: one schema named after the module. Numbered SQL scripts `Migrations/NNN_snake_case.sql`, embedded as resources, one batch per `GO`. Never edit an applied script (the runner compares hashes and refuses): add the next number.
3. **Row-level security**: every table with a `tenant_id` column needs, in the same module's scripts,
   ```sql
   CREATE SECURITY POLICY <schema>.tenant_isolation
     ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON <schema>.<table>,
     ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON <schema>.<table> AFTER INSERT,
     ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON <schema>.<table> AFTER UPDATE
     WITH (STATE = ON);
   ```
   plus `GRANT ... ON SCHEMA::<schema> TO api_role / worker_role`. The architecture test fails when a policy is missing. Tables that are global metadata (like `tenancy.tenant`) have no `tenant_id` column.
4. **Module class** implementing `IModule` (`ConfigureServices`, `MapEndpoints`), DbContext registered with `AddModuleDbContext<T>`, endpoints grouped under `/api/...` with `RequireAuthorization()` or a policy named after a permission (`Permissions.*`).
5. **Compose it** in the host that needs it (`Program.cs`: `modules = [new TenancyModule(), new <Name>Module()]`) and, for schema changes, in the Migrator (`runner.ApplyAsync("<name>", typeof(<Name>Module).Assembly)`).
6. **Register it in the architecture tests**: add the assembly to `ModuleRulesTests.Modules()`.
7. **Anonymous routes** are a security decision: add them to the allow-list in `EndpointSecurityTests` and say why in the pull request.
8. **Observability**: use an `ActivitySource` / `Meter` named `InvoiceHub.<Name>` (picked up by ServiceDefaults) and log with structured properties, never with PII.

## Tenant context in each kind of code

| Code runs as | Tenant comes from | SQL login | How |
|---|---|---|---|
| API request | the token's `tenant_id` (checked by `TenantResolutionMiddleware`) | `api_user` | automatic: `ITenantScope.SetTenant` |
| Worker handling one delivery | the delivery row | `worker_user` | create a DI scope, `ITenantScope.SetTenant(tenantId)` |
| Worker doing cross-tenant work (relay, sweeper, health) | none | `worker_user` | `ITenantScope.SetSystem()`: only the worker role is honoured by SQL, never `api_user` |
| Migrator and seeding | explicit per insert | admin | sets `SESSION_CONTEXT` itself |
