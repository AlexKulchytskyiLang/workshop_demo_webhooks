# How delivery works, and why nothing is lost

This is the implemented design (M2). It follows the deck; where the code taught us something, the deck's wording is corrected here and listed under "Notes for deck v3".

## The path of one event

```
Billing command ──(one SQL transaction)──▶ invoice row + events.event + events.outbox
        │
Outbox relay (Delivery host, 2 replicas) ── claims rows with UPDLOCK/READPAST + lease ──▶ topic "events"  (MessageId = event id)
        │
Fan-out consumer ── matches subscriptions + filters ──▶ one webhooks.delivery row per endpoint (unique: event + endpoint) ──▶ queue "deliveries" (delivery id only)
        │
Delivery consumer ── reads SQL state ── slot ── claim ── sign ── send ── classify ── record ──▶ succeeded | retry_scheduled | held | dead
        │
Receiver ── verify signature ── inbox (unique webhook-id) ── 200 ── process later (sequence guard)
```

SQL is the source of truth at every step. The broker is a fast transport that may lose, duplicate or reorder messages without any effect on correctness.

## Who guarantees what

| Guarantee | Mechanism | Where |
|---|---|---|
| No event lost between "business change" and "queued" (ASR-01) | Event and outbox row are written in the **same transaction** as the invoice. No dual write | `InvoiceCommands`, `EventPublisher` |
| A crash after publishing does not lose or double-count | Relay claims rows with a lease, publishes, then marks. Republish after a crash is absorbed by topic duplicate detection and by the fan-out unique key | `OutboxRelay` |
| The broker losing a message does not lose an event | Every outbox row carries `fanned_out_at`. A row that was published but never confirmed by fan-out is **republished** after a timeout. The emulator keeps nothing on restart, so this is not theoretical | `OutboxRelay.RepublishStalledAsync`, `IFanOutTracker` |
| One event, one delivery per endpoint, however often fan-out runs | `UNIQUE (event_id, endpoint_id)` + `INSERT ... WHERE NOT EXISTS` | `FanOutHandler` |
| A crashed worker's attempt is taken back | In-flight rows carry `claimed_until`. The sweeper moves expired ones back to the due list | `RetrySweeper` |
| A timer lost with the broker is recovered | Every due delivery without a live message (`dispatch_until` lapsed) is re-queued by the sweeper. Retries longer than 30 minutes are never put in the broker at all | `RetrySweeper`, `DeliveryHandler` |
| At most 5 calls in flight to one endpoint (ASR-07) | `endpoint_slot` rows: a worker must win a free slot (`UPDATE TOP (1) ... READPAST`) before calling. A crashed worker's slot frees itself when its lease expires | `DeliveryHandler` |
| A slow or sick receiver cannot hold the workers (ASR-06) | A delivery that cannot get a slot, or whose endpoint's circuit is open, is **put back with a delay** instead of waiting. Workers never block on a receiver. The slot is taken **before** anything else is read, and how the wait is kept depends on the receiver: if the slots have only just been taken (the receiver is merely full) the wait is a short broker timer; if the oldest attempt has been running for a second or more (the receiver is slow or hung), or the circuit is open, the wait lives **in SQL only** (no message) and the sweeper brings the line back in batches. The wait grows with how long the slots have been held. A hung receiver therefore costs a few database reads, not a stream of broker timers that would slow every other tenant | `DeliveryHandler.DeferAsync`, `BusyStateAsync`, `RetrySweeper` |
| No retry storm (ASR-07) | Backoff 5 s, 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 8 h, 12 h, then 12 h, ±20 % jitter; `Retry-After` honoured as a floor (capped at 12 h); circuit opens after 5 consecutive failures, probes at 30 s doubling to 15 min | `RetryPolicy`, `CircuitBreaker` |
| 72 h retry window, nothing silently dropped (ASR-04) | `deadline_at = occurred_at + 72 h`. Past it the delivery is `dead` (visible, replayable). A paused or auto-disabled endpoint **holds** events for 7 days instead of dropping them | `DeliveryHandler`, `RetrySweeper` |
| Duplicates are harmless (R-02) | Stable `webhook-id` = event id on every attempt and every replay; the receiver's inbox has a unique key on it | `PayloadBuilder`, receiver `HookEndpoint` |
| Reordering is harmless (R-03) | Every event carries a per-aggregate `sequence`; the receiver applies an event only if it is newer than what it holds | receiver `InboxProcessor` |
| Messages are authentic (ASR-08) | Standard Webhooks HMAC, `webhook-id.timestamp.body`, newest signature first, verified against the reference library | `WebhookSigning` |
| Server-side request forgery (ASR-10) | HTTPS only, no credentials in the URL, infrastructure ports refused; the host name is resolved, **every** answer must be public, and the connection goes to exactly the address that was checked; redirects are never followed; reply capped at 4 KB | `SsrfGuard`, `WebhookSender` |
| Tenant isolation, twice (P6, ASR-11) | Tenant comes from the signed token and is copied into `SESSION_CONTEXT`; row-level security enforces it again in SQL. Workers set the tenant **per message**. Cross-tenant jobs use an explicit system mode that only the worker login may set | `TenantSessionInterceptor`, `security.fn_tenant_predicate` |
| Personal data never leaves in a fat payload (ASR-12) | PII event classes are always sent thin; the API refuses a fat endpoint subscribed to one, and the sender forces thin anyway | `EndpointsApi`, `DeliveryHandler` |

