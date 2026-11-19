import { useEffect, useId, useState } from 'react';
import {
  PLATFORM_API, RECEIVER_API, RECEIVER_NAME, call, useLive,
  type FileRow, type InboxDetail, type InboxRow, type InvoiceRow, type KycRow, type ReconcileResult, type Stats, type SubscriptionRow,
} from './api';

type Tab = 'inbox' | 'invoices' | 'files' | 'reconcile' | 'chaos';

const TABS: { id: Tab; label: string }[] = [
  { id: 'inbox', label: 'Inbox' }, { id: 'invoices', label: 'Invoices & renewals' }, { id: 'files', label: 'Files & identity checks' },
  { id: 'reconcile', label: 'Did I miss anything?' }, { id: 'chaos', label: 'Break things & trust' },
];

const CHAOS: { mode: string; title: string; text: string }[] = [
  { mode: 'ok', title: 'Healthy', text: 'Answer every valid request with 200.' },
  { mode: 'fail500', title: 'Server error (500)', text: 'The classic outage. The platform retries with growing delays, opens the circuit, and nothing is lost.' },
  { mode: 'slow', title: 'Slow', text: 'Answer only after a delay. The platform times out, retries, and stops other tenants from being affected.' },
  { mode: 'gone', title: 'Gone (410)', text: 'Tell the platform this address no longer exists: it switches the endpoint off and holds the events.' },
  { mode: 'drop', title: 'Drop the answer', text: 'Process the event but never answer. The platform sends it again: a duplicate your system must absorb.' },
  { mode: 'unauthorized', title: 'Unauthorized (401)', text: 'Pretend the signature check fails. A misconfiguration, not an outage.' },
  { mode: 'throttle', title: 'Throttle (429)', text: 'Ask the platform to slow down. It honours Retry-After.' },
];

const time = (iso: string | null) => (iso ? new Date(iso.endsWith('Z') ? iso : `${iso}Z`).toLocaleTimeString() : '—');
const bytes = (n: number | null) => (n === null ? '—' : n >= 1_048_576 ? `${(n / 1_048_576).toFixed(1)} MB` : n >= 1024 ? `${(n / 1024).toFixed(1)} KB` : `${n} B`);

export function App() {
  const [tab, setTab] = useState<Tab>('inbox');
  const stats = useLive<Stats>('/api/stats');
  const c = stats.data?.counts;

  useEffect(() => { document.title = `${RECEIVER_NAME} · receiving system`; }, []);

  return (
    <>
      <header>
        <h1>{RECEIVER_NAME}</h1>
        <span className="badge">a tenant's own system, independent of the platform</span>
        <span className="badge mono">{RECEIVER_API}</span>
        {stats.data && stats.data.chaos.mode !== 'ok' && <span className="pill red" role="status">Broken on purpose: {stats.data.chaos.mode}</span>}
        {stats.error && <span className="err" role="alert">{stats.error}</span>}
      </header>
      <main>
        <div className="cards" aria-label="What this system holds">
          <Card label="Received" value={c?.received} />
          <Card label="Processed" value={c?.processed} />
          <Card label="Waiting" value={c?.waiting} />
          <Card label="Dead" value={c?.dead} bad={(c?.dead ?? 0) > 0} />
          <Card label="Duplicates absorbed" value={c?.duplicates} />
          <Card label="Stale ignored" value={c?.staleIgnored} />
          <Card label="Rejected (bad signature)" value={c?.rejected} />
          <Card label="Files verified" value={c ? `${c.filesVerified}/${c.files}` : undefined} />
          <Card label="Recovered by pull" value={c?.recovered} />
        </div>

        <div className="tabs" role="tablist" aria-label="Receiving system">
          {TABS.map((t) => <button key={t.id} type="button" role="tab" id={`t-${t.id}`} aria-selected={tab === t.id} aria-controls="panel" onClick={() => setTab(t.id)}>{t.label}</button>)}
        </div>
        <div id="panel" role="tabpanel" aria-labelledby={`t-${tab}`}>
          {tab === 'inbox' && <Inbox />}
          {tab === 'invoices' && <Invoices />}
          {tab === 'files' && <Files />}
          {tab === 'reconcile' && <Reconcile />}
          {tab === 'chaos' && <Chaos stats={stats.data} onChanged={stats.reload} />}
        </div>
      </main>
    </>
  );
}

