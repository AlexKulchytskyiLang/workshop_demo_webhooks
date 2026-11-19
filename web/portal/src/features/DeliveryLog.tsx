import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { logsApi, type DeliveryCounts, type DeliveryDetail, type DeliveryItem, type LogFilters } from '../api/advanced';
import { useCatalog, useEndpoints } from '../api/queries';
import { useCan } from '../auth/me';
import { formatUtc, timeAgo, timeUntil } from '../lib/format';
import { toCurl } from '../lib/curl';
import { useDebounced } from '../lib/useDebounced';
import { CodeBlock, CopyButton } from '../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../ui/Feedback';
import { Icon } from '../ui/Icon';
import { Drawer } from '../ui/Modal';

export const STATUS_LABEL: Record<string, string> = {
  succeeded: 'Delivered', retry_scheduled: 'Retrying', pending: 'Pending', in_flight: 'Sending', held: 'Held', dead: 'Dead',
};
export const STATUS_COLOUR: Record<string, string> = {
  succeeded: 'green', retry_scheduled: 'amber', pending: 'blue', in_flight: 'blue', held: 'gray', dead: 'red',
};

export function DeliveryStatusPill({ status }: { status: string }) {
  return <span className={`pill ${STATUS_COLOUR[status] ?? 'gray'}`}>{STATUS_LABEL[status] ?? status}</span>;
}

const RANGES = [
  { value: '1h', label: 'Last hour', hours: 1 },
  { value: '24h', label: 'Last 24 hours', hours: 24 },
  { value: '7d', label: 'Last 7 days', hours: 168 },
  { value: '30d', label: 'Last 30 days', hours: 720 },
  { value: 'all', label: 'All retained time', hours: 0 },
];

const CHIPS: { value: string; label: string; count: (c: DeliveryCounts) => number }[] = [
  { value: '', label: 'All', count: (c) => c.total },
  { value: 'succeeded', label: 'Delivered', count: (c) => c.succeeded },
  { value: 'retry_scheduled', label: 'Retrying', count: (c) => c.retrying },
  { value: 'pending,in_flight', label: 'Pending', count: (c) => c.pending },
  { value: 'held', label: 'Held', count: (c) => c.held },
  { value: 'dead', label: 'Dead', count: (c) => c.dead },
];

/**
 * The delivery log (UX-SPEC D8): one row per event and endpoint, newest first, with the attempts inside. The filters live in the address, so a link such as
 * "this endpoint's dead events" can be shared or bookmarked. Search is by id, business object or event name on purpose: payload content is not indexed.
 */
