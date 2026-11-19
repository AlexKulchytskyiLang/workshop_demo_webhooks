-- Module: Tenancy. Schema `tenancy`.

IF SCHEMA_ID(N'tenancy') IS NULL EXEC(N'CREATE SCHEMA tenancy');
GO

-- The tenant directory is global metadata (id, slug, name, status, limits). It is NOT row-level secured on purpose:
-- the public tenant picker has to read it before anybody has signed in. It holds no tenant data.
CREATE TABLE tenancy.tenant (
  id                        uniqueidentifier NOT NULL CONSTRAINT pk_tenant PRIMARY KEY,
  slug                      varchar(40)      NOT NULL CONSTRAINT uq_tenant_slug UNIQUE,
  name                      nvarchar(120)    NOT NULL,
  status                    varchar(16)      NOT NULL CONSTRAINT ck_tenant_status CHECK (status IN ('pending', 'active', 'suspended')),
  delivery_quota_per_min    int              NOT NULL CONSTRAINT df_tenant_quota DEFAULT 600,
  max_inflight_per_endpoint int              NOT NULL CONSTRAINT df_tenant_inflight DEFAULT 5,
  created_at                datetime2        NOT NULL CONSTRAINT df_tenant_created DEFAULT SYSUTCDATETIME()
);
GO

-- Membership projection of the Keycloak users of a tenant (Keycloak stays the identity source of truth).
CREATE TABLE tenancy.user_profile (
  id            uniqueidentifier NOT NULL CONSTRAINT pk_user_profile PRIMARY KEY,
  tenant_id     uniqueidentifier NOT NULL CONSTRAINT fk_user_profile_tenant REFERENCES tenancy.tenant (id),
  keycloak_sub  uniqueidentifier NOT NULL CONSTRAINT uq_user_profile_sub UNIQUE,
  email         nvarchar(254)    NOT NULL,
  display_name  nvarchar(120)    NOT NULL,
  role          varchar(16)      NOT NULL CONSTRAINT ck_user_profile_role CHECK (role IN ('owner', 'admin', 'developer', 'support', 'viewer')),
  created_at    datetime2        NOT NULL CONSTRAINT df_user_profile_created DEFAULT SYSUTCDATETIME(),
  last_seen_at  datetime2        NULL
);
GO

CREATE INDEX ix_user_profile_tenant ON tenancy.user_profile (tenant_id, role);
GO

CREATE SECURITY POLICY tenancy.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON tenancy.user_profile,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON tenancy.user_profile AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON tenancy.user_profile AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::tenancy TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::tenancy TO worker_role;
GO
