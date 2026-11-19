import { useState } from 'react';
import { Link, NavLink, Outlet, useOutletContext, useParams, useSearchParams } from 'react-router-dom';
import { ApiError } from '../../api/client';
import { useEndpoint, useEndpointMutation } from '../../api/queries';
import { webhooksApi, type EndpointDetail } from '../../api/webhooks';
import { useCan } from '../../auth/me';
import { TestDialog, VerifyDialog } from '../../features/EndpointDialogs';
import { usePageTitle } from '../../lib/usePageTitle';
import { EnvTag, StatusPill } from '../../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../../ui/Feedback';
import { Icon } from '../../ui/Icon';

export interface EndpointCtx {
  detail: EndpointDetail;
  canWrite: boolean;
  canDelete: boolean;
  canReveal: boolean;
}
export const useEndpointCtx = () => useOutletContext<EndpointCtx>();

const SIGNING_LABEL = { hmac: 'HMAC-SHA256', es256: 'ES256', both: 'HMAC + ES256' } as const;

/** The workspace of one endpoint: header, status, the go-live checklist while it is new, and the four tabs. */
export function EndpointLayout() {
  const { id = '' } = useParams();
  const [params] = useSearchParams();
  const can = useCan();
  const toast = useToast();
  const query = useEndpoint(id);
  const [verifying, setVerifying] = useState(false);
  const [testing, setTesting] = useState(false);
  const [tested, setTested] = useState(false);
  const pause = useEndpointMutation(id, () => webhooksApi.pause(id));
  const resume = useEndpointMutation(id, () => webhooksApi.resume(id));
  usePageTitle(query.data?.endpoint.name ?? 'Endpoint');

  if (query.isPending) return <Loading what="the endpoint" />;
  if (query.isError) {
    const missing = query.error instanceof ApiError && query.error.status === 404;
    return (
      <>
        <div className="crumb"><Link to="/">Webhooks</Link> / Endpoints</div>
        {missing
          ? <div className="banner amber" role="alert"><div className="grow"><b>This endpoint does not exist</b>, or it belongs to another organisation. <Link to="/">Back to your endpoints</Link>.</div></div>
          : <ErrorBanner error={query.error} onRetry={() => void query.refetch()} />}
      </>
    );
  }

  const detail = query.data;
  const e = detail.endpoint;
  const ctx: EndpointCtx = { detail, canWrite: can('endpoints:write'), canDelete: can('endpoints:delete'), canReveal: can('secrets:reveal') };
  const fresh = params.get('welcome') === '1' || e.status === 'pending';
  const events = detail.subscriptions.map((s) => s.eventType);
  const togglable = ctx.canWrite && (e.status === 'active' || e.status === 'pending' || e.status === 'paused');

  return (
    <>
      <div className="crumb"><Link to="/">Webhooks</Link> / Endpoints / <b style={{ color: 'var(--text)' }}>{e.name}</b></div>
      <div className="ep-h">
        <div>
          <div className="row wrap"><h1>{e.name}</h1><StatusPill health={e.health} /></div>
          <div className="meta">
            <span className="chip url-chip" title={e.url}>{e.url}</span>
            <EnvTag environment={e.environment} />
            <span className="tag">API {e.apiVersion}</span>
            <span className="tag">{SIGNING_LABEL[e.signing]}</span>
            <span className="tag">{e.payloadStyle === 'auto' ? 'Payload: event default' : e.payloadStyle === 'thin' ? 'Payload: thin' : 'Payload: snapshot'}</span>
          </div>
        </div>
        <div className="row wrap">
          {togglable && (e.status === 'paused' && e.backlog.held > 0
            ? <Link className="btn" to="/replay"><Icon name="play" />Resume…</Link>
            : e.status === 'paused'
            ? <button type="button" className="btn" disabled={resume.isPending} onClick={() => resume.mutate(undefined, { onSuccess: () => toast.show('Resumed. Held events are delivered at a steady pace.'), onError: (x) => toast.show(errorText(x)) })}><Icon name="play" />Resume</button>
            : <button type="button" className="btn" disabled={pause.isPending} onClick={() => pause.mutate(undefined, { onSuccess: () => toast.show('Paused. New events are held, nothing is lost.'), onError: (x) => toast.show(errorText(x)) })}><Icon name="pause" />Pause</button>)}
          {ctx.canWrite && e.status === 'pending' && <button type="button" className="btn pri" onClick={() => setVerifying(true)}><Icon name="check" />Verify URL</button>}
          {ctx.canWrite && <button type="button" className="btn" onClick={() => setTesting(true)}><Icon name="send" />Send test</button>}
        </div>
      </div>

      {fresh && <GoLive detail={detail} tested={tested} onVerify={() => setVerifying(true)} onTest={() => setTesting(true)} canWrite={ctx.canWrite} />}
      {e.health === 'auto_disabled' && (
        <div className="banner red" role="alert"><Icon name="alert" /><div className="grow"><b>This endpoint was disabled automatically.</b> {e.backlog.held > 0 ? `${e.backlog.held} events are held for you. ` : ''}Fix the problem on your side, verify the URL, then resume it.</div></div>
      )}

      <nav className="tabs" aria-label="Endpoint sections">
        <NavLink to="events" className={({ isActive }) => (isActive ? 'on' : undefined)}>Events &amp; filters</NavLink>
        <NavLink to="security" className={({ isActive }) => (isActive ? 'on' : undefined)}>Security</NavLink>
        <NavLink to="delivery" className={({ isActive }) => (isActive ? 'on' : undefined)}>Delivery &amp; resilience</NavLink>
        <NavLink to="settings" className={({ isActive }) => (isActive ? 'on' : undefined)}>Settings</NavLink>
        <NavLink to="logs" className={({ isActive }) => (isActive ? 'on' : undefined)}>Logs</NavLink>
      </nav>

      <Outlet context={ctx} />

      <VerifyDialog endpointId={id} open={verifying} onClose={() => setVerifying(false)} />
      {testing && <TestDialog endpointId={id} events={events} open onClose={() => setTesting(false)} onPassed={() => setTested(true)} />}
    </>
  );
}

