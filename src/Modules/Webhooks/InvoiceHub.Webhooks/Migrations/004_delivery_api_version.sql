-- Module: Webhooks (M5). A delivery remembers the payload version its endpoint had when the delivery was CREATED (UX-SPEC D5):
-- retries, replays and held events keep that version, so upgrading an endpoint never changes what is already on its way.
ALTER TABLE webhooks.delivery ADD api_version varchar(10) NOT NULL CONSTRAINT df_delivery_api_version DEFAULT '2026-11-01';
GO
