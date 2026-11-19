-- Module: Notifications. Schema `notifications`: who gets told what, and the in-app inbox (UX-SPEC D10).

IF SCHEMA_ID(N'notifications') IS NULL EXEC(N'CREATE SCHEMA notifications');
GO

-- One rule per trigger and tenant. No row means "the platform default", so a new tenant is protected from day one without having to configure anything.
CREATE TABLE notifications.alert_rule (
  tenant_id        uniqueidentifier NOT NULL,
  trigger_name     varchar(30)      NOT NULL,
  enabled          bit              NOT NULL,
  in_app           bit              NOT NULL,
  email            bit              NOT NULL,
  recipient_roles  varchar(80)      NOT NULL,               -- ',owner,admin,'
  updated_at       datetime2        NOT NULL CONSTRAINT df_alert_rule_updated DEFAULT SYSUTCDATETIME(),
  updated_by       varchar(80)      NULL,
  CONSTRAINT pk_alert_rule PRIMARY KEY (tenant_id, trigger_name)
);
GO

-- The unique key (tenant, event, kind) makes the handler idempotent: a redelivered event message never creates a second alert or a second email.
CREATE TABLE notifications.inbox_item (
  id           bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_inbox_item PRIMARY KEY,
  tenant_id    uniqueidentifier NOT NULL,
  event_id     uniqueidentifier NOT NULL,
  kind         varchar(30)      NOT NULL,
  at           datetime2        NOT NULL CONSTRAINT df_inbox_at DEFAULT SYSUTCDATETIME(),
  severity     varchar(10)      NOT NULL CONSTRAINT ck_inbox_severity CHECK (severity IN ('info', 'warning', 'critical')),
  title        nvarchar(200)    NOT NULL,
  body         nvarchar(800)    NOT NULL,
  endpoint_id  uniqueidentifier NULL,
  for_roles    varchar(80)      NOT NULL,
  visible      bit              NOT NULL,
  CONSTRAINT uq_inbox_event_kind UNIQUE (tenant_id, event_id, kind)
);
GO

CREATE INDEX ix_inbox_tenant_time ON notifications.inbox_item (tenant_id, id DESC) WHERE visible = 1;
GO

CREATE TABLE notifications.inbox_read (
  tenant_id  uniqueidentifier NOT NULL,
  item_id    bigint           NOT NULL CONSTRAINT fk_inbox_read_item REFERENCES notifications.inbox_item (id),
  user_sub   uniqueidentifier NOT NULL,
  read_at    datetime2        NOT NULL CONSTRAINT df_inbox_read_at DEFAULT SYSUTCDATETIME(),
  CONSTRAINT pk_inbox_read PRIMARY KEY (item_id, user_sub)
);
GO

CREATE SECURITY POLICY notifications.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.alert_rule,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.alert_rule AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.alert_rule AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.inbox_item,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.inbox_item AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.inbox_item AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.inbox_read,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.inbox_read AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON notifications.inbox_read AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::notifications TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::notifications TO worker_role;
GO
