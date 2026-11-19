# ASR proof: what is shown, by which test, and where we fall short

This maps every architecturally significant requirement of the register (`architecture/ASR-register.md`) to the automated check that proves it on the real stack, and states plainly what is **not** proven or deviates. Nothing here is a claim without a test or a measurement; where there is none, the row says so.

**How to read the "Proof" column.** *Unit* = `tests/InvoiceHub.UnitTests`, *Arch* = `tests/InvoiceHub.ArchitectureTests`, *IT* = `tests/InvoiceHub.IntegrationTests` (boots SQL Server, Keycloak, the Service Bus emulator, Azurite, Mailpit, the API, two delivery workers, the simulator and two receiving apps), *Browser* = `web/e2e` (Playwright in Chrome, with axe accessibility checks in both themes), *Load* = opt-in `LoadTests` (results in [LOAD-RESULTS.md](LOAD-RESULTS.md)).

Status: **✅ proven** by an automated check on the real stack · **◐ proven in part** (what is missing is stated) · **✗ not built / not shown**.

## Reliability

| ASR | Scenario → response measure | Status | Proof and notes |
|---|---|---|---|
| **01** No loss of committed events | processes and the broker crash right after commit → 100 % delivered | ✅ | IT `ChaosTests`: a delivery worker process is **killed** mid-run, and the broker is **restarted** mid-run (the emulator keeps nothing); every event still arrives, nothing is dead or held. IT `PipelineTests.An_event_the_broker_lost_is_republished_from_the_outbox`, `…An_attempt_abandoned_by_a_crashed_worker_is_recovered_by_the_sweeper`. The event and its outbox row commit in the same transaction as the business change (Billing, Jobs, Compliance commands). *Not tested:* a SQL Server restart in the middle of a run. |
| **02** Duplicates are harmless | a retry after a timeout → same `webhook-id`, 0 duplicate effects | ✅ | IT `PipelineTests.The_same_event_sent_three_times_has_one_effect_and_bad_requests_are_refused`; the receiver's inbox has a unique key on the webhook-id and a sequence guard (`Simulated_invoices_arrive_signed_and_the_receiver_converges_on_the_right_state`). A single replay shows the same id arriving again and being absorbed (IT `PortalAdvancedApiTests.A_single_replay_resends_the_same_webhook_id…`). |
| **03** Bounded latency | 50 events/s → p95 commit→first attempt ≤ 5 s, p99 ≤ 30 s | ◐ **met up to 15 events/s, not at 50** | Load `LoadTests.Latency_is_within_five_seconds_up_to_the_rate_the_broker_emulator_can_carry`: a ladder of offered rates, measured by the receiver from the event's commit time to its inbox ([LOAD-RESULTS.md](LOAD-RESULTS.md)). **At 6, 10 and 15 events/s p95 is 0.3 to 0.6 s** (p99 at most 1.1 s). **At 51 events/s a 6-second burst has p50 0.8 s but p95 25 s**: in the stage table a stall of about 12 s shows up once, in whichever broker-facing stage is working at that moment (relay publish, fan-out, queue), while one delivery attempt takes 9 ms and the hosts use almost no processor time. Our reading, **not proven**: the Service Bus *emulator* pushes back under a burst (its receive and complete rate is 50 to 160 messages per second in total, SPIKE-0, and every event needs two such hops) and the client library then pauses for 10 s. Pre-sizing the database files removed one stall (in the relay) and limiting concurrent broker calls changed nothing. The target therefore stays **unproven**: it needs a real Service Bus namespace to be measured honestly. Laptop numbers, one machine for everything. |
| **04** Survive receiver outages | endpoint down 3 h → zero loss, resumes ≤ 10 min after recovery, > 72 h → Dead with notification | ◐ | Zero loss and recovery: IT `PipelineTests.A_failing_receiver_loses_nothing_events_are_retried_until_it_recovers`, `AsrProofTests.ASR_07…` (backlog drains after recovery). The 72 h window, jitter and `Retry-After`: Unit `RetryPolicyTests`, `ClassifierTests`; expiry to Dead is done by the sweeper and the delivery handler (Unit `StateMachineTests`, `RetryPolicyTests`); there is **no integration test that lets a real 72 h window run out** (the windows are compressed in tests). Notification when auto-disabled: IT `PortalAdvancedApiTests.A_failing_endpoint_warns…` (bell + email). **Deviation:** with production timers the circuit's probe wait grows to **15 minutes**, so recovery after a long outage can take up to 15 min, not 10. The operator can resume at once (Replay center); lowering the cap to 5 min is a one-line change we recommend. 3 h was not run in real time; all timers are compressed in tests. |
| **14** Recovery tooling | 800 dead → redrive with one action; DLQ depth alert | ◐ | Replay jobs with a dry run, a typed confirmation and a pace; replay of a single event; guided resume with discard / only-recent / deliver-all: IT `PortalAdvancedApiTests` (`A_replay_job…`, `A_single_replay…`, `A_paused_endpoint_holds_events_and_resume…`), Browser `advanced.spec.ts`. **Not built:** an operator-side alert on broker dead-letter depth (the platform operator uses the Aspire dashboard; there is no operator console). |