## Per-endpoint settings (M3)

Every endpoint carries its own policy, edited on the portal's Delivery, Security and Events tabs and enforced by the same code that delivers (the schedule preview calls it; it is never a second implementation):

| Setting | Values | Enforced in |
|---|---|---|
| Retry schedule | Standard (ADR-07 list, 72 h), Fast recovery (8 attempts in 1 h), Workshop demo (10 min), or **Custom**: attempts 2 to 30, delay growth (x2 to x8, linear, fixed), first delay, longest delay, window up to 72 h | `RetryPolicy`, `RetryParameters` |
| Impossible combinations | A window shorter than the first retry, a cap below the first delay, a window over 72 h | rejected with the reason before saving |
| Circuit breaker | opens after 2 to 50 consecutive failures; probes at 30 s doubling to 15 min | `CircuitBreaker` |
| Auto-disable | N failures in a row within T hours, or failing without a break for D days; N must exceed the circuit threshold | `AutoDisable` |
| Hold period | 1 to 30 days while paused or auto-disabled, then Dead | `DeliveryHandler`, `RetrySweeper` |
| Timeout / concurrency | 1 to 30 s; 1 to 10 requests at once (the slots) | `WebhookSender`, `endpoint_slot` |
| Custom headers | up to 10, 2 KB; secret values encrypted and never returned; reserved names (`host`, `content-*`, `webhook-*`, `traceparent`...) refused; line breaks refused (header injection) | `HeaderRules`, `HeaderStore` |
| Delivery credential | a static API key / bearer header, encrypted, masked everywhere; OAuth client credentials and mutual TLS are **planned** | `RequestFactory` |

## Signing keys (M3)

An endpoint holds **several** keys at once. That is how rotation and algorithm migration work without downtime:

* **HMAC-SHA256** (`v1,...`): one shared secret. Revealing it needs a fresh sign-in (step-up, token `auth_time` within 5 minutes) and is logged.
* **ES256** (`v1a,...`): a P-256 key pair. The private key is stored encrypted and is **never** shown to anyone; the public half is published as a JWKS at `/api/public/endpoints/{id}/jwks.json` (anonymous, rate limited, cacheable), and the `webhook-key-id` header names the keys used. (Standard Webhooks reserves `v1a` for Ed25519, which .NET does not ship; ES256 is the asymmetric option in the base class library.)
* **Both**: one request carries both signatures, HMAC first (some library versions read only the first entry, Spike 0). A receiver verifies whichever it supports, so tenants migrate one receiver at a time.
* **Rotation**: a new key signs immediately; the old one keeps signing next to it for the overlap the person chooses (immediately, 24 h, 7 days). Changing the algorithm works the same way for the algorithm being left.

