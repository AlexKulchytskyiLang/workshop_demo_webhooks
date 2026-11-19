import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useApiVersions, useCatalog, useEndpointMutation } from '../../api/queries';
import { webhooksApi, type HeaderView, type PayloadStyle } from '../../api/webhooks';
import { EventPicker } from '../../features/EventPicker';
import { VersionDialog } from '../../features/VersionDialog';
import { FilterBuilder } from '../../features/FilterBuilder';
import { snapshotBlockedReason } from '../../lib/events';
import { countConditions, emptyFilter, fromDoc, stateErrors, toDoc, type FilterState } from '../../lib/filters';
import { useDebounced } from '../../lib/useDebounced';
import { RadioCards } from '../../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../../ui/Feedback';
import { Icon } from '../../ui/Icon';
import { useEndpointCtx } from './EndpointLayout';

export function EventsTab() {
  const { detail, canWrite } = useEndpointCtx();
  const catalog = useCatalog();
  const id = detail.endpoint.id;

  return (
    <div className="split">
      <div>
        {catalog.isPending && <Loading what="the event catalogue" />}
        {catalog.isError && <ErrorBanner error={catalog.error} onRetry={() => void catalog.refetch()} />}
        {catalog.data && <SubscriptionsCard key={JSON.stringify(detail.subscriptions)} catalog={catalog.data} />}
        <HeadersCard key={JSON.stringify(detail.headers)} endpointId={id} headers={detail.headers} canWrite={canWrite} />
      </div>
      <aside className="sticky" aria-label="Payload settings">
        {catalog.data && <PayloadStyleCard catalog={catalog.data} />}
        <VersionCard />
      </aside>
    </div>
  );
}

function SubscriptionsCard({ catalog }: { catalog: NonNullable<ReturnType<typeof useCatalog>['data']> }) {
  const { detail, canWrite } = useEndpointCtx();
  const id = detail.endpoint.id;
  const toast = useToast();
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set(detail.subscriptions.map((s) => s.eventType)));
  const [filters, setFilters] = useState<Record<string, FilterState>>(() => Object.fromEntries(detail.subscriptions.map((s) => [s.eventType, fromDoc(s.filter)])));
  const [editing, setEditing] = useState<string | null>(null);

  const save = useEndpointMutation(id, () => webhooksApi.setSubscriptions(id, [...selected].sort().map((t) => ({ eventType: t, filter: toDoc(filters[t] ?? emptyFilter()) }))));

  const filterCounts = useMemo(() => Object.fromEntries([...selected].map((t) => [t, countConditions(filters[t] ?? emptyFilter())])), [selected, filters]);
  const savedSet = detail.subscriptions.map((s) => s.eventType).sort().join('|');
  const dirty = [...selected].sort().join('|') !== savedSet
    || [...selected].some((t) => JSON.stringify(toDoc(filters[t] ?? emptyFilter())) !== JSON.stringify(detail.subscriptions.find((s) => s.eventType === t)?.filter ?? null));
  const errors = [...selected].flatMap((t) => stateErrors(filters[t] ?? emptyFilter()).map((e) => `${t}: ${e}`));
  const blocked = detail.endpoint.payloadStyle === 'fat' ? snapshotBlockedReason(catalog, selected) : null;
  const filterTarget = editing && selected.has(editing) ? editing : null;

  return (
    <>
      <section className="card" aria-labelledby="ev-h">
        <div className="hd">
          <h2 id="ev-h">Events</h2>
          {canWrite && (
            <div className="row">
              {dirty && <span className="pill amber">Unsaved changes</span>}
              <button type="button" className="btn pri" disabled={!dirty || save.isPending || errors.length > 0 || selected.size === 0 || blocked !== null}
                onClick={() => save.mutate(undefined, { onSuccess: () => toast.show('Events and filters saved'), onError: (e) => toast.show(errorText(e)) })}>
                {save.isPending ? 'Saving…' : 'Save events & filters'}
              </button>
            </div>
          )}
        </div>
        <div className="bd">
          <EventPicker catalog={catalog} selected={selected} onChange={(next) => { setSelected(next); if (editing && !next.has(editing)) setEditing(null); }} filterCounts={filterCounts} initiallyOpen={['Invoices', 'Payments']} disabled={!canWrite} />
          {selected.size === 0 && <p className="error-text" role="alert">Choose at least one event. To stop deliveries, pause the endpoint instead.</p>}
          {blocked && <div className="banner amber" role="alert" style={{ marginTop: 10 }}><div className="grow">{blocked} Switch the payload style to Thin or Auto (right) before saving.</div></div>}
          {save.isError && <ErrorBanner error={save.error} />}
        </div>
      </section>

      <section className="card" aria-labelledby="filter-h">
        <div className="hd">
          <div>
            <h2 id="filter-h">Filter</h2>
            <div className="muted sm">Deliver only events that match. Evaluated on our side, before delivery, so filtered events cost you nothing.</div>
          </div>
          <div className="row">
            <label className="sr-only" htmlFor="filter-event">Event to filter</label>
            <select id="filter-event" className="select mono" style={{ width: 250 }} value={filterTarget ?? ''} onChange={(e) => setEditing(e.target.value || null)}>
              <option value="">Choose an event…</option>
              {[...selected].sort().map((t) => <option key={t} value={t}>{t}{filterCounts[t] ? ` (${filterCounts[t]})` : ''}</option>)}
            </select>
          </div>
        </div>
        <div className="bd">
          {!filterTarget && <p className="muted" style={{ margin: 0 }}>Choose one of your events to add conditions such as <span className="mono">data.amount &gt; 5000</span>.</p>}
          {filterTarget && (
            <FilterEditor eventType={filterTarget} state={filters[filterTarget] ?? emptyFilter()} disabled={!canWrite}
              onChange={(next) => setFilters((f) => ({ ...f, [filterTarget]: next }))} />
          )}
        </div>
      </section>
    </>
  );
}

