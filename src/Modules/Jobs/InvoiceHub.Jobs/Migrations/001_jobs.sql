-- Module: Jobs. Schema `jobs`: bulk exports and vendor catalogue imports (long-running work whose RESULT is a big file).

IF SCHEMA_ID(N'jobs') IS NULL EXEC(N'CREATE SCHEMA jobs');
GO

CREATE TABLE jobs.job (
  id             uniqueidentifier NOT NULL CONSTRAINT pk_job PRIMARY KEY,
  tenant_id      uniqueidentifier NOT NULL,
  kind           varchar(10)      NOT NULL CONSTRAINT ck_job_kind CHECK (kind IN ('export', 'import')),
  status         varchar(10)      NOT NULL CONSTRAINT ck_job_status CHECK (status IN ('queued', 'running', 'completed', 'failed')),
  params_json    nvarchar(max)    NOT NULL CONSTRAINT ck_job_params CHECK (ISJSON(params_json) = 1),
  result_json    nvarchar(max)    NULL CONSTRAINT ck_job_result CHECK (result_json IS NULL OR ISJSON(result_json) = 1),
  error          nvarchar(400)    NULL,
  claimed_by     varchar(60)      NULL,
  claimed_until  datetime2        NULL,                  -- a crashed runner's job becomes claimable again after this
  created_at     datetime2        NOT NULL CONSTRAINT df_job_created DEFAULT SYSUTCDATETIME(),
  started_at     datetime2        NULL,
  completed_at   datetime2        NULL
);
GO

CREATE INDEX ix_job_queue ON jobs.job (created_at) WHERE status IN ('queued', 'running');
GO

CREATE SECURITY POLICY jobs.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON jobs.job,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON jobs.job AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON jobs.job AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::jobs TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::jobs TO worker_role;
GO
