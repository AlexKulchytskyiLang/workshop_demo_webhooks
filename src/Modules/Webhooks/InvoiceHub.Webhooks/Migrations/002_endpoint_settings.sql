-- Module: Webhooks (M3). Per-endpoint settings behind the portal's Delivery, Security and Settings tabs, and the asymmetric signing keys.

ALTER TABLE webhooks.endpoint ADD
  environment          varchar(12)   NOT NULL CONSTRAINT df_endpoint_env DEFAULT 'production' CONSTRAINT ck_endpoint_env CHECK (environment IN ('production', 'staging', 'development')),
  retry_json           nvarchar(max) NULL CONSTRAINT ck_endpoint_retry_json CHECK (retry_json IS NULL OR ISJSON(retry_json) = 1),   -- parameters of the "custom" retry preset
  circuit_failures     int           NOT NULL CONSTRAINT df_endpoint_circuit DEFAULT 5 CONSTRAINT ck_endpoint_circuit CHECK (circuit_failures BETWEEN 2 AND 50),
  autodisable_failures int           NOT NULL CONSTRAINT df_endpoint_ad_failures DEFAULT 100 CONSTRAINT ck_endpoint_ad_failures CHECK (autodisable_failures BETWEEN 5 AND 10000),
  autodisable_hours    int           NOT NULL CONSTRAINT df_endpoint_ad_hours DEFAULT 24 CONSTRAINT ck_endpoint_ad_hours CHECK (autodisable_hours BETWEEN 1 AND 168),
  autodisable_days     int           NOT NULL CONSTRAINT df_endpoint_ad_days DEFAULT 5 CONSTRAINT ck_endpoint_ad_days CHECK (autodisable_days BETWEEN 1 AND 30),
  hold_days            int           NOT NULL CONSTRAINT df_endpoint_hold DEFAULT 7 CONSTRAINT ck_endpoint_hold CHECK (hold_days BETWEEN 1 AND 30),
  auth_type            varchar(10)   NOT NULL CONSTRAINT df_endpoint_auth DEFAULT 'none' CONSTRAINT ck_endpoint_auth CHECK (auth_type IN ('none', 'header')),
  auth_header_name     varchar(50)   NULL,
  auth_value_enc       varbinary(1200) NULL;                                                                                       -- encrypted API key / bearer token
GO

-- signing mode: hmac, es256, or both while a tenant migrates between them
ALTER TABLE webhooks.endpoint DROP CONSTRAINT ck_endpoint_signing;
ALTER TABLE webhooks.endpoint ADD CONSTRAINT ck_endpoint_signing CHECK (signing IN ('hmac', 'es256', 'both'));
GO

-- every key belongs to one algorithm; for es256 the encrypted column holds the PKCS#8 private key (never shown, never leaves the platform)
ALTER TABLE webhooks.endpoint_secret ADD algorithm varchar(8) NOT NULL CONSTRAINT df_secret_algorithm DEFAULT 'hmac' CONSTRAINT ck_secret_algorithm CHECK (algorithm IN ('hmac', 'es256'));
ALTER TABLE webhooks.endpoint_secret ALTER COLUMN secret_enc varbinary(1200) NOT NULL;
GO

-- The PUBLIC halves of the es256 keys, readable without a tenant context: receivers fetch them anonymously as a JWKS. Nothing secret is stored here.
CREATE TABLE webhooks.public_key (
  endpoint_id  uniqueidentifier NOT NULL CONSTRAINT fk_public_key_endpoint REFERENCES webhooks.endpoint (id),
  kid          varchar(24)      NOT NULL,
  jwk_json     nvarchar(max)    NOT NULL CONSTRAINT ck_public_key_jwk CHECK (ISJSON(jwk_json) = 1),
  valid_from   datetime2        NOT NULL,
  valid_until  datetime2        NULL,
  CONSTRAINT pk_public_key PRIMARY KEY (endpoint_id, kid)
);
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::webhooks.public_key TO api_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::webhooks.public_key TO worker_role;
GO

-- payload style "auto": the event's own default (personal-data events are always thin, bulk results will use a claim-check link)
ALTER TABLE webhooks.endpoint DROP CONSTRAINT ck_endpoint_style;
ALTER TABLE webhooks.endpoint ADD CONSTRAINT ck_endpoint_style CHECK (payload_style IN ('fat', 'thin', 'claim', 'auto'));
GO