## Scalability and isolation

| ASR | Scenario → response measure | Status | Proof and notes |
|---|---|---|---|
| **05** Fan-out and throughput | one event, 1,000 endpoints → originating call unchanged, throughput ~linear with workers | ◐ | The originating call only writes the event and its outbox row; fan-out is a separate stage. Throughput with 1, 2 and 3 workers (a receiver that answers after 1 s, 13 endpoints, 780 deliveries): Load `LoadTests.Throughput_grows_with_the_number_of_delivery_workers`, **11.3, 24.5 and 36.3 deliveries per second, about ×1.0, ×2.2 and ×3.2**, so roughly linear at this scale ([LOAD-RESULTS.md](LOAD-RESULTS.md); an earlier run, before the database files were pre-sized, measured ×1.4 and ×1.7). **Not shown:** 1,000 endpoints on one event (a tenant is limited to 50 endpoints in this build; the scale test uses 13). |
| **06** Noisy neighbour | one endpoint hangs 15 s on every call → other tenants' p95 degrades ≤ 5 s | ◐ **the hang is contained; a neighbour's burst is not** | IT `AsrProofTests.ASR_06_…` runs the same Fabrikam traffic (90 events) three times: alone, next to a busy but healthy Contoso (40 invoices, 120 events), and next to a Contoso whose receiver answers after 15 s (every attempt times out). **The hang adds nothing: Fabrikam's p95 is the same next to a hanging neighbour as next to a healthy one (a difference of −1.0 s to +2.7 s over four runs, limit 5 s).** The slots (at most 5 in flight per endpoint), the timeout, the circuit breaker, and keeping a slow receiver's waiting in SQL instead of in the broker (`DeliveryHandler`) do that. **But** the busy neighbour itself, hanging or not, takes Fabrikam from p95 0.5 s (alone) to about 13 s: the shared relay, fan-out and queue are one line for every tenant, and in this environment the broker stage stalls under a burst (see ASR-03). No per-tenant fairness exists in that shared stage yet (a tenant quota is **not built**), so a tenant's own burst can delay others by seconds when the broker is the bottleneck. |
| **07** No retry storms | 503 for 1 h with 10,000 pending → ≤ 5 in flight, circuit opens after 5, `Retry-After` honoured | ✅ | IT `AsrProofTests.ASR_07_…` (180 deliveries waiting for a dead receiver: only a handful of requests reach it, then the backlog drains after recovery); the per-endpoint slots (≤ 10 configured, 5 default) and the circuit breaker: Unit `CircuitBreakerTests`, `ClassifierTests`; IT `PipelineTests`. Tenant-level quota is **not built** (see below). |

## Security

| ASR | Scenario → response measure | Status | Proof and notes |
|---|---|---|---|
| **08** Integrity | replay after 10 min / alter one byte → 100 % rejected | ✅ | Unit `SigningTests` (test vectors, tolerance); IT `PortalApiTests` negative tests (tampered signature, six-minute-old timestamp) against the real receiver, in the portal's Playground (Browser). |
| **09** Secrets | rotate under load → 0 failed verifications; revealable only after step-up | ✅ | IT `AsrProofTests.ASR_09_…` rotates the key in the middle of a run and then switches the receiver to the new secret only: 0 rejected, 0 dead. Reveal and rotate need a fresh sign-in: IT `PipelineTests`/`PortalApiTests` (a plain token is refused), `PortalAdvancedApiTests.Revealing_a_secret_needs_a_real_fresh_sign_in…` uses a real authorization-code sign-in and checks that the audit line has no secret. Secrets are AES-GCM encrypted at rest (master key from configuration; Key Vault in production). |
| **10** SSRF | `169.254.169.254` or a private IP, also after DNS rebinding → blocked at registration and delivery | ✅ | Unit `SsrfGuardTests` (private v4/v6, metadata, a rebinding fake resolver; the connection is made to the pinned address); IT `PortalApiTests.Checking_a_URL…` (live checks), registration refused by the API. The dev-only allow-list for local receivers is honoured only in Development and shown in the portal. |
| **11** Tenant isolation | guessed ids → 404; a missing `WHERE` returns 0 rows | ✅ | IT `RowLevelSecurityTests` (as `api_user` and `worker_user`: no rows without context, no cross-tenant read, writes for another tenant blocked, the API login cannot switch on system mode), `ClaimCheckAndPullTests.One_tenant_cannot_see_open_or_change_the_other_tenants_endpoint…`, `PipelineTests.Tenants_cannot_see_or_touch_each_others_…`; Arch: every table with a `tenant_id` has filter and block predicates. |
| **12** PII minimisation | `kyc.completed` → 0 PII bytes in the webhook body, broker message, blob, log line | ◐ | IT `ClaimCheckAndPullTests.An_identity_check_travels_as_ids_only…`: a canary name never appears in the receiver's bodies, in `events.event`, or in any recorded response; the webhook is thin (`verification_id` only); the receiver pulls with a `kyc:read` key and every read is audited; a key without the scope gets 403 and another tenant's key 404. Arch: personal-data columns exist only in the Compliance schema. Broker messages carry ids only by construction (`EventEnvelope` has no data). *Not machine-checked:* that no log line ever contains personal data (code review only). |

