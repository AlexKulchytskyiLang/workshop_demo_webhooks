import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useId, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { playgroundApi } from '../api/advanced';
import { useApiVersions, useCatalog, useEndpoints } from '../api/queries';
import type { Scenario } from '../api/webhooks';
import { useCan } from '../auth/me';
import { usePageTitle } from '../lib/usePageTitle';
import { CodeBlock, NoAccess, RadioCards } from '../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../ui/Feedback';
import { Icon } from '../ui/Icon';

const SCENARIOS: { value: Scenario; title: string; description: string }[] = [
  { value: 'normal', title: 'Send the event', description: 'A correctly signed request. Your server should answer 2xx.' },
  { value: 'bad_signature', title: 'Tampered signature', description: 'A WRONG signature. A safe server rejects it with 401.' },
  { value: 'old_timestamp', title: '6-minute-old timestamp', description: 'Correctly signed but stale. A safe server rejects it: replay protection.' },
  { value: 'duplicate', title: 'Send twice with the same id', description: 'The same webhook-id twice. Your server must acknowledge both and act once.' },
];

function parseObject(text: string): { value?: Record<string, unknown>; error?: string } {
  try {
    const value: unknown = JSON.parse(text);
    if (typeof value !== 'object' || value === null || Array.isArray(value)) return { error: 'The event data must be a JSON object, like { "amount": 10 }.' };
    return { value: value as Record<string, unknown> };
  } catch (e) {
    return { error: `This is not valid JSON yet: ${e instanceof Error ? e.message : 'check the commas and quotes'}.` };
  }
}

/**
 * The Playground (UX-SPEC D7): pick an event and one of your endpoints, edit the data, and send it through exactly the code that sends real events.
 * The negative tests prove your server is safe. Test events are tagged, never counted in the numbers and never trigger auto-disable.
 */
