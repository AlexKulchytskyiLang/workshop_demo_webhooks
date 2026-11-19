import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useInvalidateEndpoint } from '../api/queries';
import { webhooksApi, type PingResult, type Scenario, type TestResult } from '../api/webhooks';
import { CodeBlock, RadioCards } from '../ui/Display';
import { ErrorBanner } from '../ui/Feedback';
import { Icon } from '../ui/Icon';
import { Modal } from '../ui/Modal';

const SCENARIOS: { value: Scenario; title: string; description: string }[] = [
  { value: 'normal', title: 'Send a normal test event', description: 'A correctly signed request. Your server should answer 2xx.' },
  { value: 'bad_signature', title: 'Tampered signature', description: 'A request with a WRONG signature. A safe server rejects it (401).' },
  { value: 'old_timestamp', title: '6-minute-old timestamp', description: 'Correctly signed but stale. A safe server rejects it: that is replay protection.' },
  { value: 'duplicate', title: 'Send twice with the same id', description: 'The same webhook-id twice. Your server must acknowledge both and act once.' },
];

/** The verification step: one signed webhook.ping. A 2xx makes the endpoint active; anything else is explained in words. */
export function VerifyDialog({ endpointId, open, onClose }: { endpointId: string; open: boolean; onClose: () => void }) {
  const invalidate = useInvalidateEndpoint();
  const ping = useMutation<PingResult, Error>({ mutationFn: () => webhooksApi.ping(endpointId), onSuccess: () => invalidate(endpointId) });
  const result = ping.data;

  return (
    <Modal
      open={open}
      title="Verify the endpoint"
      onClose={() => { ping.reset(); onClose(); }}
      footer={
        <>
          <button type="button" className="btn" onClick={() => { ping.reset(); onClose(); }}>{result?.ok ? 'Done' : 'Close'}</button>
          {!result?.ok && <button type="button" className="btn pri" disabled={ping.isPending} onClick={() => ping.mutate()}>{ping.isPending ? 'Sending…' : result ? 'Try again' : 'Send verification request'}</button>}
        </>
      }
    >
      {!result && !ping.isPending && !ping.isError && (
        <p>We will send one signed <span className="mono">webhook.ping</span> to your address. Answer with any 2xx and the endpoint goes live. Before that, your server needs the signing secret: see the <Link to={`/endpoints/${endpointId}/security`} onClick={onClose}>Security tab</Link>.</p>
      )}
      {ping.isPending && <p role="status">Sending the verification request…</p>}
      {ping.isError && <ErrorBanner error={ping.error} />}
      {result && (
        <div aria-live="polite">
          <div className={`banner ${result.ok ? 'green' : 'red'}`} role={result.ok ? 'status' : 'alert'}>
            <Icon name={result.ok ? 'check' : 'alert'} />
            <div className="grow">
              {result.ok
                ? <><b>Verified.</b> Your server answered {result.statusCode} in {result.durationMs} ms. The endpoint is active and real events will be delivered.</>
                : <><b>Not verified.</b> {result.hint}</>}
            </div>
          </div>
          {!result.ok && (
            <dl className="facts" style={{ gridTemplateColumns: '130px 1fr' }}>
              <dt>Answer</dt><dd>{result.statusCode ?? 'no answer'}{result.timedOut ? ' (timed out)' : ''}</dd>
              {result.error && <><dt>Detail</dt><dd>{result.error}</dd></>}
              {result.responseSnippet && <><dt>Your reply</dt><dd className="mono">{result.responseSnippet}</dd></>}
              <dt>Request id</dt><dd className="mono">{result.requestId}</dd>
            </dl>
          )}
        </div>
      )}
    </Modal>
  );
}

/** A test event with an optional negative scenario. Shows what was sent (secrets hidden), what came back, and a checklist of what it proves. */
export function TestDialog({ endpointId, events, open, onClose, onPassed }: { endpointId: string; events: string[]; open: boolean; onClose: () => void; onPassed?: () => void }) {
  const [eventType, setEventType] = useState(events[0] ?? '');
  const [scenario, setScenario] = useState<Scenario>('normal');
  const test = useMutation<TestResult, Error>({
    mutationFn: () => webhooksApi.test(endpointId, eventType || events[0]!, scenario),
    onSuccess: (r) => { if (r.ok) onPassed?.(); },
  });
  const r = test.data;
  const close = () => { test.reset(); onClose(); };

  return (
    <Modal
      open={open}
      title="Send a test event"
      wide
      onClose={close}
      footer={
        <>
          <button type="button" className="btn" onClick={close}>Close</button>
          <button type="button" className="btn pri" disabled={test.isPending || events.length === 0} onClick={() => test.mutate()}><Icon name="send" />{test.isPending ? 'Sending…' : 'Send test event'}</button>
        </>
      }
    >
      <p className="help" style={{ marginTop: 0 }}>Test events carry <span className="mono">evt_test_…</span> ids and <span className="mono">"test": true</span>. They never count in your numbers and never trigger auto-disable.</p>
      <div className="field">
        <label className="f" htmlFor="test-event">Event</label>
        <select id="test-event" className="select mono" value={eventType || events[0]} onChange={(e) => setEventType(e.target.value)}>
          {events.map((e) => <option key={e} value={e}>{e}</option>)}
        </select>
      </div>
      <RadioCards legend="Test scenario" name="scenario" value={scenario} onChange={setScenario} columns={2}
        options={SCENARIOS.map((s) => ({ value: s.value, title: s.title, description: s.description }))} />

      {test.isError && <ErrorBanner error={test.error} />}
      {r && (
        <div className="result" aria-live="polite" style={{ marginTop: 14 }} data-testid="test-result">
          <div className="rh">
            <span className="stat">{r.statusCode ?? 'No answer'}</span>
            <span className="muted">{r.durationMs} ms</span>
            <span className={`pill ${r.ok ? 'green' : 'red'}`}>{r.ok ? (r.scenario === 'normal' || r.scenario === 'duplicate' ? 'Passed' : 'Negative test passed') : 'Needs attention'}</span>
          </div>
          <div className="bd">
            <ul className="checks">
              {r.checks.map((c, i) => (
                <li key={i}>
                  <span className={c.status === 'ok' ? 'ok' : c.status === 'warn' ? 'warn' : 'bad'}><Icon name={c.status === 'ok' ? 'check' : 'alert'} /></span>
                  <span><span className="sr-only">{c.status === 'ok' ? 'Passed' : c.status === 'warn' ? 'Warning' : 'Problem'}: </span>{c.text}</span>
                </li>
              ))}
            </ul>
            <div className="grid g2" style={{ marginTop: 12 }}>
              <div>
                <div className="sm muted">Request sent</div>
                <CodeBlock label="Request sent" code={`POST ${r.request.url}\n${Object.entries(r.request.headers).map(([k, v]) => `${k}: ${v}`).join('\n')}\n\n${pretty(r.request.body)}`} />
              </div>
              <div>
                <div className="sm muted">Response from your server</div>
                <CodeBlock label="Response from your server" code={r.error ? `Error: ${r.error}` : `${r.statusCode}\n${r.responseSnippet ?? '(empty body)'}`} />
              </div>
            </div>
          </div>
        </div>
      )}
      {!r && !test.isPending && events.length === 0 && <p className="error-text">Subscribe to at least one event first.</p>}
      {test.isPending && <p role="status" className="help">Sending…</p>}
    </Modal>
  );
}

function pretty(json: string): string {
  try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; }
}
