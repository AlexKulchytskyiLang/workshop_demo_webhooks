# Spike 0: what the emulators and libraries really do

Date: 2026-11-12 · Code: `demo/spikes/spike0/` (Aspire AppHost + one probe) · Raw results: `demo/spikes/spike0/results/spike0-results.json` (final run) and earlier runs next to it.

**Versions actually used:** .NET SDK 10.0.103 · Aspire 13.6.5 (Keycloak integration is the preview `13.6.5-preview`) · Service Bus emulator image **2.0.0** (Aspire's default; the 1.1.2 image on this machine is not used) · Keycloak **26.6** · SQL Server 2022-latest · EF Core 10.0.15 · `Azure.Messaging.ServiceBus` 7.21.0.

**How to re-run:** `dotnet run --project demo/spikes/spike0/Spike0.AppHost --launch-profile http`. It stops itself when the probe finishes (or after 10 minutes) and removes its containers. First run pulls images.

## Verdict
Nothing blocks the plan. Three adjustments follow from what was learned (marked **ADJUST**): never rely on cancelling scheduled messages; give background jobs an explicit "system" tenant context; emit the newest signature first. The Service Bus emulator turned out to support more than its documentation says (scheduled messages, duplicate detection, filters, dead-lettering), within clear limits.

## Results
| Area | Check | Result | What it means for the design |
|---|---|---|---|
| Service Bus | Scheduled message arrives at its due time | **PASS**, 0.2 s late | Retries can be **scheduled messages**. SQL `next_attempt_at` stays the source of truth and the sweeper is the safety net |
| Service Bus | Cancel a scheduled message | **FAIL**: the call returns ok, the message is still delivered at its due time | **ADJUST:** never rely on cancel. A delivery worker always re-reads the SQL state first; if the delivery is already Succeeded, Held, Paused or Dead it completes the message and sends nothing |
| Service Bus | Duplicate detection (topic and queue) | **PASS** | `events` topic uses `MessageId = event id`. **Emulator limit: window must be at most 5 minutes** (config above 5 min makes the container crash at start). Cloud default is 10 min, maximum 7 days |
| Service Bus | Scheduled + immediate message with the same MessageId on a dedup queue | 1 delivered | Matches the cloud docs: scheduled messages take part in duplicate detection. So the `deliveries` queue has **no** duplicate detection and a retry gets a fresh MessageId (delivery id + attempt number) |
| Service Bus | Dead-letter after MaxDeliveryCount | **PASS** via abandon and via lock expiry, reason `MaxDeliveryCountExceeded`; explicit dead-letter with a custom reason also works | DLQ is usable for poison messages; alert on its depth. Business `Dead` stays in SQL |
| Service Bus | Settlement reliability | `AbandonMessage` path failed with `MessageLockLost` in the first two full runs and passed in the isolated diagnostic run (cause not pinned down: it is either abandon or the following dead-letter-queue read) | **ADJUST:** every settle call tolerates `MessageLockLost` (log, rely on redelivery). Handlers are idempotent anyway |
| Service Bus | Unsettled message after lock expiry | redelivered, delivery count 2; completing the old lock fails | Crash safety (ASR-01) is real: a killed worker's messages return |
| Service Bus | Correlation filter on an application property | **PASS** | Optional: per-event-type subscriptions. v1 uses one `fanout` subscription |
| Service Bus | Concurrent connections | **10 total** (the 11th client fails with `ConnectionsQuotaExceeded`) | One `ServiceBusClient` per process (singleton). Budget: Api 1 + Delivery 3 + Simulator 1 + tests. Integration tests must not open extra clients |
| Service Bus | Throughput | send about 13,000 msg/s; **receive + complete 50 to 160 msg/s**, does not scale with more processors | **ADJUST expectations:** sustained demo load at most about 50 deliveries/s. ASR-03 is demonstrated at 30 events/s. The 1,000 deliveries/s target of ASR-05 cannot be shown on the emulator; we prove fan-out and linear scaling at toy scale and say so |
| SQL | RLS filter + block predicate via `SESSION_CONTEXT`, set by an EF connection interceptor | **PASS** (tenant sees only own rows; no context returns 0 rows; block predicate error 33504; cross-tenant UPDATE affects 0 rows) | Pattern confirmed for P6 / ASR-11 |
| SQL | Pooled connection reuse | **no leak** (context cleared on reset) | Still set on every open and covered by a regression test |
| SQL | RLS vs the admin login (sa/dbo) | **the policy also filters sa/dbo**: no context returns 0 rows | **ADJUST:** background jobs that work across tenants (relay, sweeper, health) cannot use an empty context. Add a `system` key to `SESSION_CONTEXT` that the predicate honours **only** for a dedicated `worker` SQL login/role; the API login can never set it. The Migrator sets context per tenant or disables the policy inside its own transaction |
| Keycloak | Aspire preview integration + realm import | **PASS**. Container is served over **HTTPS at 8085**; issuer is `https://localhost:8085/realms/invoicehub` | Portal and API use exactly that issuer; the ASP.NET dev certificate must be trusted (it is on this machine) |
| Keycloak | Token for a seeded user | `tenant_id`, `roles`, audience `invoicehub-api` present; `auth_time` absent for the direct grant (expected) | Mappers work. `auth_time` for step-up is verified in M1 with a browser |
| Keycloak | Admin API with a service account | **PASS** once `view-realm` was added to `manage-users`, `view-users`, `query-users` (without it, reading a role returns 403; `query-groups` was added at the same time and is not proven necessary) | Registration saga can create users, set `tenant_id`, assign roles |
| Keycloak | Custom attribute | works **only because `tenant_id` is declared in the realm's user profile**; an undeclared attribute was dropped | Realm JSON declares the user profile (done in `infra/keycloak` for M1) |
| Standard Webhooks | Our HMAC vs the reference library `StandardWebhooks` | **identical signatures**; tampered body and 6-minute-old timestamp rejected | ASR-08 and ASR-17 confirmed at the format level |
| Standard Webhooks | Several signatures in one header (rotation overlap) | NuGet **1.1.1 and 1.1.2 only honour the first signature**; **1.0.36 is correct** (any position, three signatures, double spaces) | **ADJUST:** emit the **newest** secret's signature first, document it for tenants, run the interop test against 1.0.36, and keep a test that pins the 1.1.x behaviour so the day it is fixed we notice. Our own sample verifiers do not use the library |

## Decisions to carry into M1
1. Retry timing = scheduled message on `deliveries` (fresh MessageId per attempt) + SQL `next_attempt_at` + sweeper. No cancel.
2. `events` topic: duplicate detection on, window 5 min in the demo (configurable; production default 10 min or more). `deliveries` queue: off.
3. Settlement helper wraps complete / abandon / dead-letter and swallows `MessageLockLost` with a log line.
4. RLS gets a `system` mode for the worker role only; Migrator and seed use explicit contexts. Two SQL logins: `api_user` (tenant only) and `worker_user` (tenant or system).
5. Keycloak realm JSON declares the user profile with `tenant_id`; the admin service account roles above; HTTPS issuer `https://localhost:8085`.
6. Demo load budget at most about 50 deliveries/s; chaos and ASR proofs are scaled accordingly and the write-up states the production difference.
7. Signature header order: newest first.

## Not covered by this spike (and why)
- Step-up re-authentication (`prompt=login&max_age=0`) needs a browser: checked in M1.
- `traceparent` propagation through Service Bus message properties: standard SDK behaviour, verified in M2 with the first end-to-end trace.
- Sessions: not used (ordering is not guaranteed by design, ADR-10).

## What went wrong while doing the spike (for the record)
The first run hung for 35 minutes because my emulator config used a 10-minute duplicate-detection window; this emulator version refuses to start above 5 minutes and the probe kept waiting for it. Fixed in the config, and the AppHost now has a 10-minute watchdog that stops everything. Several probe checks were also my own mistakes (shared queues between tests); they were isolated onto dedicated queues.