Changing the mode, rotating and revealing all demand the step-up sign-in. The API checks it, not the screen.

## Testing an endpoint without waiting for business (M3)

`webhook.ping` verifies a new address (2xx makes it active); test events send a realistic sample with an optional negative scenario: a **tampered signature** and a **6-minute-old timestamp** must be refused (4xx) for the test to pass, and a **duplicate** must be acknowledged twice. Both go through the exact signing and egress code of real deliveries, carry `evt_test_...` ids and `"test": true`, create no delivery rows, count in no number and never touch the breaker or auto-disable.

## The delivery state machine

```
pending ──▶ in_flight ──▶ succeeded
   │           ├────────▶ retry_scheduled ──▶ in_flight …      (non-2xx, timeout, network error, 3xx, 429)
   │           ├────────▶ held                                (410 Gone, auto-disable)
   │           └────────▶ dead                                (72 h window exceeded)
   └─▶ held   (endpoint paused / disabled when the event arrived)          held ──▶ pending (resume, rate-limited) | dead (7 days)
dead ──▶ pending (redrive)        succeeded ──▶ pending (replay, same webhook-id)
```

`DeliveryStateMachine` encodes the legal moves and has a unit test per rule.

## What counts as a failure

| Response | Retried | Feeds circuit breaker | Feeds auto-disable |
|---|---|---|---|
| 2xx | done | resets | resets |
| 5xx, 408, timeout, network error | yes | yes | yes |
| 429 | yes (Retry-After) | no | no |
| 3xx | yes (never followed) | no | no |
| 401, 403, other 4xx | yes | no | no (a 401 means a configuration mistake, not a dead server) |
| 410 | no: delivery held, endpoint disabled | no | disables |

## Replay, resume and the delivery log (M4)

* **Replay keeps the `webhook-id`.** A replayed delivery is the same delivery put back in the queue (`attempt_floor` makes its retry schedule start over while the attempt numbers in the log keep counting), so a receiver that already handled the event ignores it and one that missed it finally gets it.
* **A replay job is nothing but a set of deliveries with spaced due times.** There is no second state machine: the sweeper and the normal delivery path (circuit breaker, per-endpoint slots, retries) protect the receiver. Progress is read from the deliveries themselves (`replay_job_id`), cancelling puts back whatever was not yet attempted.
* **Guard rails:** a dry run first, a typed confirmation that names the number of events (`REPLAY 37`), a pace, a cap per tenant per 24 hours (5,000), and *resume* of a paused or switched-off endpoint needs a test that passed in the last 30 minutes, then asks what to do with the held events (all at a steady pace, only the recent ones, or discard: discarded events are kept as dead and can still be replayed).
* **The log stores the request as it was sent, masked.** Credentials, secret headers and key-like headers are replaced before the row is written, so the log can never leak one. The body is rebuilt on demand with the same builder, the version and style of the delivery, and shown only to roles with `payloads:view` (personal-data events are shown without values to roles below developer).
* **Alerts are platform events.** The pre-warnings (25, 50, 75 %) and the switch-off are published through the same outbox as business events (`webhook.endpoint.degraded`, `webhook.endpoint.auto_disabled`), so a tenant can subscribe an endpoint to them and the Notifications module consumes them like any other. One alert per event (a unique key), emails go to the roles of the rule.

## Big results, thin events, versions and the pull API (M5)

