import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useEndpointMutation, useEndpoints, useEgressPolicy, useOverview } from '../api/queries';
import { webhooksApi, type EndpointListItem } from '../api/webhooks';
import { useCan } from '../auth/me';
import { formatDuration, formatNumber, HEALTH_LABEL, shortUrl } from '../lib/format';
import { usePageTitle } from '../lib/usePageTitle';
import { EnvTag, Kpi, Sparkline, StatusPill } from '../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../ui/Feedback';
import { Icon } from '../ui/Icon';
import { ConfirmTyped } from '../ui/Modal';

const SPARK_TONE: Record<string, 'green' | 'amber' | 'red' | 'muted'> = {
  healthy: 'green', degraded: 'amber', circuit_open: 'amber', auto_disabled: 'red', disabled: 'red', paused: 'muted', pending_verification: 'muted',
};

export function Overview() {
  usePageTitle('Webhooks');
  const can = useCan();
  const overview = useOverview();
  const endpoints = useEndpoints();
  const egress = useEgressPolicy();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('all');
  const [environment, setEnvironment] = useState('all');
  const [deleting, setDeleting] = useState<EndpointListItem | null>(null);

  const items = endpoints.data?.items ?? [];
  const shown = useMemo(() => items.filter((i) => {
    const s = i.summary;
    const q = search.trim().toLowerCase();
    return (status === 'all' || s.health === status)
      && (environment === 'all' || s.environment === environment)
      && (q === '' || s.name.toLowerCase().includes(q) || s.url.toLowerCase().includes(q) || i.events.some((e) => e.includes(q)));
  }).sort((a, b) => severity(b.summary.health) - severity(a.summary.health) || a.summary.name.localeCompare(b.summary.name)), [items, search, status, environment]);

  return (
    <>
      <div className="page-h">
        <div>
          <h1>Webhooks</h1>
          <p className="sub">Send InvoiceHub events to your systems in real time. Signed, retried, and fully inspectable.</p>
        </div>
        {can('endpoints:write') && <Link className="btn pri" to="/endpoints/new"><Icon name="plus" />Add endpoint</Link>}
      </div>

      {egress.data?.notice && <div className="banner blue" role="note"><Icon name="info" /><div className="grow">{egress.data.notice}</div></div>}

      {overview.data && overview.data.attention.length > 0 && (
        <div className="attn" aria-label="Needs attention">
          {overview.data.attention.map((a) => (
            <div key={`${a.kind}-${a.endpointId}`} className={`banner ${a.kind === 'key_expiring' ? 'amber' : 'red'}`} role="alert">
              <Icon name="alert" />
              <div className="grow">{attentionText(a)}</div>
              <Link className="btn" to={`/endpoints/${a.endpointId}/${a.kind === 'key_expiring' ? 'security' : 'delivery'}`}>{a.kind === 'key_expiring' ? 'Review keys' : 'Review & resume'}</Link>
            </div>
          ))}
        </div>
      )}

      {overview.isError && <ErrorBanner error={overview.error} onRetry={() => void overview.refetch()} />}
      <section className="grid g5" style={{ marginBottom: 16 }} aria-label="Last 24 hours">
        {overview.isPending ? <Loading what="the numbers" /> : overview.data && <Kpis data={overview.data.kpis} />}
      </section>

      <section className="card" aria-labelledby="endpoints-h">
        <div className="hd">
          <h2 id="endpoints-h">Endpoints</h2>
          <div className="row wrap">
            <label className="sr-only" htmlFor="ep-search">Search endpoints</label>
            <input id="ep-search" type="search" className="input" style={{ width: 260 }} placeholder="Search name, URL or event type" value={search} onChange={(e) => setSearch(e.target.value)} />
            <label className="sr-only" htmlFor="ep-status">Status</label>
            <select id="ep-status" className="select" style={{ width: 170 }} value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="all">All statuses</option>
              {Object.entries(HEALTH_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
            <label className="sr-only" htmlFor="ep-env">Environment</label>
            <select id="ep-env" className="select" style={{ width: 170 }} value={environment} onChange={(e) => setEnvironment(e.target.value)}>
              <option value="all">All environments</option>
              <option value="production">Production</option>
              <option value="staging">Staging</option>
              <option value="development">Development</option>
            </select>
          </div>
        </div>

        {endpoints.isPending && <div className="bd"><Loading what="endpoints" /></div>}
        {endpoints.isError && <div className="bd"><ErrorBanner error={endpoints.error} onRetry={() => void endpoints.refetch()} /></div>}

        {endpoints.data && items.length === 0 && <EmptyState canCreate={can('endpoints:write')} />}

        {endpoints.data && items.length > 0 && (
          <div className="table-wrap">
            <table>
              <caption className="sr-only">Your webhook endpoints, problems first</caption>
              <thead>
                <tr><th scope="col">Status</th><th scope="col">Integration</th><th scope="col">Events</th><th scope="col">Version</th><th scope="col">24 h health</th><th scope="col"><span className="sr-only">Actions</span></th></tr>
              </thead>
              <tbody>
                {shown.map((item) => <Row key={item.summary.id} item={item} onDelete={() => setDeleting(item)} />)}
                {shown.length === 0 && <tr><td colSpan={6} className="muted">No endpoint matches these filters.</td></tr>}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {deleting && <DeleteDialog item={deleting} onClose={() => setDeleting(null)} />}
    </>
  );
}

const severity = (health: string) => ({ auto_disabled: 6, disabled: 5, circuit_open: 4, degraded: 3, pending_verification: 2, paused: 1, healthy: 0 }[health] ?? 0);

function attentionText(a: { kind: string; name: string; held: number }) {
  if (a.kind === 'key_expiring') return <><b>A signing key of “{a.name}” expires within 48 hours.</b> Receivers that still verify with it need the new key before then.</>;
  if (a.kind === 'circuit_open') return <><b>“{a.name}” has been failing for over an hour.</b> We are probing it with growing pauses. Nothing is lost: events wait for it.</>;
  return <><b>“{a.name}” was {a.kind === 'gone' ? 'disabled because your server answered 410 Gone' : 'disabled automatically after too many failures'}.</b> {a.held > 0 ? <><b>{formatNumber(a.held)} events are held</b> for you. </> : null}Nothing is lost.</>;
}

function Kpis({ data }: { data: NonNullable<ReturnType<typeof useOverview>['data']>['kpis'] }) {
  const trend = data.deliveriesPrevious24h > 0 ? Math.round(((data.deliveries24h - data.deliveriesPrevious24h) / data.deliveriesPrevious24h) * 100) : null;
  return (
    <>
      <Kpi label="Deliveries (24 h)" value={formatNumber(data.deliveries24h)} detail={trend === null ? 'no deliveries the day before' : `${trend >= 0 ? '▲' : '▼'} ${Math.abs(trend)} % vs the day before`} tone={trend !== null && trend >= 0 ? 'up' : 'muted'} />
      <Kpi label="Success rate" value={data.successRate === null ? 'n/a' : `${data.successRate} %`} detail={data.firstAttemptRate === null ? 'nothing finished yet' : `first attempt ${data.firstAttemptRate} %`} />
      <Kpi label="Avg response latency" value={data.avgLatencyMs === null ? 'n/a' : `${data.avgLatencyMs} ms`} detail={data.p95LatencyMs === null ? undefined : `p95 ${data.p95LatencyMs} ms`} />
      <Kpi label="Retrying / dead" value={`${data.retrying} / ${data.dead}`} detail={`${data.retrying} retrying · ${data.dead} dead`} tone={data.dead > 0 ? 'down' : 'muted'} />
      <Kpi label="Held" value={formatNumber(data.held)} detail={data.oldestHeldSeconds === null ? 'nothing is held' : `oldest ${formatDuration(data.oldestHeldSeconds)}`} tone={data.held > 0 ? 'down' : 'muted'} />
    </>
  );
}

function Row({ item, onDelete }: { item: EndpointListItem; onDelete: () => void }) {
  const can = useCan();
  const toast = useToast();
  const s = item.summary;
  const pause = useEndpointMutation(s.id, () => webhooksApi.pause(s.id));
  const resume = useEndpointMutation(s.id, () => webhooksApi.resume(s.id));
  const canToggle = can('endpoints:write') && (s.status === 'active' || s.status === 'pending' || s.status === 'paused');
  const detail = s.health === 'auto_disabled' ? `${s.consecutiveFailures} / ${s.autoDisableFailures} failures` : s.health === 'paused' ? 'new events are held' : s.health === 'circuit_open' ? 'retrying · circuit open' : undefined;

  return (
    <tr>
      <td><StatusPill health={s.health} detail={detail} /></td>
      <td>
        <Link className="name-link" to={`/endpoints/${s.id}`}>{s.name}</Link> <EnvTag environment={s.environment} />
        {s.description && <div className="muted sm">{s.description}</div>}
        <div className="mono sm muted" title={s.url}>{shortUrl(s.url)}</div>
      </td>
      <td>
        {item.events.slice(0, 2).map((e) => <span key={e} className="chip" style={{ marginRight: 4 }}>{e}</span>)}
        {item.events.length > 2 && <span className="chip more" title={item.events.slice(2).join(', ')}>+{item.events.length - 2} more</span>}
      </td>
      <td className="mono sm nw">{s.apiVersion}</td>
      <td>
        {item.successRate24h === null ? <span className="muted sm">no deliveries yet</span> : (
          <div className="row nw"><Sparkline series={item.series} tone={SPARK_TONE[s.health] ?? 'green'} /><b>{item.successRate24h} %</b></div>
        )}
      </td>
      <td>
        <div className="acts">
          {canToggle && (s.status === 'paused'
            ? <button type="button" className="btn icon gh" aria-label={`Resume ${s.name}`} title="Resume" disabled={resume.isPending} onClick={() => resume.mutate(undefined, { onSuccess: () => toast.show('Resumed. Held events are delivered at a steady pace.'), onError: (e) => toast.show(errorText(e)) })}><Icon name="play" /></button>
            : <button type="button" className="btn icon gh" aria-label={`Pause ${s.name}`} title="Pause" disabled={pause.isPending} onClick={() => pause.mutate(undefined, { onSuccess: () => toast.show('Paused. New events are held, nothing is lost. Resume any time.'), onError: (e) => toast.show(errorText(e)) })}><Icon name="pause" /></button>)}
          <Link className="btn icon gh" to={`/endpoints/${s.id}`} aria-label={`Open ${s.name}`} title="Open"><Icon name="edit" /></Link>
          {can('endpoints:delete') && <button type="button" className="btn icon gh" aria-label={`Delete ${s.name}`} title="Delete" onClick={onDelete}><Icon name="trash" /></button>}
        </div>
      </td>
    </tr>
  );
}

function DeleteDialog({ item, onClose }: { item: EndpointListItem; onClose: () => void }) {
  const toast = useToast();
  const s = item.summary;
  const remove = useEndpointMutation(s.id, () => webhooksApi.deleteEndpoint(s.id));
  const waiting = s.backlog.pending + s.backlog.retrying + s.backlog.held;
  return (
    <ConfirmTyped
      open
      title={`Delete “${s.name}”`}
      phrase={s.name}
      confirmLabel="Delete endpoint"
      busy={remove.isPending}
      error={remove.isError ? errorText(remove.error) : null}
      consequence={<>{waiting > 0 ? <><b>{formatNumber(waiting)} events</b> are waiting for this endpoint ({s.backlog.pending} pending, {s.backlog.retrying} retrying, {s.backlog.held} held) and will <b>not</b> be delivered.</> : 'No events are waiting for this endpoint.'} Its signing keys stop working at once. Delivery history stays for audit.</>}
      onClose={onClose}
      onConfirm={() => remove.mutate(undefined, { onSuccess: () => { toast.show(`“${s.name}” was deleted`); onClose(); } })}
    />
  );
}

function EmptyState({ canCreate }: { canCreate: boolean }) {
  return (
    <div className="bd">
      <h3>Send your first event in about ten minutes</h3>
      <ol className="steps" style={{ margin: '12px 0' }}>
        <li className="cur"><div><b>Add an endpoint</b><div className="help">Name, the HTTPS address of your server, and the events you want.</div></div></li>
        <li><div><b>Verify the address</b><div className="help">We send a signed <span className="mono">webhook.ping</span>; your server answers 2xx.</div></div></li>
        <li><div><b>Send a test event</b><div className="help">A realistic payload for any event, including tests that prove your server is safe.</div></div></li>
        <li><div><b>Go live</b><div className="help">Real events start flowing. Pause any time, nothing is lost.</div></div></li>
      </ol>
      {canCreate ? <Link className="btn pri" to="/endpoints/new"><Icon name="plus" />Add endpoint</Link> : <p className="muted">Ask an admin or developer to add the first endpoint.</p>}
    </div>
  );
}