function Card({ label, value, bad }: { label: string; value: number | string | undefined; bad?: boolean }) {
  return <div className={`card${bad ? ' bad' : ''}`}><div className="l">{label}</div><div className="v">{value ?? '…'}</div></div>;
}

// ------------------------------------------------------------------------------------------------ inbox

function Inbox() {
  const inbox = useLive<InboxRow[]>('/api/inbox?limit=60');
  const [open, setOpen] = useState<string | null>(null);
  const detail = useLive<InboxDetail>(open ? `/api/inbox/${open}` : '/api/stats', 60_000);
  const rows = inbox.data ?? [];
  return (
    <div className="grid2" style={{ gridTemplateColumns: open ? 'minmax(0,1.3fr) minmax(0,1fr)' : '1fr' }}>
      <section className="panel" aria-labelledby="inbox-h">
        <h2 id="inbox-h">Inbox: one row per event, however often it was sent</h2>
        <p>A webhook is acknowledged first and processed afterwards. The idempotency key is the <span className="mono">webhook-id</span>; the sequence guard ignores an event older than what we already hold.</p>
        <table aria-label="Inbox">
          <thead><tr><th scope="col">Received</th><th scope="col">Event</th><th scope="col">Status</th><th scope="col">Outcome</th><th scope="col">Dup.</th><th scope="col">Version</th><th scope="col">Trace</th></tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="click" tabIndex={0} onClick={() => setOpen(r.webhookId)} onKeyDown={(e) => { if (e.key === 'Enter') setOpen(r.webhookId); }} aria-label={`Open ${r.type}`}>
                <td className="mono">{time(r.receivedAt)}</td>
                <td>{r.type}{r.test && <span className="pill gray" style={{ marginLeft: 6 }}>test</span>}{r.source === 'pull' && <span className="pill blue" style={{ marginLeft: 6 }}>pulled</span>}<div className="mono muted">{r.aggregateId}{r.sequence !== null ? ` #${r.sequence}` : ''}</div></td>
                <td><span className={`pill ${r.status === 'processed' ? 'green' : r.status === 'dead' ? 'red' : r.status === 'ignored' ? 'gray' : 'amber'}`}>{r.status}</span></td>
                <td className="mono">{r.outcome ?? '—'}</td>
                <td>{r.duplicates > 0 ? r.duplicates : ''}</td>
                <td className="mono">{r.apiVersion ?? ''}</td>
                <td className="mono" title={r.traceParent ?? ''}>{r.traceParent ? r.traceParent.split('-')[1]?.slice(0, 8) : ''}</td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={7}>Nothing received yet.</td></tr>}
          </tbody>
        </table>
      </section>
      {open && (
        <section className="panel" aria-labelledby="detail-h">
          <div className="row" style={{ justifyContent: 'space-between' }}><h2 id="detail-h">Event detail</h2><button type="button" className="btn quiet" onClick={() => setOpen(null)}>Close</button></div>
          {detail.data && 'webhookId' in detail.data && (
            <>
              <p className="mono">{detail.data.webhookId}</p>
              <h3 style={{ fontSize: 13 }}>Body as received</h3>
              <pre tabIndex={0} aria-label="Body as received">{pretty(detail.data.body)}</pre>
              <h3 style={{ fontSize: 13 }}>Headers</h3>
              <pre tabIndex={0} aria-label="Headers">{JSON.stringify(detail.data.headers, null, 2)}</pre>
            </>
          )}
        </section>
      )}
    </div>
  );
}

function pretty(json: string): string {
  try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; }
}

