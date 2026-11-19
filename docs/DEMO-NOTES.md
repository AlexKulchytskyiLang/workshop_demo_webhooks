# Demo notes: the portal, a new endpoint, and the security tour

A crib sheet for the person on stage. It complements [RUNBOOK.md](RUNBOOK.md), which has the six scenes of the main demo (broker flow, outage, duplicates, claim check, thin events, isolation). This one covers what you need *around* them: how to sign in as Contoso, how to create a new endpoint (the address of "your API" that we call), how to create an API key for the pull API, and a security tour with the exact click for each point.

Passwords: every demo account uses **`Passw0rd!2026`**.

## 0. Before you go on stage (5 minutes)

1. Start: `dotnet run --project src\AppHost\InvoiceHub.AppHost --launch-profile http`, or press F5 on `InvoiceHub.AppHost` in Visual Studio.
2. Open the Aspire dashboard <http://localhost:15180> and wait until **every** resource is *Running* or *Finished*. `delivery` must say *Running*, not *Unhealthy*. `delivery-b` and `delivery-c` stay *Not started* on purpose.
3. Open these tabs, in this order, and leave them:

| Tab | Address | Signed in as |
|---|---|---|
| Portal | <http://localhost:5173> | nobody yet |
| Contoso's receiving system | <http://localhost:5174> | (no login) |
| Simulator console | <http://localhost:5400> | (no login) |
| Mailpit | <http://localhost:8025> | (no login) |
| Aspire dashboard | <http://localhost:15180> | (no login) |

4. On Contoso's receiving system press **Forget everything** (last tab) so counters start at zero, and make sure the failure switch says *Healthy*.
5. Zoom the browser to 125 %. Use the dark theme (the toggle is in the top bar) for a projector.

## 1. Sign in as Contoso (1 minute)

1. Open the portal. You see **Choose your organisation**: *Contoso Ltd*, *Fabrikam Inc*, and *Register your organisation*.
2. Click **Contoso Ltd**. You are sent to the Keycloak sign-in page (the identity provider).
3. Sign in as **`developer@contoso.test`** / `Passw0rd!2026`. You land on the **Overview**: KPIs, the endpoint table, the bell at the top.

What to say:

* "The portal does not hold passwords. It is an OIDC client of Keycloak: authorization code with PKCE, no secret in the browser."
* "The tenant is not something the browser tells the API. It is a **claim in the token** (`tenant_id`). The organisation I clicked is only a hint, and the API refuses a mismatch."

Show the mismatch (20 seconds, optional): sign out, choose **Fabrikam Inc**, sign in as `owner@contoso.test`: the portal stops with an explanation. That is the tenant check.

Who to use for what:

| Account | What it can do | Use it to show |
|---|---|---|
| `owner@` / `admin@contoso.test` | everything, including the audit log, team and roles | audit, API keys, team |
| `developer@contoso.test` | endpoints, secrets (after a fresh sign-in), replay, API keys | the main demo |
| `support@contoso.test` | look at deliveries and payloads, replay **one** event | support story |
| `viewer@contoso.test` | read only: every change button is disabled or missing | roles are enforced |

## 2. Create a new endpoint (4 to 5 minutes)

"Endpoint" here means: *the address of the customer's API that we call with webhooks.* The steps follow the go-live checklist the portal shows on the endpoint's page.

1. **Overview → Add endpoint**. The page says *Three things to start. Safe defaults for everything else.*
2. Pick the template **ERP / accounting**. It pre-selects a sensible set of events.
3. **Name:** `Demo ERP hook`. **Endpoint URL:** `http://localhost:5201/hooks/demo-erp`.
   * The live checks under the field explain each rule: HTTPS in production, no credentials in the URL, nothing in a private network, loopback or the cloud-metadata address. Plain `http://localhost` is accepted **only because this is a development machine**; say so, because it is the first thing a security person will ask.
   * Optional (20 seconds): type `https://169.254.169.254/latest/meta-data` and show it is refused (the metadata address). Then `http://example.com/hook`: refused, HTTPS required.