/** inv_01a1d17131fd7902a91ae1ff24c8338f -> inv_01a1d1…8338f: enough to recognise, the full id is in the tooltip. */
const shortId = (id: string) => (id.length <= 18 ? id : `${id.slice(0, 10)}…${id.slice(-5)}`);

function FilterEditor({ eventType, state, onChange, disabled }: { eventType: string; state: FilterState; onChange: (s: FilterState) => void; disabled: boolean }) {
  const doc = useMemo(() => toDoc(state), [state]);
  const debounced = useDebounced(JSON.stringify(doc), 600);
  const hasErrors = stateErrors(state).length > 0;
  const test = useQuery({
    queryKey: ['filter-test', eventType, debounced],
    queryFn: ({ signal }) => webhooksApi.filterTest(eventType, debounced === 'null' ? null : JSON.parse(debounced), signal),
    enabled: !hasErrors,
    retry: false,
  });
  const data = test.data;
  // what is shown must match what is typed: while a newer version is being tested (or the rows are invalid) the old result is dimmed and announced as updating
  const stale = hasErrors || JSON.stringify(doc) !== debounced || test.isFetching;

  return (
    <div>
      <FilterBuilder eventType={eventType} state={state} onChange={onChange} disabled={disabled} />
      <hr className="s" />
      <div className="stack">
        <div>
          <div className="sm muted" style={{ marginBottom: 4 }}>Compiled expression (read-only, sandboxed)</div>
          <pre className="code" tabIndex={0} aria-label="Compiled filter expression">{hasErrors ? 'Fix the highlighted conditions first.' : data?.expression ?? (doc === null ? '(all events)' : '…')}</pre>
          <div className="note" style={{ marginTop: 8 }}>Filters run on the <b>canonical event</b>, not on your pinned version, so upgrading the payload version never changes what you receive.</div>
        </div>
        <div aria-live="polite" aria-busy={stale} data-stale={stale}>
          <div className="sm muted" style={{ marginBottom: 4 }}>Test against your latest {eventType} events{stale && data && !hasErrors ? <b className="updating"> · updating…</b> : ''}</div>
          {test.isFetching && !data && <p className="help" role="status">Testing…</p>}
          {test.isError && <p className="error-text" role="alert">{errorText(test.error)}</p>}
          {data && !hasErrors && !data.valid && <p className="error-text" role="alert">{data.error}</p>}
          {hasErrors && <p className="help">Fix the highlighted conditions and the test runs again.</p>}
          {data && !hasErrors && data.valid && data.total === 0 && <p className="muted">No recent {eventType} events to test against yet. Send some from the simulator or send a test event.</p>}
          {data && !hasErrors && data.valid && data.total > 0 && (
            <>
              <p className="sm" style={{ margin: '0 0 6px' }} data-testid="filter-summary"><b>{data.matched} of {data.total}</b> recent events would be delivered.</p>
              {doc !== null && data.matched === 0 && <div className="note warn">This filter matches <b>none</b> of your latest events. Check the conditions.</div>}
              <div className="table-wrap">
                <table className="sch test-table">
                  <thead><tr><th scope="col">Event</th><th scope="col">Data</th><th scope="col">Result</th></tr></thead>
                  <tbody>
                    {data.rows.slice(0, 8).map((r) => (
                      <tr key={r.eventId}>
                        <td className="mono sm" title={r.aggregateId}>{shortId(r.aggregateId)}</td>
                        <td className="sm">{Object.entries(r.summary).map(([k, v]) => `${k}: ${v}`).join(' · ')}</td>
                        <td>{r.matched ? <span className="pill green">Delivered</span> : <span className="pill gray">Filtered out</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

interface HeaderRow { name: string; value: string; secret: boolean; stored: boolean }

function HeadersCard({ endpointId, headers, canWrite }: { endpointId: string; headers: HeaderView[]; canWrite: boolean }) {
  const toast = useToast();
  const [rows, setRows] = useState<HeaderRow[]>(() => headers.map((h) => ({ name: h.name, value: h.secret ? '' : h.value ?? '', secret: h.secret, stored: h.secret && h.hasValue })));
  const save = useEndpointMutation(endpointId, () => webhooksApi.setHeaders(endpointId, rows.filter((r) => r.name.trim() !== '').map((r) => ({
    name: r.name.trim(), secret: r.secret, value: r.secret && r.value === '' && r.stored ? null : r.value, // a secret left empty keeps its stored value
  }))));
  const patch = (i: number, p: Partial<HeaderRow>) => setRows((all) => all.map((r, ri) => (ri === i ? { ...r, ...p } : r)));

  return (
    <section className="card" aria-labelledby="hdr-h">
      <div className="hd">
        <div><h2 id="hdr-h">Custom headers</h2><div className="muted sm">Static headers added to every request, for example routing for your gateway.</div></div>
        {canWrite && <button type="button" className="btn" disabled={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: () => toast.show('Headers saved'), onError: (e) => toast.show(errorText(e)) })}>{save.isPending ? 'Saving…' : 'Save headers'}</button>}
      </div>
      <div className="bd">
        {rows.length === 0 && <p className="muted" style={{ marginTop: 0 }}>No custom headers.</p>}
        {rows.map((r, i) => (
          <div className="kv" key={i}>
            <input className="input mono" aria-label={`Name of header ${i + 1}`} value={r.name} disabled={!canWrite} onChange={(e) => patch(i, { name: e.target.value })} placeholder="X-Target-Env" />
            <input className="input mono" aria-label={`Value of header ${i + 1}`} type={r.secret ? 'password' : 'text'} autoComplete="off" value={r.value} disabled={!canWrite}
              placeholder={r.secret && r.stored ? '•••••••• (unchanged)' : ''} onChange={(e) => patch(i, { value: e.target.value })} />
            <label className="row sm"><input type="checkbox" checked={r.secret} disabled={!canWrite} onChange={(e) => patch(i, { secret: e.target.checked })} /> secret</label>
            <button type="button" className="btn icon gh" disabled={!canWrite} aria-label={`Remove header ${i + 1}`} onClick={() => setRows((all) => all.filter((_, ri) => ri !== i))}><Icon name="x" /></button>
          </div>
        ))}
        <div className="row">
          <button type="button" className="btn sm" disabled={!canWrite || rows.length >= 10} onClick={() => setRows((all) => [...all, { name: '', value: '', secret: false, stored: false }])}><Icon name="plus" />Add header</button>
          <span className="muted sm">{rows.length} of 10 used · values marked secret are encrypted and never shown again</span>
        </div>
        <div className="note warn" style={{ marginTop: 10 }}>Reserved, cannot be set: <span className="mono">host, content-type, content-length, webhook-*, traceparent</span>.</div>
        {save.isError && <ErrorBanner error={save.error} />}
      </div>
    </section>
  );
}

function PayloadStyleCard({ catalog }: { catalog: NonNullable<ReturnType<typeof useCatalog>['data']> }) {
  const { detail, canWrite } = useEndpointCtx();
  const toast = useToast();
  const id = detail.endpoint.id;
  const style = detail.endpoint.payloadStyle;
  const blocked = snapshotBlockedReason(catalog, new Set(detail.subscriptions.map((s) => s.eventType)));
  const change = useEndpointMutation(id, (payloadStyle: PayloadStyle) => webhooksApi.updateEndpoint(id, { payloadStyle }));

  return (
    <section className="card" aria-labelledby="style-h">
      <div className="hd"><h3 id="style-h">Payload style</h3></div>
      <div className="bd">
        <RadioCards<PayloadStyle> legend="Payload style" name="payload-style" value={style}
          onChange={(v) => change.mutate(v, { onSuccess: () => toast.show('Payload style saved'), onError: (e) => toast.show(errorText(e)) })}
          options={[
            { value: 'fat', title: <>Snapshot <span className="tag fat">Fat</span></>, description: 'Full object in the body. Fewest round trips.', disabled: !canWrite || blocked !== null, disabledReason: blocked ?? undefined },
            { value: 'thin', title: <>Thin <span className="tag thin">Thin</span></>, description: 'Ids only. You fetch details via the API. Best for privacy.', disabled: !canWrite },
            { value: 'auto', title: 'Auto', description: 'The event default. Personal data events are always thin; bulk results will use a claim-check link.', disabled: !canWrite },
          ]} />
      </div>
    </section>
  );
}

function VersionCard() {
  const { detail, canWrite } = useEndpointCtx();
  const versions = useApiVersions();
  const [comparing, setComparing] = useState(false);
  const pinned = detail.endpoint.apiVersion;
  const info = versions.data?.find((v) => v.version === pinned);
  const newer = versions.data?.filter((v) => v.version > pinned && v.status !== 'preview') ?? [];
  const preview = versions.data?.find((v) => v.status === 'preview' && v.version > pinned);
  return (
    <section className="card" aria-labelledby="ver-h">
      <div className="hd"><h3 id="ver-h">Payload version</h3><span className="tag prod">pinned</span></div>
      <div className="bd">
        <div className="row sb"><b className="mono">{pinned}</b><span className={`tag${info?.status === 'older' ? ' stg' : ''}`}>{info?.status === 'older' ? 'older' : info?.status === 'preview' ? 'preview' : 'current'}</span></div>
        {info?.sunsetOn && <p className="help" style={{ color: 'var(--amber)' }}>This version ends on {info.sunsetOn}. Plan the move before then.</p>}
        {newer.length > 0 && <p className="help"><b>{newer[newer.length - 1]!.version}</b> is available. See exactly what changes before you decide.</p>}
        {newer.length === 0 && !preview && <p className="help">You are on the newest version. When a new one is published you will see what changes here, and your integration stays on this version until you upgrade it.</p>}
        {preview && <p className="help">A preview, {preview.version}, can be tried in the Playground.</p>}
        <button type="button" className="btn sm" disabled={!versions.data} onClick={() => setComparing(true)}>Compare versions…</button>
        <p className="help">Deliveries already created, and their retries, keep the version they were created with.</p>
      </div>
      {comparing && versions.data && (
        <VersionDialog endpointId={detail.endpoint.id} current={pinned} events={detail.subscriptions.map((s) => s.eventType)} versions={versions.data} canWrite={canWrite} onClose={() => setComparing(false)} />
      )}
    </section>
  );
}
