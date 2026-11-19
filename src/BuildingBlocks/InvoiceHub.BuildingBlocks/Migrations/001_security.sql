-- Platform security objects shared by every module (module "platform").
-- Application logins (api_user, worker_user) are created by the Migrator at run time because their passwords are secrets.

IF DATABASE_PRINCIPAL_ID(N'api_role') IS NULL CREATE ROLE api_role;
IF DATABASE_PRINCIPAL_ID(N'worker_role') IS NULL CREATE ROLE worker_role;
GO

IF SCHEMA_ID(N'security') IS NULL EXEC(N'CREATE SCHEMA security');
GO

-- Row-level security predicate used by every tenant-scoped table (principle P6: the tenant is enforced twice).
--   * a row is visible when its tenant_id equals SESSION_CONTEXT('tenant_id'), which the API/worker sets per unit of work;
--   * system mode (SESSION_CONTEXT('system') = 1) lets cross-tenant jobs see all rows, but ONLY for members of worker_role.
--     The API login is not a member, so a bug or an injection in the API can never switch it on.
--   * with no context at all, no rows are visible (fail closed). Spike 0 showed this applies to sa/dbo as well.
CREATE FUNCTION security.fn_tenant_predicate (@tenant_id uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
  SELECT 1 AS allowed
  WHERE @tenant_id = TRY_CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
     OR (TRY_CAST(SESSION_CONTEXT(N'system') AS int) = 1 AND IS_MEMBER(N'worker_role') = 1);
GO