## Operability, modifiability, interoperability, cost, compliance

| ASR | Scenario → response measure | Status | Proof and notes |
|---|---|---|---|
| **13** Traceability | "invoice 4821 never arrived" → support answers ≤ 5 min from the portal | ✅ | The delivery log searches by invoice id, webhook id or delivery id and shows every attempt with status, latency, response snippet and trace id: IT `PortalAdvancedApiTests.The_delivery_log_filters…`. One trace id from the business commit to the receiver's inbox: IT `AsrProofTests.ASR_13_…` (the event's recorded trace, the attempt's trace in the portal and the `traceparent` the receiver received are the same). |
| **15** Versioning | additive / breaking change → old version supported ≥ 12 months, endpoints pin | ✅ | Three dated versions (`2026-06-01` ends 2027-06-01, `2026-11-01` current, `2027-02-01` preview), projected when a delivery is built; a delivery keeps the version it was created with: IT `ClaimCheckAndPullTests.A_delivery_keeps_the_payload_version_it_was_created_with…`; the diff on a real sample: Browser `m5.spec.ts`; Unit `ClaimCheckAndVersionTests`. |
| **16** Pluggable transport | swap the broker → 0 changes in domain code and `Webhooks.Core` | ◐ | The ports (`IEventTransport`, `IDeliveryQueue`, `IClaimCheckStore`) and the isolation of the adapters are enforced by Arch tests (the adapters know only the shared kernel, `Webhooks.Core` touches only the base class library). **Not shown:** a second adapter and "the same suite on both": the Channels+DB variant was dropped when ADR-04 was revised to broker-only. |
| **17** Interoperability | an off-the-shelf Standard Webhooks library verifies unmodified | ✅ | Unit `SigningTests` with the `StandardWebhooks` NuGet package (1.0.36, which verifies the signature list correctly; note 1.1.x only checks the first signature, so HMAC entries are listed first). |
| **18** Cost / size | 50 MB result → body ≤ 64 KB inline, otherwise claim check; queue message < 256 KB | ✅ | IT `ClaimCheckAndPullTests.A_50_megabyte_export…`: the webhook is under 1 KB (about 600 bytes), the receiver downloads the 50 MB file from Azurite and its SHA-256 matches; a tampered or unsigned link is refused; no internal file reference in the envelope, the delivery log or the pull API. Messages on the broker are ids only. |
| **19** Auditability | an admin changes an endpoint URL → audit row with actor, before/after, same transaction, immutable | ◐ | The audit line is written in the **same transaction** as the change (`AuditedSave`, `TenancyAudit`); both application logins are **denied** UPDATE and DELETE on it: IT `PortalAdvancedApiTests.Every_change_leaves_an_audit_line…` (including the SQL attempts as `api_user` and `worker_user`), Arch `The_audit_log_is_append_only…`. **Deviations:** a line records *what* changed and the new safe values (never secrets, URLs only as host), not a full before/after copy; "immutable for 1 year" is enforced as *append-only*, but no retention job exists, so nothing is ever deleted. |

## Portal requirements (ASR-20 to 25)

