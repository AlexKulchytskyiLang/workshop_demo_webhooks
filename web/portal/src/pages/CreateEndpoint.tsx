import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useCatalog, useInvalidateEndpoint } from '../api/queries';
import { webhooksApi, type Environment } from '../api/webhooks';
import { useCan } from '../auth/me';
import { EventPicker } from '../features/EventPicker';
import { UrlChecks } from '../features/UrlChecks';
import { onlyKnown, snapshotBlockedReason } from '../lib/events';
import { usePageTitle } from '../lib/usePageTitle';
import { CodeBlock } from '../ui/Display';
import { ErrorBanner, Loading } from '../ui/Feedback';

const TEMPLATE_LABEL: Record<string, string> = { custom: 'Custom HTTPS', erp: 'ERP / accounting', flow: 'Power Automate / Logic Apps', chat: 'Chat alert relay', dwh: 'Data warehouse' };
const ENVIRONMENTS: { value: Environment; label: string }[] = [{ value: 'production', label: 'Production' }, { value: 'staging', label: 'Staging' }, { value: 'development', label: 'Development' }];

/** Three things to start (name, address, events); everything else has a safe default and can be changed on the endpoint's own page. */
export function CreateEndpoint() {
  usePageTitle('Add endpoint');
  const can = useCan();
  const navigate = useNavigate();
  const catalog = useCatalog();
  const invalidate = useInvalidateEndpoint();

  const [template, setTemplate] = useState('custom');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [environment, setEnvironment] = useState<Environment>('production');
  const [url, setUrl] = useState('');
  const [urlOk, setUrlOk] = useState<boolean | null>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [touched, setTouched] = useState(false);

  const create = useMutation({
    mutationFn: () => webhooksApi.createEndpoint({ name: name.trim(), url: url.trim(), description: description.trim() || undefined, environment, events: [...selected], payloadStyle: 'auto' }),
    onSuccess: async (created) => {
      await invalidate();
      navigate(`/endpoints/${created.id}?welcome=1`);
    },
  });

  if (!can('endpoints:write')) return <Navigate to="/" replace />;

  const applyTemplate = (key: string) => {
    setTemplate(key);
    const t = catalog.data?.templates[key];
    if (!t || !catalog.data) return;
    if (t.name) setName(t.name);
    if (t.description) setDescription(t.description);
    if (t.events.length > 0) setSelected(new Set(onlyKnown(catalog.data, t.events)));
  };

  const problems = [
    name.trim() === '' ? 'Give the endpoint a name.' : null,
    url.trim() === '' ? 'Enter the address of your server.' : urlOk === false ? 'Fix the address first (see the checks above).' : null,
    selected.size === 0 ? 'Choose at least one event.' : null,
  ].filter((p): p is string => p !== null);
  const blocked = catalog.data ? snapshotBlockedReason(catalog.data, selected) : null;

  return (
    <>
      <div className="crumb"><Link to="/">Webhooks</Link> / Endpoints / <b style={{ color: 'var(--text)' }}>New endpoint</b></div>
      <div className="page-h"><div><h1>Add endpoint</h1><p className="sub">Three things to start. Safe defaults for everything else, editable any time.</p></div></div>

      <form
        className="split"
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          setTouched(true);
          if (problems.length === 0) create.mutate();
        }}
      >
        <div>
          {catalog.data && (
            <fieldset className="rcgroup" style={{ marginBottom: 14 }}>
              <legend>Start from a template</legend>
              <div className="row wrap">
                {Object.keys(TEMPLATE_LABEL).filter((k) => k === 'custom' || catalog.data.templates[k]).map((k) => (
                  <label key={k} className={`rc${template === k ? ' on' : ''}`} style={{ padding: '6px 12px' }}>
                    <input type="radio" name="template" value={k} checked={template === k} onChange={() => applyTemplate(k)} />
                    <span>{TEMPLATE_LABEL[k]}</span>
                  </label>
                ))}
              </div>
            </fieldset>
          )}

          <section className="card" aria-labelledby="c1">
            <div className="hd"><h2 id="c1">1 · Identity &amp; destination</h2></div>
            <div className="bd">
              <div className="grid g2">
                <div className="field">
                  <label className="f" htmlFor="cr-name">Name</label>
                  <input id="cr-name" className={`input${touched && name.trim() === '' ? ' bad' : ''}`} value={name} onChange={(e) => setName(e.target.value)} maxLength={120} autoComplete="off" />
                </div>
                <div className="field">
                  <label className="f" htmlFor="cr-env">Environment</label>
                  <select id="cr-env" className="select" value={environment} onChange={(e) => setEnvironment(e.target.value as Environment)}>
                    {ENVIRONMENTS.map((e) => <option key={e.value} value={e.value}>{e.label}</option>)}
                  </select>
                  <div className="help">Shown as a tag so people do not mix up staging and production.</div>
                </div>
              </div>
              <div className="field">
                <label className="f" htmlFor="cr-desc">Description <span className="muted" style={{ fontWeight: 400 }}>(optional)</span></label>
                <input id="cr-desc" className="input" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={400} />
              </div>
              <div className="field">
                <label className="f" htmlFor="cr-url">Endpoint URL (HTTPS POST)</label>
                <input id="cr-url" className={`input mono${urlOk === true ? ' ok' : urlOk === false ? ' bad' : ''}`} value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://erp.example.com/hooks/invoicehub" autoComplete="off" spellCheck={false} aria-describedby="cr-url-checks" />
                <div id="cr-url-checks"><UrlChecks url={url} onResult={setUrlOk} /></div>
              </div>
            </div>
          </section>

          <section className="card" aria-labelledby="c2">
            <div className="hd"><h2 id="c2">2 · Events</h2></div>
            <div className="bd">
              {catalog.isPending && <Loading what="the event catalogue" />}
              {catalog.isError && <ErrorBanner error={catalog.error} onRetry={() => void catalog.refetch()} />}
              {catalog.data && <EventPicker catalog={catalog.data} selected={selected} onChange={setSelected} initiallyOpen={['Invoices']} />}
              <div className="help">Click a category checkbox to select all its events. Add filters (for example <span className="mono">amount &gt; 5000</span>) on the next screen.</div>
              {blocked && <div className="note" style={{ marginTop: 8 }}>{blocked}</div>}
            </div>
          </section>

          <section className="card" aria-labelledby="c3">
            <div className="hd"><h2 id="c3">3 · Secure defaults</h2><span className="muted sm">Review now or change later</span></div>
            <div className="bd table-wrap">
              <table className="sch">
                <tbody>
                  <tr><th scope="row" className="muted" style={{ width: 210 }}>Transport</th><td>HTTPS only (TLS 1.2 or newer). Redirects are never followed.</td></tr>
                  <tr><th scope="row" className="muted">Signature</th><td>HMAC-SHA256. A secret is generated when you create the endpoint; ES256 (public key) is available on the Security tab.</td></tr>
                  <tr><th scope="row" className="muted">Replay protection</th><td><b>Always on</b>: a unique <span className="mono">webhook-id</span> and a <span className="mono">webhook-timestamp</span> on every request.</td></tr>
                  <tr><th scope="row" className="muted">Retries</th><td>Standard: up to 72 hours, growing delays with jitter.</td></tr>
                  <tr><th scope="row" className="muted">Protection</th><td>Circuit breaker after 5 failures in a row; auto-disable after 100 within 24 hours. Events are held, never dropped.</td></tr>
                </tbody>
              </table>
            </div>
          </section>

          {create.isError && <ApiProblem error={create.error} />}
          {touched && problems.length > 0 && (
            <div className="banner amber" role="alert"><div className="grow"><b>Almost there.</b><ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>{problems.map((p) => <li key={p}>{p}</li>)}</ul></div></div>
          )}
          <div className="row" style={{ marginTop: 16 }}>
            <button type="submit" className="btn pri" disabled={create.isPending}>{create.isPending ? 'Creating…' : 'Create endpoint'}</button>
            <Link className="btn gh" to="/">Cancel</Link>
            <span className="muted sm">Created as <b>Pending verification</b>: no real events are sent until you pass the first test.</span>
          </div>
        </div>

        <aside className="sticky" aria-label="Help">
          <div className="card">
            <div className="hd"><h2>Go-live checklist</h2></div>
            <div className="bd">
              <ol className="steps">
                <li className="cur"><div><b>Create endpoint</b><div className="help">Name, URL and events</div></div></li>
                <li><div><b>Verify the URL</b><div className="help">We send a signed <span className="mono">webhook.ping</span>; you return 2xx</div></div></li>
                <li><div><b>Send a test event</b><div className="help">A realistic payload, including tests that prove your server is safe</div></div></li>
                <li><div><b>Check your signature code</b><div className="help">Copy a snippet for C#, Node, Python or cURL</div></div></li>
                <li><div><b>Go live</b><div className="help">Real events start flowing. You can pause any time</div></div></li>
              </ol>
            </div>
          </div>
          <div className="card">
            <div className="hd"><h3>What your server will receive</h3></div>
            <div className="bd">
              <CodeBlock label="Example request" copy={false} code={'POST /hooks/invoicehub HTTP/1.1\ncontent-type: application/json\nwebhook-id: evt_01JC4Z8Q2K7M\nwebhook-timestamp: 1762940467\nwebhook-signature: v1,K5oZfzN0rW1...'} />
              <div className="help">The <span className="mono">webhook-id</span> is stable across retries: use it for de-duplication. Reject timestamps older than 5 minutes.</div>
            </div>
          </div>
        </aside>
      </form>
    </>
  );
}

function ApiProblem({ error }: { error: unknown }) {
  const e = error instanceof ApiError ? error : null;
  return <ErrorBanner error={e ? new ApiError(e.status, e.code, e.message) : error} />;
}