export function DeliveryLog({ endpointId }: { endpointId?: string }) {
  const [params, setParams] = useSearchParams();
  const catalog = useCatalog();
  const endpoints = useEndpoints();
  const [selected, setSelected] = useState<string | null>(null);
  const searchId = useId();

  const set = (key: string, value: string) => setParams((p) => {
    if (value) p.set(key, value);
    else p.delete(key);
    return p;
  }, { replace: true });

  const endpoint = endpointId ?? params.get('endpointId') ?? '';
  const status = params.get('status') ?? '';
  const eventType = params.get('type') ?? '';
  const range = params.get('range') ?? '24h';
  const [text, setText] = useState(params.get('q') ?? '');
  const q = useDebounced(text.trim(), 350);
  useEffect(() => { set('q', q); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [q]);

  const query = useInfiniteQuery({
    queryKey: ['logs', endpoint, status, eventType, range, q],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => {
      const hours = RANGES.find((r) => r.value === range)?.hours ?? 0;
      const filters: LogFilters = {
        endpointId: endpoint || undefined,
        status: status || undefined,
        eventType: eventType || undefined,
        q: q || undefined,
        from: hours > 0 ? new Date(Date.now() - hours * 3_600_000).toISOString() : undefined,
      };
      return logsApi.list(filters, pageParam, signal);
    },
    getNextPageParam: (last) => last.nextCursor,
    refetchInterval: 8_000,
  });

  const rows = useMemo(() => query.data?.pages.flatMap((p) => p.items) ?? [], [query.data]);
  const c = query.data?.pages[0]?.counts ?? null;
  const filtered = status !== '' || eventType !== '' || q !== '' || range !== '24h';

  return (
    <>
      <div className="filters">
        {!endpointId && (
          <div className="field">
            <label className="f" htmlFor={`${searchId}-ep`}>Endpoint</label>
            <select id={`${searchId}-ep`} className="select" value={endpoint} onChange={(e) => set('endpointId', e.target.value)}>
              <option value="">All endpoints</option>
              {(endpoints.data?.items ?? []).map((i) => <option key={i.summary.id} value={i.summary.id}>{i.summary.name}</option>)}
            </select>
          </div>
        )}
        <div className="field">
          <label className="f" htmlFor={`${searchId}-type`}>Event type</label>
          <select id={`${searchId}-type`} className="select" value={eventType} onChange={(e) => set('type', e.target.value)}>
            <option value="">All event types</option>
            {(catalog.data?.categories ?? []).map((cat) => (
              <optgroup key={cat.name} label={cat.name}>
                {cat.events.map((ev) => <option key={ev.type} value={ev.type}>{ev.type}</option>)}
              </optgroup>
            ))}
          </select>
        </div>
        <div className="field">
          <label className="f" htmlFor={`${searchId}-range`}>Time</label>
          <select id={`${searchId}-range`} className="select" value={range} onChange={(e) => set('range', e.target.value === '24h' ? '' : e.target.value)}>
            {RANGES.map((r) => <option key={r.value} value={r.value}>{r.label}</option>)}
          </select>
        </div>
        <div className="field grow">
          <label className="f" htmlFor={searchId}>Search</label>
          <input id={searchId} className="input" type="search" placeholder="Event id, delivery id, invoice or payment id, event name" value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
        </div>
      </div>

      <div className="chips" role="group" aria-label="Filter by status">
        {CHIPS.map((chip) => (
          <button key={chip.label} type="button" className={`chip act${status === chip.value ? ' on' : ''}`} aria-pressed={status === chip.value} onClick={() => set('status', chip.value)}>
            {chip.label}{c && <b>{chip.count(c).toLocaleString('en-US')}</b>}
          </button>
        ))}
      </div>

      {query.isError && <ErrorBanner error={query.error} onRetry={() => void query.refetch()} />}

      <div className="card">
        {query.isPending ? (
          <div className="bd"><Loading what="the delivery log" /></div>
        ) : rows.length === 0 ? (
          <div className="empty">
            <b>{filtered ? 'Nothing matches these filters' : 'No deliveries yet'}</b>
            {filtered ? 'Widen the time range or clear the search.' : 'When your endpoints receive events they appear here, with every attempt.'}
          </div>
        ) : (
          <div className="table-wrap">
            <table aria-label="Deliveries">
              <thead>
                <tr>
                  <th scope="col">Time</th><th scope="col">Event</th>{!endpointId && <th scope="col">Endpoint</th>}<th scope="col">Status</th><th scope="col">Attempts</th><th scope="col">Last result</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((d) => <LogRow key={d.id} d={d} showEndpoint={!endpointId} onOpen={() => setSelected(d.id)} />)}
              </tbody>
            </table>
          </div>
        )}
        {query.hasNextPage && (
          <div className="ft" style={{ display: 'flex', justifyContent: 'center', padding: 12, borderTop: '1px solid var(--border)' }}>
            <button type="button" className="btn" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>{query.isFetchingNextPage ? 'Loading…' : 'Load more'}</button>
          </div>
        )}
      </div>

      <DeliveryDrawer id={selected} onClose={() => setSelected(null)} />
    </>
  );
}

function LogRow({ d, showEndpoint, onOpen }: { d: DeliveryItem; showEndpoint: boolean; onOpen: () => void }) {
  return (
    <tr className="rowlink" data-testid="log-row">
      <td className="nw">
        <button type="button" onClick={onOpen} aria-label={`Inspect ${d.eventType} delivered ${timeAgo(d.createdAt)}`}>
          <span title={formatUtc(d.createdAt)}>{timeAgo(d.createdAt)}</span>
        </button>
      </td>
      <td><span className="mono">{d.eventType}</span>{d.aggregateId && <div className="muted sm mono">{d.aggregateId}</div>}</td>
      {showEndpoint && <td>{d.endpointName ?? '—'}</td>}
      <td>
        <DeliveryStatusPill status={d.status} />
        {d.replayed && <span className="tag" style={{ marginLeft: 6 }}>Replayed</span>}
        {d.status === 'retry_scheduled' && d.nextAttemptAt && <div className="muted sm">next {timeUntil(d.nextAttemptAt)}</div>}
      </td>
      <td>{d.attemptCount}</td>
      <td className="sm">{d.lastStatusCode ? <span className="mono">HTTP {d.lastStatusCode}</span> : d.lastError ? <span className="ellipsis" title={d.lastError}>{d.lastError}</span> : <span className="muted">—</span>}</td>
    </tr>
  );
}