| ASR | Response measure | Status | Proof and notes |
|---|---|---|---|
| **20** First test within ten minutes | a developer creates, verifies, tests and goes live | ✅ | Browser `endpoints.spec.ts` follows the go-live checklist end to end with a real step-up pop-up (a scripted run takes well under a minute; the ten minutes is the human budget). |
| **21** Safe defaults, validation before save | invalid configuration rejected with the reason | ✅ | IT `PortalApiTests` (settings validated before they are saved), the live URL checks, Unit `WebhookCoreSettingsTests`. |
| **22** Alert lead time | pre-warnings before anything is held | ✅ | Warnings at 25, 50 and 75 % of the auto-disable threshold, then the switch-off, once each; in the bell and (for the switch-off) by email: IT `PortalAdvancedApiTests.A_failing_endpoint_warns_at_25_50_75_percent…`, Browser `advanced.spec.ts`. |
| **23** Log search | indexed, p95 < 2 s on 100,000 attempts | ◐ | Search is by ids and names only (no payload full text, on purpose: it would store personal data twice), with keyset pagination and covering indexes. **Not measured:** the 100,000-attempt response time. |
| **24** Redaction | credentials and secrets never shown | ✅ | The log stores request headers already masked; a credential or a secret header is never returned; payload bodies need the `payloads:view` permission and personal-data events are shown without values to roles that are not developers or above: Unit `PortalAdvancedTests`, IT `PortalAdvancedApiTests.The_delivery_log_filters…`. |
| **25** Accessibility | WCAG 2.1 AA | ✅ | axe in **both** themes on every screen of the portal and on both receiving apps and the simulator console: Browser `endpoints.spec.ts`, `advanced.spec.ts`, `m5.spec.ts`. Mobile layout was not checked. |

## General requirements

| Requirement | Status | Where |
|---|---|---|
| Health checks (live / ready), readiness gates | ✅ | `/health/live`, `/health/ready` on every host; the AppHost waits for them. |
| Graceful drain | ✅ | In-flight attempts get 25 s on shutdown, then are handed back (IT `ChaosTests` kills one without notice). |
| Retries and timeouts everywhere | ✅ | EF retry + deadlock handling, an attempt timeout per endpoint, a response cap of 4 KB, resilient HTTP defaults. |
| Problem responses, config validation at start | ✅ | RFC 9457 problems with stable codes; options validated on start. |
| Rate limiting | ◐ | Per client address on public and test-send routes. **Not built:** a per-tenant or per-key quota. |
| Security headers | ◐ | `nosniff`, no framing, no referrer, `no-store` on the API. **Not set:** a Content-Security-Policy on the portal. |
| Deterministic tests | ✅ | `TimeProvider` everywhere; the retry preset and breaker timers are compressed for tests only. |

## Principles P1 to P8

All of them are visible in the code and enforced where possible: **P2** every event is published through `IEventPublisher` inside the caller's own transaction, so the business change, the event and its outbox row commit together (a code convention, not a mechanical rule); **P3** SQL is the source of truth and the broker only carries ids; **P5** every route requires authorization unless on the anonymous allow-list (IT `EndpointSecurityTests`); **P6** the tenant is enforced twice, in the API and by row-level security; **P8** one trace id end to end (above).

## What the suite is

At the time of writing (see the last section for the exact counts of the final run): unit, architecture, integration (real stack), portal unit and browser tests, plus the opt-in load run. `docs/DELIVERY-DESIGN.md` explains the design behind the guarantees; `docs/RUNBOOK.md` shows each of them live.

## The final run (2026-11-13)

| Suite | Result |
|---|---|
| Unit (`InvoiceHub.UnitTests`) | 264 passed |
| Architecture (`InvoiceHub.ArchitectureTests`) | 28 passed |
| Portal unit (vitest) | 74 passed (7 files) |
| Integration on the real stack (`InvoiceHub.IntegrationTests`) | 69 tests: 67 passed, 2 skipped (the opt-in load runs) |
| Load (`LOAD=1`, `LoadTests`) | 2 passed; numbers in [LOAD-RESULTS.md](LOAD-RESULTS.md) |
| Browser (Playwright with axe, both themes) | 46 passed, 20 skipped (the screenshot specs, run with `-Shots`) |
| Screenshots | 56 images in `docs/screenshots/` |

Things the run taught us, kept here because they changed the code: a database created with default file sizes pauses all writers when a small file grows (the Migrator now sizes the files); a hanging receiver is contained but a neighbour's burst is not (ASR-06); the broker emulator stalls for about 12 s under a 50 events/s burst (ASR-03); an endpoint switched off by one attempt was switched off a second time by a concurrent one and the owner got two alerts (fixed in `DeliveryHandler`, covered by `PortalAdvancedApiTests.A_failing_endpoint_warns…`); three compressed test timers had to be left at production values for load runs (`Demo:RealTimers`).

## Not built, on purpose or for time

OAuth client-credentials and mutual-TLS delivery authentication, JWE payload encryption, a tenant-level throughput quota and per-tenant fairness in the shared relay, fan-out and queue stages (see ASR-06), Ukrainian interface strings, an operator console, a second transport adapter, a Content-Security-Policy, a 100,000-attempt log-search measurement, a 1,000-endpoint fan-out run, the 50 events/s latency target on a real Service Bus namespace (see ASR-03; only the emulator was available). Each is a conscious gap, listed here so that no slide promises it.