* **Claim check.** The Jobs module writes a big file to blob storage **while it streams** (nothing is held in memory; size and SHA-256 are computed on the way) and publishes an event whose canonical data holds an *internal* file reference. Delivery turns that into an envelope of about 600 bytes (size, checksum, content type, an expiring read-only link) **when each attempt is built**, so a retry hours later gets a fresh link. The container and blob name never appear in a delivery log, the pull API or the envelope's own fields.
* **Thin events.** `kyc.completed` carries a verification id and a decision; the personal data lives in one table of the Compliance module. The receiver pulls `GET /v1/kyc/{id}` with an API key that has the `kyc:read` scope, and the platform writes the audit line *before* it returns the data.
* **API keys.** `ihk_{tenant}_{key id}_{secret}`: the tenant is read from the key and set before the lookup, so the lookup runs under that tenant's row-level security and a forged tenant part cannot reach another tenant's row. Only the SHA-256 is stored; the key is shown once, after a fresh sign-in.
* **Versions.** The canonical event is always the current shape. The version an endpoint pinned is applied when the delivery is built; the delivery stores the version of its endpoint **at creation**, so held, retried and replayed deliveries keep it. Filters run on the canonical event, so upgrading never changes *which* events arrive.
* **Reconciliation.** `GET /v1/events` lists the stream oldest first with a cursor. The demo receiver's *Did I miss anything?* walks it, finds what its inbox lacks and puts it into the same idempotent inbox as if it had arrived.

## Observability (M6)

* One trace per business change: the simulator starts an activity for each invoice, the event records its trace id at commit, the relay and the consumers continue it through the broker message properties, and the HTTP client sends the `traceparent` of the attempt. The receiver stores it, so the id in the portal's log, the one in the event table and the one in the receiver's inbox are the same.
* A load run measures what the tenant feels: from the commit time inside the event to its arrival in the receiver's inbox (`LoadTests`, results in `LOAD-RESULTS.md`).

## Known trade-offs (so nobody is surprised)
* **Delivery credential is a static header.** OAuth 2.0 client credentials and mutual TLS (layer 2 of the portal's security model) and JWE payload encryption (layer 4) are designed in the UX spec but not built.
* **No tenant-level throughput quota yet.** Concurrency is capped per endpoint (max 10); a tenant with many endpoints can still occupy many workers.

* **The endpoint's row lock is held for as little as possible.** An attempt that fails reads and rewrites the endpoint's health (failure counter, circuit, warnings), so it takes the row lock first. An attempt that succeeds resets the health to a constant, so it writes the delivery and the attempt first and takes the lock only for one last `UPDATE`. This matters because up to five attempts finish at once and each would otherwise wait for the others' bookkeeping.
* **Broker calls are limited per process** (four at a time for sends, timers and settlements, `BrokerGate`) so that a burst of handlers does not hit the broker in the same instant. In the measurements it changed nothing against the emulator; it is cheap insurance, and the number is a constant to revisit against a real namespace.
* **The database files are sized up front** (512 MB data, 256 MB log, 128 MB growth steps, in the Migrator). A database created with the defaults pauses every writer for seconds each time a small file grows; under a burst that was visible as a stall in the middle of a run.
* **A full endpoint still costs a few database reads per waiting delivery.** A delivery that cannot get a slot is looked at again after its wait (short for a receiver that is merely full, long for a slow one), and every look is a handful of reads. That removes head-of-line blocking and is cheap at demo scale; at very large scale a per-endpoint queue (the worker that frees a slot pulls the next waiting delivery of that endpoint) would be the next step. While a delivery waits in SQL only, the sweeper's poll interval adds to its wait.
* **Fan-out DLQ.** After 10 failed handling attempts a message goes to the broker's dead-letter queue. For events that is no longer a loss: the outbox republishes them. For deliveries the sweeper re-queues them from SQL.
* **Emulator limits.** It caps MaxDeliveryCount at 10 and message TTL at 1 hour, keeps nothing on restart and handles roughly 50 to 160 messages per second. The code does not depend on any of that being different in Azure.
* **The demo timers are compressed.** The seeded endpoint uses the `demo` retry preset (2 s to 30 s, 10 minute window) so a workshop can watch retries; the standard 72 h preset is a one-word switch.

## Notes for deck v3

* The deck says the relay is "single active (app lock)". The implementation runs **several relays at once** (`UPDLOCK, READPAST` + lease), which scales and has no leader to lose.
* Add the outbox `fanned_out_at` safety net to the outbox slide: it is the answer to "what if the broker loses a message?".
* Scheduled messages cannot be cancelled (Spike 0). The delivery worker therefore re-reads SQL and ignores stale messages; say so on the retry slide.