// ---------------------------------------------------------------------------------------------- the drawer

type DrawerTab = 'request' | 'response' | 'attempts' | 'event';
const TABS: { id: DrawerTab; label: string }[] = [
  { id: 'request', label: 'Request' }, { id: 'response', label: 'Response' }, { id: 'attempts', label: 'Attempts' }, { id: 'event', label: 'Event' },
];

function DeliveryDrawer({ id, onClose }: { id: string | null; onClose: () => void }) {
  const can = useCan();
  const toast = useToast();
  const client = useQueryClient();
  const [tab, setTab] = useState<DrawerTab>('request');
  const query = useQuery({ queryKey: ['delivery', id], queryFn: ({ signal }) => logsApi.get(id!, signal), enabled: id !== null, refetchInterval: 5_000 });
  const replay = useMutation({
    mutationFn: () => logsApi.replay(id!),
    onSuccess: async () => {
      toast.show('Replay queued. The same webhook-id is sent again in a moment.');
      await Promise.all([client.invalidateQueries({ queryKey: ['logs'] }), client.invalidateQueries({ queryKey: ['delivery', id] })]);
    },
    onError: (e) => toast.show(errorText(e)),
  });
  useEffect(() => { if (id) setTab('request'); }, [id]);

  const d = query.data;
  const canReplay = can('replay:single') && d !== undefined && ['succeeded', 'dead', 'held'].includes(d.delivery.status);

  return (
    <Drawer
      open={id !== null}
      onClose={onClose}
      title={d ? (
        <>
          <div className="row wrap"><h2 className="mono">{d.delivery.eventType}</h2><DeliveryStatusPill status={d.delivery.status} />{d.delivery.replayCount > 0 && <span className="tag">Replayed {d.delivery.replayCount}×</span>}</div>
          <div className="muted sm" style={{ marginTop: 4 }}>
            To <b>{d.endpoint?.name ?? 'a deleted endpoint'}</b> · <span className="mono">{d.delivery.webhookId}</span>
          </div>
        </>
      ) : <h2>Delivery</h2>}
      footer={d && (
        <>
          <div className="muted sm" style={{ maxWidth: 360 }}>
            {canReplay ? 'Replay sends the same webhook-id again. A receiver that already handled it should ignore it.' : can('replay:single') ? 'Replay is possible once the delivery has finished.' : 'Your role cannot replay events.'}
          </div>
          <button type="button" className="btn pri" disabled={!canReplay || replay.isPending} onClick={() => replay.mutate()}><Icon name="refresh" />Replay this event</button>
        </>
      )}
    >
      {query.isPending && id !== null && <Loading what="the delivery" />}
      {query.isError && <ErrorBanner error={query.error} />}
      {d && (
        <>
          <div className="tabs sm" role="tablist" aria-label="Delivery details">
            {TABS.map((t) => (
              <button key={t.id} type="button" role="tab" id={`dt-${t.id}`} aria-selected={tab === t.id} aria-controls="dt-panel" className="tabbtn" onClick={() => setTab(t.id)}>{t.label}</button>
            ))}
          </div>
          <div role="tabpanel" id="dt-panel" aria-labelledby={`dt-${tab}`} data-testid="drawer">
            {tab === 'request' && <RequestPanel d={d} />}
            {tab === 'response' && <ResponsePanel d={d} />}
            {tab === 'attempts' && <AttemptsPanel d={d} />}
            {tab === 'event' && <EventPanel d={d} />}
          </div>
        </>
      )}
    </Drawer>
  );
}

function RequestPanel({ d }: { d: DeliveryDetail }) {
  const r = d.request;
  if (!r) return <p className="help">No request has been sent yet. {d.delivery.status === 'held' ? 'This event is held until the endpoint is resumed.' : 'It is waiting for its first attempt.'}</p>;
  return (
    <div className="stack">
      <div className="row wrap sb">
        <div><span className="tag">{r.method}</span> <span className="mono sm" style={{ wordBreak: 'break-all' }}>{r.url}</span></div>
        <CopyButton text={toCurl(r)} label="Copy as cURL" what="The cURL command" />
      </div>
      <section aria-labelledby="req-h">
        <h3 id="req-h" style={{ marginBottom: 6 }}>Headers as sent</h3>
        <table className="hdrs"><tbody>
          {Object.entries(r.headers ?? {}).map(([k, v]) => <tr key={k}><td>{k}</td><td>{v}</td></tr>)}
        </tbody></table>
        <p className="help">Credentials and secret headers are always hidden. The signature is shortened.</p>
      </section>
      <section aria-labelledby="body-h">
        <h3 id="body-h" style={{ marginBottom: 6 }}>Body</h3>
        {r.body ? <CodeBlock code={r.body} label="Request body" /> : <div className="note warn">{r.bodyNote ?? 'The body is not available.'}</div>}
        {r.body && r.bodyNote && <p className="help">{r.bodyNote}</p>}
      </section>
    </div>
  );
}

