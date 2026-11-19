-- Module: Webhooks. Schema `webhooks`: endpoints, secrets, subscriptions, deliveries, attempts.
-- SQL is the source of truth for delivery state (principle P3); the broker only carries ids.

IF SCHEMA_ID(N'webhooks') IS NULL EXEC(N'CREATE SCHEMA webhooks');
GO

CREATE TABLE webhooks.endpoint (
  id                  uniqueidentifier NOT NULL CONSTRAINT pk_endpoint PRIMARY KEY,
  tenant_id           uniqueidentifier NOT NULL,
  name                nvarchar(120)    NOT NULL,
  url                 nvarchar(2000)   NOT NULL,
  description         nvarchar(400)    NULL,
  status              varchar(12)      NOT NULL CONSTRAINT ck_endpoint_status CHECK (status IN ('pending', 'active', 'paused', 'disabled')),
  disabled_reason     varchar(40)      NULL,
  payload_style       varchar(8)       NOT NULL CONSTRAINT df_endpoint_style DEFAULT 'fat' CONSTRAINT ck_endpoint_style CHECK (payload_style IN ('fat', 'thin', 'claim')),
  api_version         varchar(10)      NOT NULL CONSTRAINT df_endpoint_version DEFAULT '2026-11-01',
  signing             varchar(8)       NOT NULL CONSTRAINT df_endpoint_signing DEFAULT 'hmac' CONSTRAINT ck_endpoint_signing CHECK (signing IN ('hmac', 'es256')),
  retry_preset        varchar(12)      NOT NULL CONSTRAINT df_endpoint_retry DEFAULT 'standard',
  timeout_seconds     int              NOT NULL CONSTRAINT df_endpoint_timeout DEFAULT 10 CONSTRAINT ck_endpoint_timeout CHECK (timeout_seconds BETWEEN 1 AND 30),
  max_inflight        int              NOT NULL CONSTRAINT df_endpoint_inflight DEFAULT 5 CONSTRAINT ck_endpoint_inflight CHECK (max_inflight BETWEEN 1 AND 10),
  headers_json        nvarchar(max)    NULL CONSTRAINT ck_endpoint_headers CHECK (headers_json IS NULL OR ISJSON(headers_json) = 1),
  verified_at         datetime2        NULL,
  consecutive_failures int             NOT NULL CONSTRAINT df_endpoint_failures DEFAULT 0,
  failing_since       datetime2        NULL,
  circuit_open_until  datetime2        NULL,
  circuit_probe_step  int              NOT NULL CONSTRAINT df_endpoint_probe DEFAULT 0,
  last_success_at     datetime2        NULL,
  last_failure_at     datetime2        NULL,
  created_by          varchar(80)      NULL,
  created_at          datetime2        NOT NULL CONSTRAINT df_endpoint_created DEFAULT SYSUTCDATETIME(),
  updated_at          datetime2        NOT NULL CONSTRAINT df_endpoint_updated DEFAULT SYSUTCDATETIME(),
  deleted_at          datetime2        NULL
);
GO

CREATE INDEX ix_endpoint_tenant ON webhooks.endpoint (tenant_id, created_at) WHERE deleted_at IS NULL;
GO

