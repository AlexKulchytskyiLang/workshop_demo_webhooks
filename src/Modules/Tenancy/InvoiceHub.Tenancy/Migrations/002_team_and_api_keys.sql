-- Module: Tenancy (M4). Team management and platform API keys.

-- A disabled member keeps their row (history, audit) but cannot sign in: Keycloak is told too.
ALTER TABLE tenancy.user_profile ADD disabled_at datetime2 NULL;
GO

-- Platform API keys (the pull API, for example GET /v1/events). The secret part is shown once and only its SHA-256 is stored.
-- A key looks like  ihk_<tenant>_<key id>_<secret>  so the tenant is known BEFORE the lookup, and the lookup then runs under that tenant's row-level security.
CREATE TABLE tenancy.api_key (
  id            uniqueidentifier NOT NULL CONSTRAINT pk_api_key PRIMARY KEY,
  tenant_id     uniqueidentifier NOT NULL CONSTRAINT fk_api_key_tenant REFERENCES tenancy.tenant (id),
  name          nvarchar(80)     NOT NULL,
  prefix        varchar(16)      NOT NULL,
  key_hash      binary(32)       NOT NULL,
  scopes        varchar(200)     NOT NULL,
  created_by    varchar(80)      NOT NULL,
  created_at    datetime2        NOT NULL CONSTRAINT df_api_key_created DEFAULT SYSUTCDATETIME(),
  expires_at    datetime2        NULL,
  last_used_at  datetime2        NULL,
  revoked_at    datetime2        NULL
);
GO

CREATE INDEX ix_api_key_tenant ON tenancy.api_key (tenant_id, created_at DESC);
GO

CREATE SECURITY POLICY tenancy.api_key_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON tenancy.api_key,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON tenancy.api_key AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON tenancy.api_key AFTER UPDATE
  WITH (STATE = ON);
GO