function ResponsePanel({ d }: { d: DeliveryDetail }) {
  const last = d.attempts[d.attempts.length - 1];
  if (!last) return <p className="help">There is no response yet: no attempt has been made.</p>;
  return (
    <div className="stack">
      <dl className="kvlist">
        <dt>Status</dt><dd>{last.statusCode ? <span className="mono">HTTP {last.statusCode}</span> : 'No answer'}</dd>
        <dt>Result</dt><dd>{last.outcome}</dd>
        <dt>Took</dt><dd>{last.durationMs} ms</dd>
        {last.error && <><dt>Error</dt><dd>{last.error}</dd></>}
        <dt>Trace id</dt><dd className="mono">{last.traceId ?? '—'}</dd>
      </dl>
      <section aria-labelledby="resp-h">
        <h3 id="resp-h" style={{ marginBottom: 6 }}>Body your server returned</h3>
        {last.responseSnippet
          ? <CodeBlock code={last.responseSnippet} label="Response body" />
          : <div className="note">{d.canViewPayload ? 'Your server returned no body.' : 'Your role cannot view response bodies.'}</div>}
        <p className="help">We keep at most 4 KB of a reply, so an error page cannot fill our storage or leak data into it.</p>
      </section>
    </div>
  );
}

function AttemptsPanel({ d }: { d: DeliveryDetail }) {
  if (d.attempts.length === 0) return <p className="help">No attempts yet.</p>;
  return (
    <div className="stack">
      <div className="table-wrap">
        <table aria-label="Attempts">
          <thead><tr><th scope="col">#</th><th scope="col">Started</th><th scope="col">Took</th><th scope="col">Result</th><th scope="col">Answer</th></tr></thead>
          <tbody>
            {d.attempts.map((a) => (
              <tr key={a.attemptNo}>
                <td>{a.attemptNo}</td>
                <td className="nw" title={formatUtc(a.startedAt)}>{timeAgo(a.startedAt)}</td>
                <td>{a.durationMs} ms</td>
                <td><span className={`pill ${a.outcome === 'success' ? 'green' : 'red'}`}>{a.outcome === 'success' ? 'Success' : a.outcome.replaceAll('_', ' ')}</span></td>
                <td className="sm">{a.statusCode ? <span className="mono">HTTP {a.statusCode}</span> : a.error ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {d.delivery.status === 'retry_scheduled' && <p className="help">Next attempt {timeUntil(d.delivery.nextAttemptAt)}. We keep trying until {formatUtc(d.delivery.deadlineAt)}.</p>}
      {d.delivery.status === 'held' && <p className="help">Held until {formatUtc(d.delivery.heldUntil)}. Resume the endpoint in the <Link to="/replay">Replay center</Link>.</p>}
      {d.delivery.status === 'dead' && <p className="help">We stopped trying{d.delivery.heldReason ? ` (${d.delivery.heldReason.replaceAll('_', ' ')})` : ''}. You can replay it.</p>}
    </div>
  );
}

function EventPanel({ d }: { d: DeliveryDetail }) {
  const e = d.event;
  return (
    <dl className="kvlist">
      <dt>Event</dt><dd className="mono">{d.delivery.eventType}</dd>
      {e?.description && <><dt>What it means</dt><dd>{e.description}</dd></>}
      <dt>Webhook id</dt><dd className="mono">{d.delivery.webhookId}</dd>
      <dt>Business object</dt><dd className="mono">{d.delivery.aggregateId ?? '—'}</dd>
      {e && <><dt>Sequence</dt><dd>{e.sequence}</dd><dt>Happened</dt><dd>{formatUtc(e.occurredAt)}</dd></>}
      <dt>Delivery id</dt><dd className="mono">{d.delivery.id}</dd>
      <dt>Created</dt><dd>{formatUtc(d.delivery.createdAt)}</dd>
      {e?.containsPii && <><dt>Personal data</dt><dd>This event class carries personal data, so it is always sent as ids only.</dd></>}
      {d.delivery.replayedAt && <><dt>Last replayed</dt><dd>{formatUtc(d.delivery.replayedAt)}</dd></>}
    </dl>
  );
}