function GoLive({ detail, tested, onVerify, onTest, canWrite }: { detail: EndpointDetail; tested: boolean; onVerify: () => void; onTest: () => void; canWrite: boolean }) {
  const e = detail.endpoint;
  const verified = e.verifiedAt !== null;
  const live = e.status === 'active';
  return (
    <section className="card golive" aria-labelledby="golive-h">
      <div className="hd"><h2 id="golive-h">Go-live checklist</h2><span className="muted sm">{live ? 'Live: real events are being delivered' : 'No real events are sent until your server passes the first check'}</span></div>
      <div className="bd">
        <ol className="steps">
          <li className="done"><div><b>Create endpoint</b><div className="help">Done. Name, URL and events are saved.</div></div></li>
          <li className={verified ? 'done' : 'cur'}>
            <div><b>Verify the URL</b><div className="help">We send a signed <span className="mono">webhook.ping</span>; your server answers 2xx. Your server needs the signing secret first (Security tab).</div>
              {!verified && canWrite && <button type="button" className="btn sm pri" onClick={onVerify}>Verify URL</button>}</div>
          </li>
          <li className={tested ? 'done' : verified ? 'cur' : undefined}>
            <div><b>Send a test event</b><div className="help">A realistic payload, including tests that prove your server is safe.</div>
              {verified && !tested && canWrite && <button type="button" className="btn sm" onClick={onTest}>Send test event</button>}</div>
          </li>
          <li><div><b>Check your signature code</b><div className="help">Copy a snippet for C#, Node, Python or cURL from the <Link to="security">Security tab</Link>.</div></div></li>
          <li className={live ? 'done' : undefined}><div><b>Go live</b><div className="help">{live ? 'Real events are flowing. You can pause any time.' : 'Happens automatically once the URL is verified.'}</div></div></li>
        </ol>
      </div>
    </section>
  );
}