// ------------------------------------------------------------------------------------------------ invoices

function Invoices() {
  const invoices = useLive<InvoiceRow[]>('/api/invoices?limit=60');
  const subs = useLive<SubscriptionRow[]>('/api/subscriptions');
  return (
    <div className="grid2">
      <section className="panel" aria-labelledby="inv-h">
        <h2 id="inv-h">Invoice mirror</h2>
        <p>What this ERP believes about each invoice. Events may arrive in any order; the sequence number makes the final state correct.</p>
        <table aria-label="Invoices">
          <thead><tr><th scope="col">Invoice</th><th scope="col">Customer</th><th scope="col">Amount</th><th scope="col">Status</th><th scope="col">Seq.</th></tr></thead>
          <tbody>
            {(invoices.data ?? []).map((i) => (
              <tr key={i.invoiceId}><td className="mono">{i.number ?? i.invoiceId}</td><td>{i.customer}</td><td>{i.amount?.toLocaleString('en-US')} {i.currency}</td><td><span className={`pill ${i.status === 'paid' ? 'green' : i.status === 'overdue' ? 'red' : 'amber'}`}>{i.status}</span></td><td>{i.sequence}</td></tr>
            ))}
            {(invoices.data ?? []).length === 0 && <tr><td colSpan={5}>No invoices yet.</td></tr>}
          </tbody>
        </table>
      </section>
      <section className="panel" aria-labelledby="sub-h">
        <h2 id="sub-h">Subscription renewals</h2>
        <table aria-label="Subscription renewals">
          <thead><tr><th scope="col">Subscription</th><th scope="col">Plan</th><th scope="col">Amount</th><th scope="col">Renewed</th></tr></thead>
          <tbody>
            {(subs.data ?? []).map((s) => <tr key={s.subscriptionId}><td className="mono">{s.subscriptionId}</td><td>{s.plan}</td><td>{s.amount?.toLocaleString('en-US')} {s.currency}</td><td className="mono">{time(s.renewedAt)}</td></tr>)}
            {(subs.data ?? []).length === 0 && <tr><td colSpan={4}>No renewals yet.</td></tr>}
          </tbody>
        </table>
      </section>
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ files and identity checks

function Files() {
  const files = useLive<FileRow[]>('/api/files');
  const kyc = useLive<KycRow[]>('/api/kyc');
  return (
    <>
      <section className="panel" aria-labelledby="files-h">
        <h2 id="files-h">Files fetched through claim-check links</h2>
        <p>Big results never travel inside a webhook. The event carries a size, a checksum and an expiring link; this system downloads the file and checks it against the checksum while it streams.</p>
        <table aria-label="Files">
          <thead><tr><th scope="col">When</th><th scope="col">Event</th><th scope="col">Size</th><th scope="col">Checksum</th><th scope="col">Verdict</th><th scope="col">Took</th></tr></thead>
          <tbody>
            {(files.data ?? []).map((f) => (
              <tr key={f.id}>
                <td className="mono">{time(f.fetchedAt)}</td>
                <td>{f.type}<div className="mono muted">{f.webhookId}</div></td>
                <td>{bytes(f.actualSize)}{f.expectedSize !== null && f.actualSize !== f.expectedSize ? ` (expected ${bytes(f.expectedSize)})` : ''}</td>
                <td className="mono" title={f.actualSha256 ?? ''}>{f.actualSha256 ? `${f.actualSha256.slice(0, 16)}…` : '—'}</td>
                <td><span className={`pill ${f.verdict === 'verified' ? 'green' : f.verdict === 'mismatch' ? 'red' : 'amber'}`}>{f.verdict === 'link_expired' ? 'link expired' : f.verdict}</span>{f.error && <div className="muted">{f.error}</div>}</td>
                <td>{f.durationMs} ms</td>
              </tr>
            ))}
            {(files.data ?? []).length === 0 && <tr><td colSpan={6}>No files yet. Start an export in the simulator.</td></tr>}
          </tbody>
        </table>
      </section>
      <section className="panel" aria-labelledby="kyc-h">
        <h2 id="kyc-h">Identity checks pulled with an API key</h2>
        <p>The webhook only said "verification X finished". The details were pulled from the platform with a key that has the <span className="mono">kyc:read</span> scope, and every pull left an audit line on the platform.</p>
        <table aria-label="Identity checks">
          <thead><tr><th scope="col">Pulled</th><th scope="col">Verification</th><th scope="col">Decision</th><th scope="col">Person</th><th scope="col">Document</th></tr></thead>
          <tbody>
            {(kyc.data ?? []).map((k) => (
              <tr key={k.verificationId}><td className="mono">{time(k.fetchedAt)}</td><td className="mono">{k.verificationId}</td><td><span className={`pill ${k.decision === 'approved' ? 'green' : 'amber'}`}>{k.decision}</span> risk {k.riskScore}</td><td>{k.name} ({k.country})</td><td className="mono">{k.document}</td></tr>
            ))}
            {(kyc.data ?? []).length === 0 && <tr><td colSpan={5}>No identity checks yet.</td></tr>}
          </tbody>
        </table>
      </section>
    </>
  );
}

// ------------------------------------------------------------------------------------------------ reconcile

function Reconcile() {
  const config = useLive<{ apiUrl: string | null; apiKey: string | null; configured: boolean }>('/api/pull-config', 10_000);
  const [hours, setHours] = useState(24);
  const [result, setResult] = useState<ReconcileResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [url, setUrl] = useState('');
  const [key, setKey] = useState('');
  const hoursId = useId();
  const urlId = useId();
  const keyId = useId();

  const run = async () => {
    setBusy(true); setError(null);
    try { setResult(await call<ReconcileResult>('/api/reconcile', { method: 'POST', body: JSON.stringify({ hours }) })); } catch (e) { setError(e instanceof Error ? e.message : 'Failed'); }
    setBusy(false);
  };
  const save = async () => {
    setError(null);
    try { await call('/api/pull-config', { method: 'PUT', body: JSON.stringify({ apiUrl: url || null, apiKey: key || null }) }); setKey(''); config.reload(); } catch (e) { setError(e instanceof Error ? e.message : 'Failed'); }
  };

  return (
    <div className="grid2">
      <section className="panel" aria-labelledby="rec-h">
        <h2 id="rec-h">Did I miss anything?</h2>
        <p>Webhooks are at-least-once, but a receiver can still be down for longer than the retry window. This walks the platform's event stream (<span className="mono">GET /v1/events</span>), compares it with the inbox, and recovers what is missing through the same idempotent path.</p>
        <div className="row">
          <div><label htmlFor={hoursId}>Look back</label><select id={hoursId} value={hours} onChange={(e) => setHours(Number(e.target.value))}>{[1, 6, 24, 72, 168].map((h) => <option key={h} value={h}>{h} hours</option>)}</select></div>
          <button type="button" className="btn" disabled={busy || !config.data?.configured} onClick={() => void run()}>{busy ? 'Checking…' : 'Check what I missed'}</button>
        </div>
        <div aria-live="polite" style={{ marginTop: 10 }}>
          {error && <div className="err" role="alert">{error}</div>}
          {result && <p className={result.problem ? 'err' : 'ok'} data-testid="reconcile-result">{result.problem ?? `Checked ${result.checked} events: ${result.missing} were missing, ${result.recovered} recovered.`}</p>}
        </div>
      </section>
      <section className="panel" aria-labelledby="pull-h">
        <h2 id="pull-h">The platform and my API key</h2>
        <p>Currently: <span className="mono">{config.data?.apiUrl ?? PLATFORM_API}</span>, key <span className="mono">{config.data?.apiKey ?? 'none'}</span> {config.data?.configured ? <span className="pill green">configured</span> : <span className="pill amber">not configured</span>}</p>
        <div className="row">
          <div><label htmlFor={urlId}>Platform address</label><input id={urlId} value={url} onChange={(e) => setUrl(e.target.value)} placeholder={PLATFORM_API} /></div>
          <div><label htmlFor={keyId}>API key</label><input id={keyId} type="password" value={key} onChange={(e) => setKey(e.target.value)} placeholder="ihk_…" autoComplete="off" /></div>
          <button type="button" className="btn quiet" disabled={!url && !key} onClick={() => void save()}>Save</button>
        </div>
      </section>
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ chaos and trust

function Chaos({ stats, onChanged }: { stats: Stats | undefined; onChanged: () => void }) {
  const trust = useLive<{ secrets: string[]; jwksUrl: string | null; configured: boolean }>('/api/trust', 10_000);
  const [delay, setDelay] = useState(15);
  const [secret, setSecret] = useState('');
  const [jwks, setJwks] = useState('');
  const [error, setError] = useState<string | null>(null);
  const secretId = useId();
  const jwksId = useId();
  const delayId = useId();

  const set = async (mode: string) => {
    setError(null);
    try { await call('/api/chaos', { method: 'PUT', body: JSON.stringify({ mode, delaySeconds: delay }) }); onChanged(); } catch (e) { setError(e instanceof Error ? e.message : 'Failed'); }
  };
  const saveTrust = async () => {
    setError(null);
    try { await call('/api/trust', { method: 'PUT', body: JSON.stringify({ secrets: secret ? [secret] : [], jwksUrl: jwks || null }) }); setSecret(''); trust.reload(); } catch (e) { setError(e instanceof Error ? e.message : 'Failed'); }
  };
  const reset = async () => { await call('/api/reset', { method: 'POST' }); onChanged(); };

  return (
    <div className="grid2">
      <section className="panel" aria-labelledby="chaos-h">
        <h2 id="chaos-h">Break this system on purpose</h2>
        <p>Pick how this receiver misbehaves, then send some traffic from the simulator and watch the platform cope.</p>
        <div role="radiogroup" aria-label="Failure mode">
          {CHAOS.map((m) => (
            <label key={m.mode} className={`radio${stats?.chaos.mode === m.mode ? ' on' : ''}`}>
              <input type="radio" name="chaos" checked={stats?.chaos.mode === m.mode} onChange={() => void set(m.mode)} />
              <span><span className="t">{m.title}</span><span className="d">{m.text}</span></span>
            </label>
          ))}
        </div>
        <div className="row"><div><label htmlFor={delayId}>Delay for “slow” (seconds)</label><input id={delayId} type="number" min={1} max={120} value={delay} onChange={(e) => setDelay(Number(e.target.value))} style={{ width: 90 }} /></div></div>
        {error && <div className="err" role="alert">{error}</div>}
      </section>
      <section className="panel" aria-labelledby="trust-h">
        <h2 id="trust-h">What this system trusts</h2>
        <p>Signing secrets pasted from the portal (HMAC) and/or the platform's published public keys (JWKS). A request is accepted when any of them verifies it.</p>
        <p>Secrets: <span className="mono">{(trust.data?.secrets ?? []).join(', ') || 'none'}</span> · JWKS: <span className="mono">{trust.data?.jwksUrl ?? 'none'}</span></p>
        <div className="row">
          <div><label htmlFor={secretId}>Signing secret (whsec_…)</label><input id={secretId} type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="off" /></div>
          <div><label htmlFor={jwksId}>Public key set (JWKS URL)</label><input id={jwksId} value={jwks} onChange={(e) => setJwks(e.target.value)} /></div>
          <button type="button" className="btn quiet" onClick={() => void saveTrust()}>Save</button>
        </div>
        <hr style={{ borderColor: 'var(--border)', margin: '14px 0' }} />
        <button type="button" className="btn quiet" onClick={() => void reset()}>Forget everything (fresh demo run)</button>
      </section>
    </div>
  );
}