export function Playground() {
  usePageTitle('Playground');
  const can = useCan();
  const toast = useToast();
  const catalog = useCatalog();
  const endpoints = useEndpoints();
  const [endpointId, setEndpointId] = useState('');
  const [type, setType] = useState('invoice.paid');
  const [scenario, setScenario] = useState<Scenario>('normal');
  const [version, setVersion] = useState('');
  const versions = useApiVersions();
  const versionId = useId();
  const [filter, setFilter] = useState('');
  const [text, setText] = useState('');
  const endpointSelect = useId();
  const editorId = useId();
  const filterId = useId();

  const items = endpoints.data?.items;
  useEffect(() => {
    if (!endpointId && items && items.length > 0) setEndpointId((items.find((i) => i.summary.status === 'active') ?? items[0])!.summary.id);
  }, [items, endpointId]);

  const current = items?.find((i) => i.summary.id === endpointId)?.summary.apiVersion ?? '2026-11-01';
  const chosen = version || current;
  const sample = useQuery({ queryKey: ['sample', type, chosen], queryFn: ({ signal }) => playgroundApi.sample(type, chosen, signal), staleTime: Infinity });
  useEffect(() => {
    if (sample.data) setText(JSON.stringify(sample.data.data, null, 2));
  }, [sample.data]);

  const parsed = useMemo(() => parseObject(text), [text]);
  const send = useMutation({
    mutationFn: () => playgroundApi.send(endpointId, type, scenario, parsed.value, chosen),
    onSuccess: (r) => toast.show(r.ok ? 'Test passed' : 'Test finished: see the checklist'),
    onError: (e) => toast.show(errorText(e)),
  });

  const categories = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return (catalog.data?.categories ?? [])
      .map((c) => ({ ...c, events: c.events.filter((e) => q === '' || e.type.includes(q) || e.description.toLowerCase().includes(q)) }))
      .filter((c) => c.events.length > 0);
  }, [catalog.data, filter]);

  const noEndpoints = endpoints.isSuccess && (items?.length ?? 0) === 0;
  const result = send.data;
  const edited = sample.data !== undefined && text !== JSON.stringify(sample.data.data, null, 2);

  return (
    <>
      <div className="page-h">
        <div>
          <h1>Playground</h1>
          <p className="sub">Try an event on one of your endpoints before real traffic depends on it. Edit the data, then send.</p>
        </div>
      </div>

      {!can('endpoints:write') && <NoAccess what="send test events (you can look around)" />}
      {noEndpoints && (
        <div className="banner blue" role="note"><Icon name="info" /><div className="grow">You have no endpoint to send to yet. <Link to="/endpoints/new">Add an endpoint</Link> first.</div></div>
      )}

      <div className="pg">
        <section className="card" aria-labelledby="pg-events">
          <div className="hd"><h2 id="pg-events">Event</h2></div>
          <div className="bd">
            <label className="f" htmlFor={filterId}>Find an event</label>
            <input id={filterId} className="input" type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="invoice, payment, kyc…" autoComplete="off" />
          </div>
          <div className="list">
            {catalog.isPending && <div className="bd"><Loading what="the event list" /></div>}
            {categories.map((c) => (
              <div key={c.name}>
                <div className="pcat">{c.name}</div>
                {c.events.map((e) => (
                  <button key={e.type} type="button" className={`pe${type === e.type ? ' on' : ''}`} aria-pressed={type === e.type} onClick={() => setType(e.type)}>
                    {e.type}{e.containsPii && <span className="tag pii">personal data</span>}
                  </button>
                ))}
              </div>
            ))}
            {categories.length === 0 && !catalog.isPending && <p className="help" style={{ padding: 12 }}>No event matches “{filter}”.</p>}
          </div>
        </section>

        <div className="stack">
          <section className="card" aria-labelledby="pg-data">
            <div className="hd">
              <h2 id="pg-data">Data for <span className="mono">{type}</span></h2>
              <button type="button" className="btn sm" disabled={!edited} onClick={() => sample.data && setText(JSON.stringify(sample.data.data, null, 2))}>Reset to sample</button>
            </div>
            <div className="bd">
              {sample.data && <p className="help" style={{ marginTop: 0, marginBottom: 10 }}>{sample.data.description}{sample.data.note ? ` ${sample.data.note}` : ''}</p>}
              <div className="field">
                <label className="f" htmlFor={editorId}>Event data (JSON)</label>
                <textarea id={editorId} className={`json-editor${parsed.error ? ' bad' : ''}`} value={text} onChange={(e) => setText(e.target.value)} spellCheck={false} aria-invalid={parsed.error !== undefined} aria-describedby={`${editorId}-help`} />
                <div id={`${editorId}-help`}>
                  {parsed.error ? <div className="error-text" role="alert">{parsed.error}</div> : <div className="help">The envelope (id, type, time) is added for you and the request is signed exactly like a real one.</div>}
                </div>
              </div>

              <div className="field">
                <label className="f" htmlFor={endpointSelect}>Send to</label>
                <select id={endpointSelect} className="select" value={endpointId} onChange={(e) => setEndpointId(e.target.value)} disabled={noEndpoints}>
                  {(items ?? []).map((i) => <option key={i.summary.id} value={i.summary.id}>{i.summary.name} · {i.summary.environment}</option>)}
                </select>
              </div>

              <div className="field">
                <label className="f" htmlFor={versionId}>Payload version</label>
                <select id={versionId} className="select" value={chosen} onChange={(e) => setVersion(e.target.value)}>
                  {(versions.data ?? []).map((v) => <option key={v.version} value={v.version}>{v.version} · {v.status}{v.version === current ? ' (pinned on this endpoint)' : ''}</option>)}
                </select>
                <div className="help">The sample and the request use this version. Switching the editor starts from that version's sample.</div>
              </div>

              <RadioCards legend="What to test" name="pg-scenario" value={scenario} options={SCENARIOS} onChange={setScenario} columns={2} />

              <div className="row" style={{ marginTop: 12 }}>
                <button type="button" className="btn pri" disabled={!can('endpoints:write') || !endpointId || parsed.error !== undefined || send.isPending} onClick={() => send.mutate()}>
                  <Icon name="send" />{send.isPending ? 'Sending…' : 'Send test event'}
                </button>
                <span className="help" style={{ margin: 0 }}>Test events carry <span className="mono">evt_test_</span> ids and are never counted in your numbers.</span>
              </div>
            </div>
          </section>

          <div aria-live="polite">
            {send.isError && <ErrorBanner error={send.error} />}
            {result && (
              <section className="card" aria-labelledby="pg-result" data-testid="playground-result">
                <div className="hd">
                  <h2 id="pg-result">Result</h2>
                  <span className={`pill ${result.ok ? 'green' : 'red'}`}>{result.ok ? (result.scenario === 'normal' || result.scenario === 'duplicate' ? 'Passed' : 'Negative test passed') : 'Needs attention'}</span>
                </div>
                <div className="bd stack">
                  <div className="row wrap gap">
                    <div><div className="muted sm">Your server answered</div><div className="stat">{result.statusCode ?? 'nothing'}</div></div>
                    <div><div className="muted sm">Took</div><div className="stat">{result.durationMs} ms</div></div>
                    {result.secondStatusCode !== null && <div><div className="muted sm">Second send</div><div className="stat">{result.secondStatusCode}</div></div>}
                  </div>
                  <ul className="checks" aria-label="Checklist">
                    {result.checks.map((c, i) => (
                      <li key={i}><span className={c.status === 'bad' ? 'bad' : c.status === 'warn' ? 'warn' : 'ok'} aria-hidden="true">{c.status === 'ok' ? '✓' : c.status === 'warn' ? '!' : '✕'}</span><span><span className="sr-only">{c.status === 'ok' ? 'Passed: ' : c.status === 'warn' ? 'Warning: ' : 'Problem: '}</span>{c.text}</span></li>
                    ))}
                  </ul>
                  <h3>Request we sent</h3>
                  <table className="hdrs"><tbody>
                    {Object.entries(result.request.headers).map(([k, v]) => <tr key={k}><td>{k}</td><td>{v}</td></tr>)}
                  </tbody></table>
                  <CodeBlock code={result.request.body} label="Request body" />
                  {result.responseSnippet && <><h3>Your server’s reply</h3><CodeBlock code={result.responseSnippet} label="Response body" /></>}
                </div>
              </section>
            )}
          </div>
        </div>
      </div>
    </>
  );
}
