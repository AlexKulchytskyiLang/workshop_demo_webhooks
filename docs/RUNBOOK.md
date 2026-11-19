# Runbook: running the demo, and the six scenes of the workshop

One command starts everything. This page is for the person presenting: what runs where, how to start and reset it, the click-by-click script for each scene, and what to do when something does not behave.

## 1. Start it

```powershell
cd demo\web ; npm install ; cd ..                                                    # once
dotnet run --project src\AppHost\InvoiceHub.AppHost --launch-profile http
```

Wait until the Aspire dashboard (<http://localhost:15180>) shows every resource as running or finished (the first start pulls images and takes a few minutes; a normal start about one minute). The order is decided by the AppHost, you do not have to do anything:

`sql` → `migrator` (schemas, security policies, seed) → `keycloak`, `servicebus`, `storage` (Azurite), `mailpit` → `api`, `delivery`, `simulator`, the two receivers → the portal and the two receiver screens.

Docker needs about 8 GB. Everything is on one machine.

## 2. What runs where

| What | Address | Notes |
|---|---|---|
| **Tenant portal** | <http://localhost:5173> | the product. Start here |
| Platform API | <http://localhost:5100> | `/health/ready`, `/openapi/v1.json`; the pull API is `/v1/events` and `/v1/kyc/{id}` |
| **Contoso's receiving system** (screen) | <http://localhost:5174> | live inbox, invoices, verified files, identity checks, “did I miss anything?”, failure switch |
| Contoso's receiving system (API) | <http://localhost:5201> | `/api/stats`, `/api/inbox`, `/api/chaos` |
| **Fabrikam's receiving system** (screen) | <http://localhost:5175> | a second, independent tenant system |
| Fabrikam's receiving system (API) | <http://localhost:5202> | own database |
| **Simulator console** | <http://localhost:5400> | plays the platform's customers: traffic, exports, imports, KYC, renewals |
| Mailpit (alert email) | <http://localhost:8025> | every email the platform sends lands here and nowhere else |
| Keycloak | <https://localhost:8085> | admin credentials: see the resource `keycloak` in the dashboard |
| Aspire dashboard | <http://localhost:15180> | logs, **traces**, metrics, resource health; start `delivery-b` and `delivery-c` here |
| Azurite (blob storage) | random port, see the dashboard | the claim-check files |

**Passwords.** Every demo account uses `Passw0rd!2026`. Accounts: `owner@`, `admin@`, `developer@`, `support@`, `viewer@` at `contoso.test` and at `fabrikam.test`. The SQL password is generated once and kept in the AppHost's user secrets (`dotnet user-secrets list --project src\AppHost\InvoiceHub.AppHost`).

**Demo API keys.** Both receiving systems pull from the platform with a key that is seeded from `infra/seed/seed.json` (fixed on purpose: the receivers are configured from the same file, so there is no manual step). They are **demo-only**; real keys are created in the portal and shown once.

## 3. Reset

* Same data again: stop with Ctrl+C and start again (SQL, Keycloak and Azurite keep their volumes).
* Clean slate: `docker volume rm invoicehub-sql invoicehub-keycloak invoicehub-azurite`, or start with `-- --Demo:PersistData=false`.
* The receivers forget everything with the button *Forget everything* on their last tab (or `POST /api/reset`).
* If you change `infra/seed/seed.json`: `python infra/keycloak/generate_realm.py`, then reset the Keycloak volume.

## 4. The six scenes

Open before you start: the portal (signed in as `developer@contoso.test`), Contoso's receiving system (5174), the simulator console (5400), and the Aspire dashboard in a tab.

### Scene 1: the happy path, then more workers under load

*Claim: a business change is delivered within seconds, and capacity is added by adding workers.*

1. In the portal open **Delivery logs**. In the simulator console set **Per second** to 5 and the count to 60, and start the traffic (about 15 events a second, the rate the demo's broker emulator carries comfortably, with a delay of well under a second). Watch the rows appear, and the *Received* card on Contoso's screen climb. *Do not use the 40-a-second burst button for this scene*: 120 events a second is beyond what the emulator can pass and you would be demonstrating the emulator (see the note below).
2. Open one row: the request exactly as sent (signature shortened, secrets hidden), the answer, the attempts, the event. *Copy as cURL* reproduces it.
3. The protection first: on Contoso's screen choose **Break things & trust → Slow**, delay **1** second, and start the traffic again. The *Received* card climbs at about **5 a second and no faster**: at most 5 requests are in flight to one endpoint, however many workers there are. That limit is what protects a slow customer (and it is why one endpoint cannot show scale-out).
4. Scale-out is the same mechanism with many endpoints. In the **Aspire dashboard** start `delivery-b` and `delivery-c`: three workers now compete for the same queue with no configuration, and the dashboard shows all three handling attempts. The measured effect across 13 endpoints and a 1 s receiver is in the table of [LOAD-RESULTS.md](LOAD-RESULTS.md): about 11, 25 and 36 deliveries a second with 1, 2 and 3 workers.
5. Open the dashboard's **Traces**, find `simulated invoice lifecycle`: one trace from the commit through the outbox relay, fan-out, the delivery attempt, to the receiver's inbox.

Numbers measured on a laptop are in [LOAD-RESULTS.md](LOAD-RESULTS.md). Say out loud what they show: up to about 15 events a second the delay is a fraction of a second; at 50 a second the *emulator* stalls for about 12 seconds in a burst (a real Service Bus namespace is the place to prove the 50-a-second target, and we have not).

### Scene 2: the receiver is down, nothing is lost

*Claim: an outage on the customer's side costs them time, never events.*

1. On Contoso's screen choose **Server error (500)**. Start a burst.
2. In the portal Overview the endpoint turns *Degraded*, then *Circuit open*: after five failures in a row the platform stops hammering it and sends occasional probes. The **bell** shows the pre-warnings (25, 50, 75 % of the way to automatic switch-off) and an email arrives in Mailpit when it is switched off.
3. Open the endpoint → **Delivery & resilience** and lower *auto-disable* to 8 failures if you want to show the switch-off quickly. Held events wait in the **Replay center → Held**.
4. Choose **Healthy** on Contoso's screen. In the Replay center press **Resume…**: the guide needs a test that passes, then you choose *deliver all at a steady pace*. The *Received* count ends at exactly what was sent.

### Scene 3: duplicates and out-of-order

*Claim: delivery is at-least-once; the receiver makes it exactly-once in effect.*

1. On Contoso's screen choose **Drop the answer**: the receiver processes each event and never replies, so the platform sends it again. Start a burst.
2. *Duplicates absorbed* climbs; *Invoices* still shows each invoice once with the right final status. The idempotency key is the `webhook-id` (the inbox's unique key); the sequence guard ignores an event older than what it already holds (*Stale ignored*).
3. In the portal's Playground send the negative tests: tampered signature and a six-minute-old timestamp are refused; “send twice with the same id” is acknowledged twice and acted on once.

### Scene 4: a 50 MB export as a claim check

*Claim: big results never travel inside a webhook.*

1. In the simulator console press **Request export** with 50 MB.
2. On Contoso's screen open **Files & identity checks**: the file appears with its size, its checksum and the verdict **verified** (it was downloaded and hashed while streaming).
3. In the portal's **Delivery logs** open the `export.completed` row: the body is about 600 bytes (size, checksum, an expiring link); the log shows “a new link is minted for every attempt”. The location of the file inside the platform is not in it.
4. Optional: copy the link from the receiver's inbox detail, change one character of `sig=` and open it: refused.

### Scene 5: identity checks as thin events, pulled with an API key, audited

*Claim: personal data never travels in a webhook.*

1. Press **Complete verifications** in the simulator console. On Contoso's screen **Identity checks** fills in: the receiver pulled the details itself.
2. Open the `kyc.completed` row in the inbox: the body holds only `verification_id`.
3. In the portal sign in as `owner@contoso.test` → **Audit log**, area *Everything*: one line per pull, attributed to the **key** (never a person), without any personal data.
4. **API keys & IPs → Create API key** (a fresh sign-in is required, the key is shown once) with only `events:read`, then call `GET /v1/kyc/{id}` with it: 403. The scope is the permission.

### Scene 6: tenant isolation

*Claim: one tenant can never reach another's data, even by guessing ids, even if the code is wrong.*

1. Sign in as `owner@fabrikam.test`. Open `http://localhost:5173/endpoints/0198a000-0000-7000-8000-0000000001e1/events` (Contoso's endpoint): “does not exist, or it belongs to another organisation”. It is not a permission error: the platform does not even confirm the id exists.
2. Sign in on the portal as a Fabrikam user but choose Contoso on the organisation screen: refused with an explanation.
3. The database says the same without the API. With the SQL password from the user secrets:

   ```powershell
   $sa = "<sql password>"; $sql = docker ps --filter name=sql- --format "{{.Names}}" | Select-Object -First 1
   docker exec $sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $sa -d invoicehub -Q "EXEC sp_set_session_context N'tenant_id', '0198a000-0000-7000-8000-000000000002'; EXECUTE AS USER = 'api_user'; SELECT COUNT(*) FROM webhooks.endpoint; SELECT COUNT(*) FROM webhooks.endpoint WHERE id = '0198a000-0000-7000-8000-0000000001e1'; REVERT;"
   ```

   The first count is Fabrikam's endpoints, the second is **0**, with no `WHERE tenant_id` anywhere: row-level security filters every query of the application login.

## 5. When something does not behave

| Symptom | Likely cause | What to do |
|---|---|---|
| Portal says it cannot reach the platform | the API is still starting | wait for `api` to be healthy in the dashboard |
| Sign-in loops | the portal must keep port 5173 (Keycloak's redirect address) | free the port, restart |
| Nothing arrives at a receiver | the receiver's failure switch is on, or it does not know the secret | Contoso's screen shows a red “Broken on purpose” badge; for a new endpoint paste its secret (Security tab → Reveal) into *What this system trusts* |
| Emails do not appear | Mailpit is not running | check the `mailpit` resource; open <http://localhost:8025> |
| Export stays “queued” | the delivery host has not started yet, or Azurite is down | check `delivery` and `storage` in the dashboard (the job runner lives in `delivery`) |
| `delivery-b` / `delivery-c` are not running | by design: they are started by hand for scene 1 | press Start in the dashboard |
| A resource logs `crit … Hosting startup assembly exception … Microsoft.WebTools.ApiEndpointDiscovery` | you started the AppHost from **Visual Studio**: its debugger asks every web project to load one of its own helper assemblies, which a project run this way does not carry | harmless (the application starts right after it). Each receiver now has its own launch profile and port (Contoso 5201, Fabrikam 5202), so the ports are right either way. `dotnet run --project src/AppHost/InvoiceHub.AppHost --launch-profile http` does not show it |
| `delivery` (or another host) shows **Running (Unhealthy)** and its log says `Overriding HTTP_PORTS … Binding to values defined by URLS instead 'https://localhost:…'` | Visual Studio wrote its own `Properties/launchSettings.json` with an `https` profile and fixed `applicationUrl` ports for that project; Aspire turns it into an HTTPS endpoint and the app then listens on HTTPS only, while the health check calls HTTP | the profile of `src/Hosts/InvoiceHub.Delivery` carries no `applicationUrl` on purpose (the port comes only from the AppHost). If Visual Studio regenerates a profile with URLs for any project, delete the `applicationUrl` line and restart the AppHost |
| Docker is slow | less than 8 GB | close other containers; the Service Bus emulator and three SQL containers are the heavy ones |

## 6. Things worth saying out loud

* The demo timers are compressed (the seeded endpoints use the `demo` retry preset, 10 minutes instead of 72 hours); the production preset is a one-word switch on the Delivery tab.
* The Service Bus emulator keeps nothing on restart, and cannot cancel scheduled messages: the platform treats the database as the source of truth and the broker as a fast path, and a chaos test restarts the emulator in the middle of a run to prove it.
* Local receivers are reachable only because this is a development machine (a visible notice in the portal says so); metadata addresses and private ranges stay blocked.
