# InvoiceHub: the webhook platform demo

A real, runnable multi-tenant webhook platform for the workshop *Third-Party Integrations: Webhooks in Focus*. .NET 10 modular monolith, SQL Server with row-level security, Keycloak sign-in, React tenant portal, started by one Aspire command.

**Status: complete (M0 to M6).** The platform is built and runs end to end: sign-in and tenants (M1), the real delivery pipeline with outbox, broker, fan-out, workers and a receiving app (M2), the whole tenant portal (M3 and M4: overview, add endpoint, the endpoint workspace, delivery logs, playground, replay center, alerts with a bell and email, API keys and IPs, audit log, team and roles), big results as claim checks in blob storage, thin personal-data events with a scoped, audited pull API, payload versions, two receiving systems with their own screens, and a simulator console (M5), and the proof: chaos, ASR and load tests (M6).

Where to read next: **[docs/TECH-DESIGN.md](docs/TECH-DESIGN.md)** (what the demo is made of, how the parts talk, how the modular monolith works and what it means for microservices), **[docs/DEMO-NOTES.md](docs/DEMO-NOTES.md)** (presenter's crib sheet: sign in as Contoso, create an endpoint and an API key, the security tour), **[docs/RUNBOOK.md](docs/RUNBOOK.md)** (ports, passwords, start and reset, and the six demo scenes click by click), **[docs/ASR-PROOF.md](docs/ASR-PROOF.md)** (every requirement, the test that proves it, and where we fall short), [docs/LOAD-RESULTS.md](docs/LOAD-RESULTS.md) (laptop numbers), [docs/DELIVERY-DESIGN.md](docs/DELIVERY-DESIGN.md) (how delivery works and why)

**Final run (2026-11-13):** 264 unit, 28 architecture, 74 portal unit, 67 integration tests on the real stack (plus 2 opt-in load runs) and 46 browser tests with accessibility checks in both themes, all green. Two requirements are **not fully proven** and the documents say so: the 50 events/s latency target (met up to 15 events/s; the broker *emulator* stalls under a bigger burst) and fairness between tenants in the shared pipeline stages (a hanging receiver is contained, a neighbour's burst is not). See [docs/ASR-PROOF.md](docs/ASR-PROOF.md).

## Run it

Prerequisites: .NET SDK 10.0.103, Docker (about 8 GB for Docker Desktop: two SQL Server containers, Keycloak and the Service Bus emulator), Node 22, Chrome (for the browser tests), the ASP.NET dev certificate trusted (`dotnet dev-certs https --trust`).

```powershell
cd demo\web ; 
npm install ; 
cd ..          
# once
dotnet run --project src\AppHost\InvoiceHub.AppHost --launch-profile http
```

The first start pulls images and takes a few minutes. The Aspire dashboard opens at <http://localhost:15180> (logs, traces, metrics, resource health).

| What | URL |
|---|---|
| **Portal** (start here) | <http://localhost:5173> |
| API | <http://localhost:5100> (OpenAPI at `/openapi/v1.json`, health at `/health/ready`, the pull API at `/v1/events` and `/v1/kyc/{id}`) |
| **Contoso's receiving system** (inbox, invoices, verified files, identity checks, failure switch) | <http://localhost:5174> (its API: <http://localhost:5201/api/stats>) |
| **Fabrikam's receiving system** (a second, independent tenant system) | <http://localhost:5175> (its API: <http://localhost:5202/api/stats>) |
| **Simulator console** (plays business activity: traffic, exports, imports, KYC, renewals) | <http://localhost:5400> |
| Mailpit (every alert email lands here) | <http://localhost:8025> |
| Keycloak (admin: see the dashboard, resource `keycloak`) | <https://localhost:8085> |
| Aspire dashboard (logs, traces, metrics; start `delivery-b` and `delivery-c` to scale 1 to 3 workers) | <http://localhost:15180> |

### Sign in

On the portal choose an organisation, then enter credentials on the Keycloak page. Every demo account uses the password **`Passw0rd!2026`**:

| Organisation | Accounts (role = name before the @) |
|---|---|
| Contoso Ltd | `owner@contoso.test`, `admin@contoso.test`, `developer@contoso.test`, `support@contoso.test`, `viewer@contoso.test` |
| Fabrikam Inc | the same five at `@fabrikam.test` |

Or choose **Register your organisation** to create a third tenant and its owner (no email verification in the demo).

Picking one organisation and signing in with an account of the other is refused with an explanation: that is the tenant check of principle P6.

### Your first ten minutes in the portal

Sign in as `developer@contoso.test` (organisation Contoso Ltd) and follow the go-live checklist the portal gives you:

1. **Add endpoint**: pick the *ERP / accounting* template, name it, and paste `http://localhost:5201/hooks/my-test` as the address (the live checks explain every line; the local receiver is allowed only because this is a development machine).
2. **Verify**: it fails with advice, because the receiving application does not know the signing secret yet. On the **Security** tab choose **Reveal**: a small window asks for your password again (step-up sign-in), then shows the secret.
3. **Tell the receiver**: `curl -X PUT http://localhost:5201/api/trust -H "Content-Type: application/json" -d '{"secrets":["whsec_..."]}'` (this is what the ERP team does in their own configuration).
4. **Verify again**: the endpoint goes active. **Send test** runs a realistic event, a tampered signature (must be refused), a 6-minute-old timestamp (must be refused) and a duplicate.
5. Try **Both** signatures, **ES256** (the receiver trusts `PUT /api/trust {"jwksUrl": "<the JWKS address on the Security tab>"}` and needs no secret), **Rotate**, the **filter builder** (tested against your latest real events) and the **retry schedule preview** on the Delivery tab.

Other roles: `admin@contoso.test` may delete and reads the **audit log** and manages the **team**, `support@contoso.test` looks at deliveries and payloads and can replay a single event, `viewer@contoso.test` sees everything and can change nothing, and Fabrikam users cannot see or open Contoso's endpoint at all.

Then explore the rest: **Delivery logs** (open a row: the request as sent, the answer, the attempts, *copy as cURL*), **Playground** (edit the event data, pick a payload version, run the negative tests), **Replay center** (held events, the resume guide, a replay job with a dry run and a typed confirmation), **Settings** (alerts by role, API keys, audit log, team) and the **bell** at the top.

### See a webhook travel

With the stack running, make some business happen for Contoso and watch it arrive at the receiver:

```powershell
# 5 invoices a second for a few seconds: generated, then mostly paid, some overdue, some failed payments first
curl -X POST http://localhost:5400/simulator/invoices -H "Content-Type: application/json" `
     -d '{"tenant":"contoso","count":20,"perSecond":5}'

curl http://localhost:5201/api/stats      # events received, processed, duplicates absorbed, stale (reordered) events ignored
curl http://localhost:5201/api/invoices   # what the ERP now believes about each invoice
```

Then make the receiver sick and watch the platform cope (nothing is lost, delivery resumes by itself):

```powershell
curl -X PUT http://localhost:5201/api/chaos -H "Content-Type: application/json" -d '{"mode":"fail500"}'   # also: slow, drop, gone, unauthorized, throttle
# ... run the simulator again, watch retries and the circuit breaker in the Aspire dashboard (logs + traces) ...
curl -X PUT http://localhost:5201/api/chaos -H "Content-Type: application/json" -d '{"mode":"ok"}'
```

Sign in to the portal as `developer@contoso.test` and open the API (`/api/portal/endpoints`, `/api/portal/deliveries`) for the platform's own view: attempts, status codes, trace ids. How it all works: [docs/DELIVERY-DESIGN.md](docs/DELIVERY-DESIGN.md).

### Start from scratch

SQL Server and Keycloak keep their data in Docker volumes so registered tenants survive a restart. To reset:

```powershell
docker volume rm invoicehub-sql invoicehub-keycloak
```

or run with `--Demo:PersistData=false`. If you change `infra/seed/seed.json`, run `python infra/keycloak/generate_realm.py` and reset the Keycloak volume.

## Tests

```powershell
dotnet test --project tests\InvoiceHub.UnitTests\InvoiceHub.UnitTests.csproj           # signing (HMAC, ES256, the reference library), retry, SSRF, breaker, filters, versions, claim-check envelope, API keys, masking
dotnet test --project tests\InvoiceHub.ArchitectureTests\InvoiceHub.ArchitectureTests.csproj   # module boundaries, RLS on every table, append-only audit, personal data only in Compliance
dotnet test --project tests\InvoiceHub.IntegrationTests\InvoiceHub.IntegrationTests.csproj     # boots the whole platform in containers; pipeline, chaos, portal API, replay, audit, API keys, claim check, KYC, versions, ASR scenarios (about 6 min once the images are pulled)
cd web\portal ; node ..\node_modules\vitest\vitest.mjs run                                   # portal unit tests
powershell -File tests\run-e2e.ps1                                                           # starts everything, drives Chrome through the real flows (real step-up pop-up, axe in both themes)
$env:LOAD = "1"; dotnet test --project tests\InvoiceHub.IntegrationTests\InvoiceHub.IntegrationTests.csproj --filter-class InvoiceHub.IntegrationTests.LoadTests   # opt-in load run (about 12 min), writes docs\LOAD-RESULTS.md
```

Note on npm: the folder name of this repository contains `&`, which breaks the `.cmd` shims that npm and npx create on Windows. The portal's npm scripts therefore call node directly (`node ../node_modules/vite/bin/vite.js`). Use the scripts, not `npx`.

## Layout

```
src/AppHost                 Aspire topology (SQL, Keycloak, Service Bus emulator, Azurite, Mailpit, migrator, API, delivery workers, simulator, two receivers and their screens, portal)
src/ServiceDefaults         OpenTelemetry, health checks, resilient HttpClient defaults
src/BuildingBlocks          tenant context, RLS session interceptor, raw-SQL helper, SQL migration runner, Keycloak/JWT auth, secrets envelope, messaging ports
src/Infrastructure          InvoiceHub.Messaging.ServiceBus (the broker) and InvoiceHub.Storage.AzureBlob (claim-check files): adapters behind BuildingBlocks ports
src/Modules/Tenancy         tenants, registration, Keycloak facade, team and roles, API keys, /api/me
src/Modules/Events          event catalogue (42 events), canonical event store, transactional outbox, relay
src/Modules/Webhooks        endpoints, subscriptions, secrets, fan-out, delivery workers, sweeper, portal API (+ Webhooks.Core: pure logic)
src/Modules/Audit           the append-only audit log (written in the same transaction as the change)
src/Modules/Notifications   alert rules by role, the bell, email
src/Modules/Jobs            exports and vendor imports (big files, claim-check events)
src/Modules/Compliance      identity verifications (the only place personal data is stored) and the audited KYC pull
src/Modules/Billing         the simulated invoicing domain that produces real events
src/Hosts                   Api (portal API, pull API), Delivery (relay, fan-out, delivery workers, sweeper, job runner; scale out), Simulator (demo tooling and console), Migrator (one-shot database job)
src/Receiver                the tenants' receiving app (run twice): verify, inbox, sequence guard, claim-check download, KYC pull, reconcile, chaos switch, own database
web/portal                  React 19 + Vite + TypeScript tenant portal
web/receiver                the receiving systems' own screen (one app, run twice)
web/e2e                     Playwright browser tests
infra/seed                  demo tenants, users, event catalogue, seeded endpoint (single sources of truth)
infra/keycloak              realm generator, generated realms (normal + test), login theme
infra/servicebus            emulator topology (topic events, queue deliveries)
tests                       unit, architecture, integration (real stack), scripted end-to-end
docs                        RUNBOOK.md, ASR-PROOF.md, LOAD-RESULTS.md, DELIVERY-DESIGN.md, DECK-V3-CHANGES.md, SPIKE-0.md, MODULES.md, screenshots/
```

## Design rules that are enforced by tests

- A module reaches other modules only through their `*.Contracts` assembly (architecture tests).
- Every table with a `tenant_id` column has a row-level security policy with a block predicate (architecture test over the SQL scripts) and the application logins cannot step around it (integration tests as `api_user` and `worker_user`).
- Every API route requires authorization unless it is on the explicit anonymous allow-list (integration test).
- The tenant comes from the signed token only; the portal's `X-Selected-Tenant` header is a consistency check and can never select a tenant.
- Demo seed data and the Keycloak realm are generated from one file and cannot drift (unit tests).
- A module owns exactly one SQL schema and never references another module's tables (architecture test over the SQL scripts).
- The delivery logic (`Webhooks.Core`) touches nothing but the base class library, the broker adapter knows no module, and the receiver knows nothing of the platform but the public signing helper (architecture tests).
- The pipeline survives a sick receiver, a paused endpoint, a crashed worker and a message lost by the broker, with nothing lost and no duplicate effect (integration tests on the real stack).
- The audit log is append-only for both application logins (SQL permissions, architecture test) and personal-data columns exist only in the Compliance schema (architecture test).
- A hanging neighbour does not slow another tenant, a failing receiver is not hammered, a key rotation under load fails no verification, and one trace id runs from the commit to the receiver's inbox (`AsrProofTests`).