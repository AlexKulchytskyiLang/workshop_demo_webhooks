-- Module: Billing. Schema `billing`: the simulated invoicing domain that produces the demo events.

IF SCHEMA_ID(N'billing') IS NULL EXEC(N'CREATE SCHEMA billing');
GO

CREATE SEQUENCE billing.invoice_number_seq AS bigint START WITH 1000 INCREMENT BY 1;
GO

CREATE TABLE billing.invoice (
  id                uniqueidentifier NOT NULL CONSTRAINT pk_invoice PRIMARY KEY,
  tenant_id         uniqueidentifier NOT NULL,
  number            varchar(30)      NOT NULL,
  customer_name     nvarchar(120)    NOT NULL,
  customer_country  char(2)          NOT NULL,
  amount            decimal(18, 2)   NOT NULL,
  currency          char(3)          NOT NULL,
  status            varchar(12)      NOT NULL CONSTRAINT ck_invoice_status CHECK (status IN ('open', 'paid', 'overdue', 'void')),
  sequence          bigint           NOT NULL,                       -- version of this aggregate; carried by every event about it
  issued_at         datetime2        NOT NULL,
  due_at            datetime2        NOT NULL,
  paid_at           datetime2        NULL,
  CONSTRAINT uq_invoice_number UNIQUE (tenant_id, number)
);
GO

CREATE INDEX ix_invoice_tenant_status ON billing.invoice (tenant_id, status, due_at);
GO

CREATE SECURITY POLICY billing.tenant_isolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(tenant_id) ON billing.invoice,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON billing.invoice AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(tenant_id) ON billing.invoice AFTER UPDATE
  WITH (STATE = ON);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::billing TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::billing TO worker_role;
GRANT UPDATE ON OBJECT::billing.invoice_number_seq TO api_role;
GRANT UPDATE ON OBJECT::billing.invoice_number_seq TO worker_role;
GO
