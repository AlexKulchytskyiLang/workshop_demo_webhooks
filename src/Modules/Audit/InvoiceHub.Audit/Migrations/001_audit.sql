-- Module: Audit. Schema `audit`: the append-only log of who changed what (ASR-19).

IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA audit');
GO

CREATE TABLE audit.entry (
  id            bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_audit_entry PRIMARY KEY,
  tenant_id     uniqueidentifier NOT NULL,
  at            datetime2        NOT NULL CONSTRAINT df_audit_at DEFAULT SYSUTCDATETIME(),
  actor         varchar(80)      NOT NULL,
  actor_role    varchar(16)      NULL,
  action        varchar(60)      NOT NULL,
  target_type   varchar(30)      NOT NULL,
  target_id     varchar(64)      NULL,
  target_name   nvarchar(120)    NULL,
  details_json  nvarchar(max)    NULL CONSTRAINT ck_audit_details CHECK (details_json IS NULL OR ISJSON(details_json) = 1),
  ip            varchar(45)      NULL,
  trace_id      varchar(32)      NULL
);
GO

CREATE INDEX ix_audit_tenant_time ON audit.entry (tenant_id, at DESC, id DESC) INCLUDE (actor, action);
CREATE INDEX ix_audit_target ON audit.entry (tenant_id, target_type, target_id);
GO

CREATE SECURITY POLICY audit.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON audit.entry,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON audit.entry AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON audit.entry AFTER UPDATE
  WITH (STATE = ON);
GO

-- append-only: both application logins may add and read lines, and nothing else. The explicit DENY survives any later broad GRANT.
GRANT SELECT, INSERT ON OBJECT::audit.entry TO api_role;
GRANT SELECT, INSERT ON OBJECT::audit.entry TO worker_role;
DENY UPDATE, DELETE ON OBJECT::audit.entry TO api_role;
DENY UPDATE, DELETE ON OBJECT::audit.entry TO worker_role;
GO
