# Changes for deck v3

Deck v2 (81 slides) was written before the demo existed. Building the demo changed some details and proved others. This is the list of what v3 should say, with the reason, and which real screenshot to put where. Everything below is a *content* change, so it belongs in v3 (`build.py --new-version`), not in v2.

Screenshots are in `docs/screenshots/` (dark and light of each). Names: `m1-*` sign-in and tenants, `m3-*` the endpoint workspace, `m4-*` logs, playground, replay, settings, `m5-*` versions, the receiving systems, the simulator.

## 1. Corrections: where v2 says something the system does differently

| Slide topic (v2) | v2 says | The system does | Action |
|---|---|---|---|
| Outbox → relay | the relay is "single active (app lock)" | several relays run at once; rows are claimed with `UPDLOCK, READPAST` plus a lease, so the relay scales and has no leader to lose | change the text; add "a crashed relay's rows become claimable when the lease runs out" |
| Outbox → relay | (nothing) | `fanned_out_at` on the outbox row: published but never fanned out → the relay republishes | add: it is the answer to "what if the broker loses a message?" (a chaos test restarts the emulator mid-run) |
| Retries | "scheduled messages are the retry timer" | scheduled messages cannot be cancelled (emulator), and the broker only holds short timers; the delivery worker re-reads SQL and ignores a stale message, and the **sweeper** re-queues anything due with no live message | say "the database decides, the broker is a fast path" |
| Backoff schedule | "5 s, 30 s, 3 min, 18 min, 1.8 h, 11 h, then every 12 h" | the shipped *Standard* preset is 5 s, 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 8 h, 12 h over 72 h with ±20 % jitter; *Fast recovery* and a *custom* schedule exist; the schedule preview in the portal is computed by the same code that delivers | change the numbers or say "for example" and show the portal's preview |
| Circuit breaker | "pause 30 s, then one probe; the pause doubles" | the same, capped at 15 min; the threshold is per endpoint (2 to 50, default 5) | add the cap; note it is longer than the 10 min recovery target (see `ASR-PROOF.md`, ASR-04) |
| Auto-disable | "100 consecutive failures in 24 h" | the same, **or** failing continuously for 5 days (a quiet endpoint would never reach 100); pre-warnings at 25, 50 and 75 % are published as events and shown in a bell; events are **held** (7 days) then Dead; 4xx do not count towards switch-off | add the pre-warnings and the held state; show `m4-12-bell`, `m4-04-replay-held` |
| Payload thresholds | claim-check "64 / 128 / 256 KB range" | claim-check classes (export, import report) are always sent as an envelope of about 600 bytes with an expiring link minted **per attempt**; personal-data events are always thin | state the rule per event class, not a size range |
| Versioning | date-based pins | implemented: `2026-06-01` (ends 2027-06-01), `2026-11-01`, `2027-02-01` preview; a delivery keeps the version it was created with; filters run on the canonical event | show `m5-01-versions` |
| Pull / events API | "a cursor-based events API" | `GET /v1/events` (oldest first, cursor), `GET /v1/kyc/{id}`; API keys `ihk_…` with scopes, shown once, hashed, step-up to create | show `m4-09-api-keys` |
| "Silence is a signal" | no events from a normally busy endpoint is an alert | **not built** | mark as "next", or cut |
| Heartbeat / gap detection | mentioned | not built; the receiver's *Did I miss anything?* (reconcile against `/v1/events`) is the working version of gap detection | show `m5-02-receiver-files` / the receiver's reconcile tab |

## 2. Add: things the demo proves that v2 only asserts

1. **Nothing is lost.** The chaos tests kill a delivery worker process and restart the broker in the middle of a run; every event still arrives, none is dead. One slide with the two sentences and the result.
2. **Tenant isolation twice.** The API answers 404 for another tenant's id, and the database returns zero rows for the application login without any `WHERE` (row-level security). Scene 6 of the runbook; show the SQL result.
3. **One trace from commit to inbox.** The same trace id is on the event at commit, on the attempt in the delivery log and in the `traceparent` header the receiver got. Show the dashboard's trace of `simulated invoice lifecycle`.
4. **Claim check with numbers.** 50 MB file, 600-byte webhook, SHA-256 verified by the receiver while streaming; a link with one changed character is refused.
5. **Personal data never travels.** The canary test: a name put into a KYC verification is nowhere in the event store, the broker or any webhook; the details are pulled with a `kyc:read` key and each pull is an audit line attributed to the key.
6. **Replay is a controlled act.** Same `webhook-id`; dry run, typed confirmation that names the number of events, a pace, a daily cap; *resume* needs a passing test first. `m4-07-replay-dry-run`.
7. **Append-only audit in the same transaction**, with the database login unable to update or delete a line.
8. **Load numbers** (laptop, one machine, Service Bus *emulator*): `LOAD-RESULTS.md`. Say them plainly: p95 commit-to-inbox is 0.3 to 0.6 s at 6 to 15 events per second; at 50 events per second the target is **not met here** (a burst has a median of 0.8 s but a p95 of about 25 s, because of a stall of about 12 s in the broker-facing stages, and one delivery attempt itself takes 9 ms), and the emulator, not the design, is the suspected cause (unproven; needs a real namespace). Throughput with 1, 2 and 3 workers: 11, 25 and 36 deliveries per second (about ×1, ×2.2, ×3.2).
9. **The noisy neighbour, split in two.** A receiver that hangs on every call adds nothing to another tenant's latency (measured over four runs: −1.0 s to +2.7 s against a healthy neighbour with the same load; limit 5 s). But any tenant's *burst* delays the others through the shared relay, fan-out and queue (0.5 s alone, about 13 s next to a 120-event burst here). The first is a slide that can claim the result; the second belongs in "next": per-tenant fairness and a tenant quota.

## 3. Add: the portal, as it really is

Replace the prototype mock-ups with the real screens where the deck shows the portal:

| Where | Screenshot |
|---|---|
| Overview of endpoints and health | `m3-01-overview` |
| Create endpoint, live address checks | `m3-02-create` |
| Events, filter builder tested on real events | `m3-03-events-filters` |
| Security: four layers, step-up reveal, rotation | `m3-04-security` |
| Delivery and resilience, schedule preview | `m3-05-delivery` |
| Test dialog with negative tests | `m3-06-test-dialog`, `m4-03-playground` |
| Delivery log and the drawer | `m4-01-logs`, `m4-02-drawer` |
| Replay center: held, resume guide, jobs, dry run | `m4-04` to `m4-07` |
| Alerts, API keys, audit, team | `m4-08` to `m4-11` |
| The receiving systems and the simulator | `m5-02` to `m5-05` |

Two facts about the portal worth a slide: every screen passes the automated accessibility checks in both themes, and a person can create, verify and test an endpoint in a few minutes (the browser test follows the go-live checklist).

## 4. Known gaps to state honestly (or cut from the deck)

OAuth client-credentials and mutual-TLS delivery authentication, JWE payload encryption, a tenant-level throughput quota, a platform-operator console and a broker dead-letter alert, a second transport adapter (the Channels variant was dropped), a Content-Security-Policy, Ukrainian interface strings. Full list with reasons: `ASR-PROOF.md`, last section.

## 5. Notes for the presenter notes (Ukrainian) to refresh

* Scene scripts: `RUNBOOK.md` section 4 is the source for the six demo scenes; the click paths changed from the early plan (the receiver has its own screen, the simulator has a console, the scale-up uses the dashboard's *Start* on `delivery-b` and `delivery-c`).
* The demo's timers are compressed (`demo` retry preset: 10 minutes instead of 72 hours): say so when showing retries.