-- signing secrets, reversible (we sign with them): AES-GCM, context = endpoint id. Two valid rows = rotation overlap (ASR-09)
CREATE TABLE webhooks.endpoint_secret (
  id            uniqueidentifier NOT NULL CONSTRAINT pk_endpoint_secret PRIMARY KEY,
  tenant_id     uniqueidentifier NOT NULL,
  endpoint_id   uniqueidentifier NOT NULL CONSTRAINT fk_secret_endpoint REFERENCES webhooks.endpoint (id),
  secret_enc    varbinary(256)   NOT NULL,
  key_id        varchar(24)      NOT NULL,
  valid_from    datetime2        NOT NULL CONSTRAINT df_secret_from DEFAULT SYSUTCDATETIME(),
  valid_until   datetime2        NULL,                       -- NULL = current
  created_at    datetime2        NOT NULL CONSTRAINT df_secret_created DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX ix_secret_endpoint ON webhooks.endpoint_secret (endpoint_id, valid_from DESC);
GO

CREATE TABLE webhooks.subscription (
  tenant_id     uniqueidentifier NOT NULL,
  endpoint_id   uniqueidentifier NOT NULL CONSTRAINT fk_subscription_endpoint REFERENCES webhooks.endpoint (id),
  event_type    varchar(80)      NOT NULL,
  filter_json   nvarchar(max)    NULL CONSTRAINT ck_subscription_filter CHECK (filter_json IS NULL OR ISJSON(filter_json) = 1),
  CONSTRAINT pk_subscription PRIMARY KEY (endpoint_id, event_type)
);
GO

CREATE INDEX ix_subscription_type ON webhooks.subscription (tenant_id, event_type) INCLUDE (endpoint_id);
GO

CREATE TABLE webhooks.delivery (
  id                uniqueidentifier NOT NULL CONSTRAINT pk_delivery PRIMARY KEY,
  tenant_id         uniqueidentifier NOT NULL,
  event_id          uniqueidentifier NOT NULL,
  endpoint_id       uniqueidentifier NOT NULL CONSTRAINT fk_delivery_endpoint REFERENCES webhooks.endpoint (id),
  event_type        varchar(80)      NOT NULL,
  status            varchar(16)      NOT NULL CONSTRAINT ck_delivery_status CHECK (status IN ('pending', 'in_flight', 'succeeded', 'retry_scheduled', 'held', 'dead')),
  attempt_count     int              NOT NULL CONSTRAINT df_delivery_attempts DEFAULT 0,
  next_attempt_at   datetime2        NULL,
  deadline_at       datetime2        NOT NULL,                -- occurred_at + the retry window (72 h)
  last_status_code  int              NULL,
  last_error        nvarchar(400)    NULL,
  is_test           bit              NOT NULL CONSTRAINT df_delivery_test DEFAULT 0,
  claimed_by        varchar(60)      NULL,                    -- worker replica holding the in-flight lease
  claimed_until     datetime2        NULL,                    -- a crashed worker's row becomes reclaimable after this
  dispatch_until    datetime2        NULL,                    -- a broker message for this row is believed to exist until then
  held_reason       varchar(40)      NULL,
  held_until        datetime2        NULL,                    -- hold expiry (7 days), then Dead
  first_attempt_at  datetime2        NULL,
  created_at        datetime2        NOT NULL CONSTRAINT df_delivery_created DEFAULT SYSUTCDATETIME(),
  updated_at        datetime2        NOT NULL CONSTRAINT df_delivery_updated DEFAULT SYSUTCDATETIME(),
  completed_at      datetime2        NULL,
  CONSTRAINT uq_delivery_event_endpoint UNIQUE (event_id, endpoint_id)   -- fan-out is idempotent: same event, same endpoint, one row
);
GO

-- the sweeper looks only at work that is not finished
CREATE INDEX ix_delivery_due ON webhooks.delivery (next_attempt_at) INCLUDE (tenant_id, dispatch_until, attempt_count) WHERE status IN ('pending', 'retry_scheduled');
CREATE INDEX ix_delivery_inflight ON webhooks.delivery (claimed_until) WHERE status = 'in_flight';
CREATE INDEX ix_delivery_held ON webhooks.delivery (held_until) WHERE status = 'held';
CREATE INDEX ix_delivery_endpoint ON webhooks.delivery (tenant_id, endpoint_id, created_at DESC) INCLUDE (status, event_type, attempt_count);
CREATE INDEX ix_delivery_tenant ON webhooks.delivery (tenant_id, created_at DESC) INCLUDE (status, endpoint_id, event_type);
GO

CREATE TABLE webhooks.delivery_attempt (
  id                bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_attempt PRIMARY KEY,
  tenant_id         uniqueidentifier NOT NULL,
  delivery_id       uniqueidentifier NOT NULL CONSTRAINT fk_attempt_delivery REFERENCES webhooks.delivery (id),
  attempt_no        int              NOT NULL,
  started_at        datetime2        NOT NULL,
  duration_ms       int              NOT NULL,
  outcome           varchar(24)      NOT NULL,
  status_code       int              NULL,
  response_snippet  nvarchar(1000)   NULL,                    -- capped: we never store more of a receiver's reply than this
  error             nvarchar(400)    NULL,
  trace_id          varchar(32)      NULL
);
GO

CREATE INDEX ix_attempt_delivery ON webhooks.delivery_attempt (delivery_id, attempt_no);
GO

-- per-endpoint concurrency (ASR-07): a worker must hold a free slot row to call the endpoint; at most max_inflight (<= 10) exist in use.
-- A crashed worker's slot frees itself when its lease expires.
CREATE TABLE webhooks.endpoint_slot (
  endpoint_id   uniqueidentifier NOT NULL CONSTRAINT fk_slot_endpoint REFERENCES webhooks.endpoint (id),
  slot_no       int              NOT NULL,
  tenant_id     uniqueidentifier NOT NULL,
  delivery_id   uniqueidentifier NULL,
  leased_until  datetime2        NULL,
  CONSTRAINT pk_endpoint_slot PRIMARY KEY (endpoint_id, slot_no)
);
GO

CREATE SECURITY POLICY webhooks.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint_secret,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint_secret AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint_secret AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.subscription,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.subscription AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.subscription AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.delivery,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.delivery AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.delivery AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.delivery_attempt,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.delivery_attempt AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.delivery_attempt AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint_slot,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint_slot AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.endpoint_slot AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::webhooks TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::webhooks TO worker_role;
GO
