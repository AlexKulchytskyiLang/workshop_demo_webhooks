-- Module: Compliance. Schema `compliance`: identity verifications (KYC). The ONLY place the personal data of a verification is stored.

IF SCHEMA_ID(N'compliance') IS NULL EXEC(N'CREATE SCHEMA compliance');
GO

CREATE TABLE compliance.verification (
  id               uniqueidentifier NOT NULL CONSTRAINT pk_verification PRIMARY KEY,
  tenant_id        uniqueidentifier NOT NULL,
  decision         varchar(10)      NOT NULL CONSTRAINT ck_verification_decision CHECK (decision IN ('approved', 'review', 'rejected')),
  risk_score       int              NOT NULL CONSTRAINT ck_verification_risk CHECK (risk_score BETWEEN 0 AND 100),
  subject_name     nvarchar(120)    NOT NULL,                  -- personal data: never put into an event, a message, a blob or a log
  date_of_birth    date             NOT NULL,                  -- personal data
  document_number  nvarchar(30)     NOT NULL,                  -- personal data
  country          char(2)          NOT NULL,
  completed_at     datetime2        NOT NULL CONSTRAINT df_verification_completed DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX ix_verification_tenant ON compliance.verification (tenant_id, completed_at DESC);
GO

CREATE SECURITY POLICY compliance.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON compliance.verification,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON compliance.verification AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON compliance.verification AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::compliance TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::compliance TO worker_role;
GO