4. Look at the **Events** picker: 42 event types in categories, with bundles. Keep the template's choice.
5. **Create endpoint.** You land in the endpoint workspace with status **Pending** and a go-live checklist.
6. Open the **Security** tab. It shows four independent layers: *Transport*, *Delivery authentication*, *Message signing*, *Payload encryption* (two are marked *Planned*, which is honest).
7. In **Message signing** press **Reveal**. A dialog asks you to sign in again: this is **step-up authentication**. Enter the password again in the pop-up. The secret appears (`whsec_…`). Copy it. (If the audience asks: the API checks that the token's `auth_time` is under 5 minutes old; the screen is not the control.)
8. Tell the customer's system about it. In the tab **Contoso's receiving system → Break things & trust → What this system trusts**, paste the secret and press **Save**. "This is what the customer's engineer does in their own configuration; we never see their system."
9. Back in the portal: **Verify URL → Send verification request.** It says **Verified**, and the endpoint becomes **Active**. (If you press verify *before* step 8 it fails with advice: a good thing to show once.)
10. **Send test.** The test dialog sends a realistic event and then the **negative tests**: a *tampered signature* and a *six-minute-old timestamp* must be **refused** (4xx) for the test to pass, and a *duplicate* must be acknowledged twice. These use exactly the signing and egress code of real deliveries.
11. Make business happen: in the simulator console set **Invoices** to 10 and **Per second** to 2, press **Start traffic**. Watch **Delivery logs** in the portal fill, and the *Received* card on Contoso's screen climb.
12. Open one row in the log: the request **exactly as it was sent** (signature shortened, credentials masked), the answer, the attempts, the trace id. Press **Copy as cURL**: it deliberately does not copy the signature.

Keep the traffic small here (about 15 events a second at most). The broker in this demo is an emulator; see the note in [RUNBOOK.md](RUNBOOK.md), scene 1.

### Variations if you have more time

* **Both** signatures, or **ES256** (the receiver trusts the endpoint's public JWKS instead of a shared secret: *Break things & trust → JWKS address*).
* **Rotate** the signing key with a 24-hour overlap: new deliveries carry two signatures, the receiver that still knows only the old secret keeps working.
* The **filter builder** on the Events tab (for example `data.amount >= 5000`), tested against your latest real events *before* saving.
* The **retry schedule preview** on the Delivery tab: the preview is computed by the same code that delivers.

## 3. Create an API key for the pull API (2 minutes)

The pull API is how a customer's system fetches what a webhook only hints at (`GET /v1/events`, `GET /v1/kyc/{id}`).

1. Sign in as `developer@contoso.test` (or the owner). **Settings → API keys & IPs → Create API key.**
2. **Name:** `Nightly reconciliation`. **What the key may do:** tick only **`events:read`**. **Expiry:** 90 days. Press **Create key**.
3. Another **fresh sign-in** is required (step-up), then the key is shown **once**: `ihk_…`. Copy it. Say: "We store only its SHA-256. If it is lost we cannot show it again, only replace it."
4. Use it:

   ```powershell
   $key = "ihk_..."
   curl.exe -s -H "Authorization: Bearer $key" "http://localhost:5100/v1/events?limit=3"
   ```

   You get the three oldest events of Contoso and a `next` cursor; pass it back as `after=<next>` for the following page. Nothing of another tenant can appear, whatever you ask.
5. Show the scope: take an identity-check id from Contoso's receiving system (**Files & identity checks**) and call `GET /v1/kyc/{id}` with the same key: **403**. "The scope is the permission."
6. **Revoke** the key (typed confirmation) and repeat the first call: **401**, the very next call. Then open **Audit log**: the creation and the revoke are there, with who and from where (reading with the key is deliberately not an audit line; there would be thousands).

## 4. Download the file behind a claim-check link with curl (3 minutes)

Scene 4 of the runbook shows that a big result never travels inside the webhook: the webhook carries a ticket (size, SHA-256 and an expiring link) and the receiver fetches the file. This section lets **you** fetch it by hand, for example when the endpoint that received the webhook is webhook.site and you want to show the file.

### Why the link from a browser tab fails (what went wrong in the first attempt)

| What you see | Why | Fix |
|---|---|---|
| `AuthorizationFailure: Server failed to authenticate the request…` right after opening the link | Copying from a **formatted JSON view** brings JSON escapes along: `filename%3D\"01a1…json\"`. The real link has plain quotes there; the backslashes change a value that is **part of the signature**, so the signature no longer matches. Reproduced: the same 403 with the same message | Do not copy by hand. Parse the JSON (below), or replace `\"` with `%22` |
| The same error although the link was copied correctly | The link **expires 15 minutes** after the attempt that carried it was built (`se=` in the link; `st=` is two minutes earlier to tolerate clock differences). A new attempt, a replay or a new event gets a new link. In the first attempt both problems were present: the request was made at 10:40:54Z, the link was valid until 10:38:19Z. Tested: an untouched link, 25 seconds after its expiry, gets the very same `AuthorizationFailure` | Produce a fresh event and use the link at once |
| The link opens nowhere else | It points at `127.0.0.1:<port>`: the Azurite emulator **on the demo machine**, and the port changes at every start | Run curl on the demo machine. In production this would be a public blob URL |

The platform never shows a usable link in its own screens: the portal's delivery log hides it on purpose (a log must not hold a working key to a file). The link exists where the customer's system received it: the receiving application's inbox, or webhook.site if that is the endpoint's address.

### Produce a fresh claim-check event (5 seconds)

```powershell
# a catalogue import report (about 600 bytes of JSON), or use "Request export" in the simulator console for the 50 MB file
curl.exe -s -X POST http://localhost:5400/simulator/imports -H "Content-Type: application/json" -d '{\"tenant\":\"contoso\",\"products\":200}'
```

(In the simulator console: **Import catalogue** or **Request export**.) Within a few seconds Contoso's receiving system shows it under *Inbox* as `catalog.products.imported` or `export.completed`.

### The command: take the link from Contoso's receiver and download it

PowerShell (Windows PowerShell 5.1 and 7). Tested; the parentheses around `Invoke-RestMethod` matter in 5.1:

```powershell
$e    = (Invoke-RestMethod "http://localhost:5201/api/inbox?limit=300") | Where-Object { $_.type -in 'export.completed','catalog.products.imported' } | Select-Object -First 1
$data = ((Invoke-RestMethod "http://localhost:5201/api/inbox/$($e.webhookId)").body | ConvertFrom-Json).data
curl.exe -s -o claim-check-file -w "HTTP %{http_code}, %{size_download} bytes`n" $data.download.url.Replace('"','%22')
```

bash (Git Bash, macOS, Linux), also tested:

```bash
id=evt_...    # the webhook id from the receiver's inbox (the "id" of the body)
url=$(python -c "import json,urllib.request as u; b=json.load(u.urlopen('http://localhost:5201/api/inbox/$id'))['body']; print(json.loads(b)['data']['download']['url'])")
curl -s -o claim-check-file -w "HTTP %{http_code}, %{size_download} bytes\n" "${url//\"/%22}"
```

Expected: `HTTP 200` and the file. The `.Replace('"','%22')` / `${url//\"/%22}` turns the two plain quotes inside the link into `%22`, which is what any HTTP client sends anyway. It is also required in PowerShell: a raw `"` inside an argument to `curl.exe` is eaten by the shell.

**If the body is only on webhook.site** (or any other receiver): open the request, take the **Raw Content** (not the formatted view) and let PowerShell do the un-escaping:

```powershell
$body = @'
<paste the raw body here, exactly as shown>
'@ | ConvertFrom-Json
curl.exe -s -o claim-check-file $body.data.download.url.Replace('"','%22')
```

### Prove it is the right file (the point of the checksum)

```powershell
"announced : $($data.sha256)"
"downloaded: $((Get-FileHash claim-check-file -Algorithm SHA256).Hash.ToLower())"
```

On the test run both were `8984ef738f18ef0027be5727873159c68cc523ece29fc6acc55fb84774c566c7` and the size was 590 bytes, as announced (`$data.size_bytes`). This is exactly what the receiver does while it streams: it hashes as it reads and compares at the end (the *verified* verdict on Contoso's screen).

### Try to break it (30 seconds, same link, within the 15 minutes)

```powershell
$u = $data.download.url.Replace('"','%22')
curl.exe -s -o NUL -w "one character of sig= changed: HTTP %{http_code}`n" ($u -replace 'sig=(.)','sig=Z')   # 403
curl.exe -s -o NUL -w "no signature at all:           HTTP %{http_code}`n" ($u -replace '&sig=.*$','')          # 403
```

(If the first character of `sig=` already is a `Z`, change the replacement letter.) Both return **403 with the same error body** and say nothing about why: the storage service does not tell an attacker which part was wrong. After the 15 minutes the genuine, untouched link gets the same error (tested): the link is the permission and it is short-lived. Say it that way: "a retry hours later does not use an old link; it gets a new one, because every attempt builds its own."

### The same in Postman

The collection `demo/postman/InvoiceHub-claim-check.postman_collection.json` does the whole thing as six requests with tests (12 assertions, all passing on the test run with the Postman CLI):

| # | Request | What its tests check |
|---|---|---|
| 0 | `POST {{simulatorUrl}}/simulator/imports` | a fresh claim-check event was queued (202). Wait about 5 seconds |
| 1 | `GET {{receiverUrl}}/api/inbox?limit=300` | the newest `export.completed` or `catalog.products.imported` is found; its id is kept in `webhookId` |
| 2 | `GET {{receiverUrl}}/api/inbox/{{webhookId}}` | the body is parsed (no backslashes), the link is kept with its quotes as `%22`, the announced size and SHA-256 are kept, and the link has not expired |
| 3 | `GET {{downloadUrl}}` | HTTP 200, the size and the SHA-256 of the downloaded bytes equal what the webhook announced |
| 4 | `GET {{tamperedUrl}}` | one character of `sig=` changed: 403, `AuthorizationFailure` |
| 5 | `GET {{unsignedUrl}}` | no signature: 403, the same error |

**Postman app:** *Import* the file, open the collection, send **0**, wait a few seconds, then send **1** to **5** in order (or *Run collection* with a delay of 6000 ms). The variables `receiverUrl` (Contoso 5201, Fabrikam 5202) and `simulatorUrl` are on the collection's *Variables* tab. The *Console* (bottom left) shows the size and the SHA-256 that were announced and computed.

**Postman CLI:**

```powershell
postman collection run demo\postman\InvoiceHub-claim-check.postman_collection.json --delay-request 6000
```

Things that are set up for you and matter if you build the requests by hand:

* **Do not paste the link into a request by hand.** Request 2 takes it from the parsed JSON; a pasted copy from a formatted view carries `\"` and fails.
* **Requests 3 to 5 have URL encoding switched off** (`disableUrlEncoding`), so Postman sends the link byte for byte as it was signed. Request 2 has already turned the two plain quotes into `%22`.
* The link works for 15 minutes and only on this machine (the port of the Azurite emulator changes at every start): when request 2 says *expired*, send request 0 again.
* Requests 1 and 2 talk to the **receiver** because that is where the link exists. They are not part of the platform's API; the platform's own pull API (`/v1/events`, with an API key) never returns a usable link.

## 5. The security tour (pick what fits the time; each item has its click)

| # | Point to make | Where to show it | What the audience sees |
|---|---|---|---|
| 1 | **Identity is not ours**: OIDC with PKCE, MFA possible in Keycloak, no passwords in the app | Sign-in (section 1) | The redirect to Keycloak, no password field in the portal |
| 2 | **The tenant is in the token, and checked twice** (API, then SQL row-level security) | Scene 6 of the runbook: the Fabrikam user opening Contoso's endpoint id, then the SQL query as `api_user` | "Does not exist, or it belongs to another organisation", and `0` rows with no `WHERE` |
| 3 | **Roles are enforced by the API**, not by hiding buttons | Sign in as `viewer@contoso.test`: open an endpoint, the change controls are disabled; try the same call with the viewer's token and get 403 | Greyed controls plus a 403 |
| 4 | **Dangerous actions need a fresh sign-in** (step-up): reveal secret, rotate, change algorithm, create API key | Security → Reveal; API keys → Create | The pop-up that asks for the password again |
| 5 | **Every message is signed**; the receiver must verify the raw body; a replay or a changed byte is refused | Send test → negative tests | "Tampered signature: refused. Six-minute-old timestamp: refused" |
| 6 | **Rotation without downtime**: several keys at once, overlap you choose | Security → Rotate | Two keys in the table, the old one *Retires in 24 h* |
| 7 | **We never call an address we should not**: SSRF guard (resolve, check every address, connect to the checked one, no redirects) | Add endpoint with `https://169.254.169.254/...` | The refusal with its reason |
| 8 | **Secrets are encrypted at rest and never shown twice or logged** | Delivery log drawer (credentials masked); Security → delivery authentication field (`••••`) | Masked values everywhere |
| 9 | **Personal data does not travel**: `kyc.completed` is thin; details are pulled with a scoped key; the pull is audited **before** data is returned | Scene 5 of the runbook | The inbox body with only `verification_id`; the audit line attributed to the **key** |
| 10 | **Big results do not travel either**: a 600-byte envelope, expiring read-only link per attempt, checksum verified | Scene 4 of the runbook, and section 4 above (curl) | `sha256` verdict *verified*; a changed character in `sig=` gives 403 |
| 11 | **Everything is audited, append-only, in the same transaction** | Sign in as `owner@`: **Audit log** after the steps above | Lines for reveal, create key, verify, replay, with actor, role and IP; the database login cannot update or delete them |
| 12 | **Replay is a controlled act**: dry run, typed confirmation (`REPLAY 37`), pace, daily cap; resume needs a passing test | Replay center → New replay job | The dry-run result and the typed phrase |
| 13 | **Who gets told**: pre-warnings at 25, 50 and 75 % before an endpoint is switched off, by role, in the bell and by email | Settings → Alerts; the bell; Mailpit | The bell badge and the email in Mailpit |

### What is *not* built (say it before someone asks)

OAuth 2.0 client-credentials and mutual-TLS authentication **to the customer's server** (the Security tab shows them as *Planned*), payload encryption (JWE), a per-tenant throughput quota, a Content-Security-Policy on the portal. The list with reasons is in [ASR-PROOF.md](ASR-PROOF.md).

## 6. If something misbehaves

| Symptom | Do |
|---|---|
| The portal says it cannot reach the platform | Wait for `api` to be healthy in the dashboard |
| `delivery` is *Unhealthy* | Look at its log for `Overriding HTTP_PORTS … Binding to values defined by URLS`: see the row in [RUNBOOK.md](RUNBOOK.md) section 5 |
| Verification fails | You did not paste the secret into *What this system trusts* yet, or you pasted the wrong one |
| Nothing arrives at the receiver | Its switch is not *Healthy* (a red *Broken on purpose* badge), or it does not trust the new endpoint's secret |
| The step-up pop-up does not open | The browser blocked pop-ups for `localhost:5173`; allow them once |
| A scene is stuck | Do not debug on stage: press **L** on the portal slides of the deck for the clickable prototype, or use the screenshots in `docs/screenshots/` |
| Clean slate between runs | Stop (Ctrl+C), `docker volume rm invoicehub-sql invoicehub-keycloak invoicehub-azurite`, start again |
