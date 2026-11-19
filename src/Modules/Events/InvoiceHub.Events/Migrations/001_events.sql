-- Module: Events. Schema `events`: the canonical event store and the transactional outbox.

IF SCHEMA_ID(N'events') IS NULL EXEC(N'CREATE SCHEMA events');
GO

CREATE TABLE events.event (
  id            uniqueidentifier NOT NULL CONSTRAINT pk_event PRIMARY KEY,           -- becomes the stable webhook-id
  tenant_id     uniqueidentifier NOT NULL,
  type          varchar(80)      NOT NULL,
  aggregate_id  varchar(64)      NOT NULL,
  sequence      bigint           NOT NULL,                                          -- per aggregate: receivers apply only if newer
  data_json     nvarchar(max)    NOT NULL CONSTRAINT ck_event_json CHECK (ISJSON(data_json) = 1),
  occurred_at   datetime2        NOT NULL,
  is_test       bit              NOT NULL CONSTRAINT df_event_test DEFAULT 0,
  trace_parent  varchar(60)      NULL
);
GO

CREATE INDEX ix_event_tenant_time ON events.event (tenant_id, occurred_at DESC, id) INCLUDE (type, aggregate_id, sequence);
CREATE INDEX ix_event_aggregate   ON events.event (tenant_id, aggregate_id, sequence);
GO

CREATE TABLE events.outbox (
  id             bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_outbox PRIMARY KEY,
  event_id       uniqueidentifier NOT NULL CONSTRAINT uq_outbox_event UNIQUE,
  tenant_id      uniqueidentifier NOT NULL,
  created_at     datetime2        NOT NULL CONSTRAINT df_outbox_created DEFAULT SYSUTCDATETIME(),
  published_at   datetime2        NULL,
  claimed_by     varchar(60)      NULL,                                              -- relay replica holding the lease
  claimed_until  datetime2        NULL,                                              -- a crashed relay's rows become claimable again
  fanned_out_at  datetime2        NULL                                               -- set when fan-out has created the deliveries; the broker is only a transport
);
GO

-- the relay scans only what is still unpublished
CREATE INDEX ix_outbox_unpublished ON events.outbox (id) INCLUDE (claimed_until) WHERE published_at IS NULL;
-- published but never fanned out (the broker lost it, or fan-out kept failing): the relay republishes these
CREATE INDEX ix_outbox_stalled ON events.outbox (published_at) WHERE fanned_out_at IS NULL AND published_at IS NOT NULL;
GO

CREATE SECURITY POLICY events.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON events.event,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON events.event  AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON events.event  AFTER UPDATE,
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON events.outbox,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON events.outbox AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON events.outbox AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::events TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::events TO worker_role;
GO
