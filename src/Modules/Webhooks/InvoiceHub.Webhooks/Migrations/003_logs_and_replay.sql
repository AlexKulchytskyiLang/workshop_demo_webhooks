-- Module: Webhooks (M4). What the delivery log needs to show (the request as sent, the object the event is about) and what replay needs to remember.

-- The request exactly as sent, secrets masked (the log never stores a credential), and the payload style used, so the body can be shown again as it was.
ALTER TABLE webhooks.delivery_attempt ADD
  request_headers_json nvarchar(max) NULL CONSTRAINT ck_attempt_req_headers CHECK (request_headers_json IS NULL OR ISJSON(request_headers_json) = 1),
  payload_style        varchar(8)    NULL,
  response_size        int           NULL;
GO

-- aggregate_id: search the log by the business object ("show me everything about invoice inv_...") without reading the events schema.
-- Replay: the same delivery (same webhook-id) is sent again. attempt_floor makes the retry schedule start over while the attempt numbers in the log keep counting.
ALTER TABLE webhooks.delivery ADD
  aggregate_id         varchar(64)      NULL,
  replay_job_id        uniqueidentifier NULL,
  replay_prior_status  varchar(16)      NULL,
  replay_count         int              NOT NULL CONSTRAINT df_delivery_replay_count DEFAULT 0,
  replayed_at          datetime2        NULL,
  attempt_floor        int              NOT NULL CONSTRAINT df_delivery_attempt_floor DEFAULT 0;
GO

CREATE INDEX ix_delivery_aggregate ON webhooks.delivery (tenant_id, aggregate_id) WHERE aggregate_id IS NOT NULL;
CREATE INDEX ix_delivery_replay_job ON webhooks.delivery (replay_job_id) WHERE replay_job_id IS NOT NULL;
GO

-- "Resume" needs a passing test first (UX-SPEC D3): remember when one last passed.
ALTER TABLE webhooks.endpoint ADD last_test_passed_at datetime2 NULL;
GO

-- A replay job: which deliveries were chosen, by whom, at what pace. Progress is read from the deliveries themselves (replay_job_id), so there is no
-- second state to keep in step: a job is "running" while any of its deliveries is still waiting or in flight.
CREATE TABLE webhooks.replay_job (
  id               uniqueidentifier NOT NULL CONSTRAINT pk_replay_job PRIMARY KEY,
  tenant_id        uniqueidentifier NOT NULL,
  endpoint_id      uniqueidentifier NOT NULL CONSTRAINT fk_replay_endpoint REFERENCES webhooks.endpoint (id),
  kind             varchar(10)      NOT NULL CONSTRAINT ck_replay_kind CHECK (kind IN ('resume', 'replay', 'single')),
  mode             varchar(12)      NOT NULL CONSTRAINT ck_replay_mode CHECK (mode IN ('deliver_all', 'only_recent', 'discard', 'replay')),
  states           varchar(40)      NOT NULL,                     -- which delivery states were selected, e.g. 'dead,held'
  event_type       varchar(80)      NULL,
  from_at          datetime2        NULL,
  to_at            datetime2        NULL,
  total            int              NOT NULL,
  discarded        int              NOT NULL CONSTRAINT df_replay_discarded DEFAULT 0,
  rate_per_second  int              NOT NULL,
  created_by       varchar(80)      NOT NULL,
  created_at       datetime2        NOT NULL CONSTRAINT df_replay_created DEFAULT SYSUTCDATETIME(),
  cancelled_at     datetime2        NULL
);
GO

CREATE INDEX ix_replay_job_tenant ON webhooks.replay_job (tenant_id, created_at DESC);
GO

CREATE SECURITY POLICY webhooks.replay_job_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.replay_job,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.replay_job AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON webhooks.replay_job AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::webhooks.replay_job TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::webhooks.replay_job TO worker_role;
GO
