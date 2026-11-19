import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { Link } from 'react-router-dom';
import { logsApi, playgroundApi, replayApi, type ReplayJob, type ReplayOverview, type ReplaySelection, type ReplayState, type ResumeMode } from '../api/advanced';
import { useCatalog } from '../api/queries';
import { useCan } from '../auth/me';
import { formatDuration, formatUtc, timeAgo, timeUntil } from '../lib/format';
import { usePageTitle } from '../lib/usePageTitle';
import { RadioCards, StatusPill, TabList } from '../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../ui/Feedback';
import { Icon } from '../ui/Icon';
import { Modal } from '../ui/Modal';

type Tab = 'held' | 'dead' | 'jobs';

/**
 * The Replay center (UX-SPEC D3 and D9). Held events wait here while an endpoint is paused or switched off; Resume is a guided flow that needs a passing test first.
 * Replay re-sends events with the SAME webhook-id, behind a dry run, a typed confirmation, a pace and a daily cap.
 */
export function Replay() {
  usePageTitle('Replay center');
  const can = useCan();
  const [tab, setTab] = useState<Tab>('held');
  const [resuming, setResuming] = useState<string | null>(null);
  const [replaying, setReplaying] = useState<Partial<ReplaySelection> | null>(null);
  const overview = useQuery({ queryKey: ['replay-overview'], queryFn: ({ signal }) => replayApi.overview(signal), refetchInterval: 8_000 });
  const jobs = useQuery({ queryKey: ['replay-jobs'], queryFn: ({ signal }) => replayApi.jobs(signal), refetchInterval: 4_000 });

  const o = overview.data;
  const held = o?.endpoints.filter((e) => e.held > 0 || e.status === 'paused' || e.status === 'disabled') ?? [];
  const heldTotal = o?.endpoints.reduce((n, e) => n + e.held, 0) ?? 0;
  const deadTotal = o?.endpoints.reduce((n, e) => n + e.dead, 0) ?? 0;
  const running = jobs.data?.items.filter((j) => j.status === 'running').length ?? 0;
  const resumingEndpoint = o?.endpoints.find((e) => e.id === resuming);

  return (
    <>
      <div className="page-h">
        <div>
          <h1>Replay center</h1>
          <p className="sub">Nothing is lost. Events wait here when an endpoint is paused or off, and you decide what happens to them.</p>
        </div>
        {can('replay:run') && <button type="button" className="btn pri" onClick={() => setReplaying({})}><Icon name="refresh" />New replay job</button>}
      </div>

      {overview.isError && <ErrorBanner error={overview.error} onRetry={() => void overview.refetch()} />}
      {o && (
        <p className="help" style={{ marginTop: 0, marginBottom: 12 }}>
          Replay allowance today: <b>{o.cap.remaining.toLocaleString('en-US')}</b> of {o.cap.limit.toLocaleString('en-US')} events left in the last 24 hours.
        </p>
      )}

      <TabList<Tab> label="Replay center sections" idPrefix="rc" value={tab} onChange={setTab} tabs={[
        { id: 'held', label: 'Held', count: heldTotal }, { id: 'dead', label: 'Dead', count: deadTotal }, { id: 'jobs', label: 'Jobs', count: running > 0 ? running : undefined },
      ]} />
      <div role="tabpanel" id="rc-panel" aria-labelledby={`rc-${tab}`}>
        {overview.isPending && <Loading what="the replay center" />}
        {o && tab === 'held' && <HeldTab held={held} canRun={can('replay:run')} onResume={setResuming} onRelease={(id) => setReplaying({ endpointId: id, states: ['held'] })} />}
        {o && tab === 'dead' && <DeadTab canSingle={can('replay:single')} canRun={can('replay:run')} onReplayAll={(id) => setReplaying({ endpointId: id, states: ['dead'] })} />}
        {tab === 'jobs' && <JobsTab jobs={jobs.data?.items ?? []} loading={jobs.isPending} canRun={can('replay:run')} />}
      </div>

      {resumingEndpoint && o && <ResumeGuide key={resumingEndpoint.id} endpoint={resumingEndpoint} overview={o} onClose={() => setResuming(null)} />}
      {replaying && o && <ReplayJobDialog initial={replaying} overview={o} onClose={() => setReplaying(null)} onStarted={() => { setReplaying(null); setTab('jobs'); }} />}
    </>
  );
}

// ---------------------------------------------------------------------------------------------- held

function HeldTab({ held, canRun, onResume, onRelease }: { held: ReplayOverview['endpoints']; canRun: boolean; onResume: (id: string) => void; onRelease: (id: string) => void }) {
  if (held.length === 0) {
    return <div className="card"><div className="empty"><b>Nothing is held</b>When an endpoint is paused or switched off, the events it misses wait here, for the number of days you chose.</div></div>;
  }
  return (
    <div className="stack">
      {held.map((e) => {
        const off = e.status === 'paused' || e.status === 'disabled';
        return (
          <section key={e.id} className="card" aria-label={e.name} data-testid="held-card">
            <div className="hd">
              <div className="row wrap"><h2><Link to={`/endpoints/${e.id}/delivery`}>{e.name}</Link></h2><StatusPill health={e.health} /></div>
              {canRun && <button type="button" className="btn pri" onClick={() => (off ? onResume(e.id) : onRelease(e.id))}>{off ? 'Resume…' : 'Release held events…'}</button>}
            </div>
            <div className="bd">
              <dl className="kvlist">
                <dt>Held events</dt><dd><b>{e.held.toLocaleString('en-US')}</b>{e.oldestHeld ? ` · the oldest from ${timeAgo(e.oldestHeld)}` : ''}</dd>
                {e.holdExpiresAt && <><dt>Kept until</dt><dd>{formatUtc(e.holdExpiresAt)} ({timeUntil(e.holdExpiresAt)}). After that they become dead.</dd></>}
                <dt>Last passing test</dt><dd>{e.lastTestPassedAt ? `${timeAgo(e.lastTestPassedAt)}${e.testFresh ? '' : ' (too old to resume)'}` : 'none yet'}</dd>
              </dl>
              {!canRun && <p className="help">Your role cannot resume endpoints. An owner, admin or developer can.</p>}
            </div>
          </section>
        );
      })}
    </div>
  );
}

function ResumeGuide({ endpoint, overview, onClose }: { endpoint: ReplayOverview['endpoints'][number]; overview: ReplayOverview; onClose: () => void }) {
  const client = useQueryClient();
  const toast = useToast();
  const [mode, setMode] = useState<ResumeMode>('deliver_all');
  const [typed, setTyped] = useState('');
  const typedId = useId();
  const test = useMutation({ mutationFn: () => playgroundApi.ping(endpoint.id), onSuccess: () => client.invalidateQueries({ queryKey: ['replay-overview'] }) });
  const resume = useMutation({
    mutationFn: () => replayApi.resume(endpoint.id, mode, overview.recentHours),
    onSuccess: async (r) => {
      toast.show(r.released > 0 ? `Resumed. ${r.released} events are being delivered at ${overview.ratePerSecond} per second.` : 'Resumed.');
      await Promise.all(['replay-overview', 'replay-jobs', 'logs', 'endpoints', 'overview'].map((k) => client.invalidateQueries({ queryKey: [k] })));
      onClose();
    },
  });
  const passed = endpoint.testFresh || test.data?.ok === true;
  const discarding = mode === 'discard';
  const ready = passed && (!discarding || typed.trim() === 'DISCARD') && !resume.isPending;

  return (
    <Modal open onClose={onClose} wide title={`Resume “${endpoint.name}”`}
      footer={<><button type="button" className="btn" onClick={onClose}>Cancel</button><button type="button" className="btn pri" disabled={!ready} onClick={() => resume.mutate()}>{resume.isPending ? 'Resuming…' : 'Resume endpoint'}</button></>}>
      <ol className="steps">
        <li className="done"><div><b>Fix the problem on your side</b><div className="help">{endpoint.disabledReason === 'auto_disabled' ? 'We switched this endpoint off because it kept failing.' : endpoint.disabledReason === 'gone' ? 'Your server said this address is gone (410). Update the URL first.' : 'The endpoint is paused.'} Look at <Link to={`/endpoints/${endpoint.id}/logs`} onClick={onClose}>its delivery log</Link> to see what went wrong.</div></div></li>
        <li className={passed ? 'done' : 'cur'}>
          <div>
            <b>Send a test that passes</b>
            <div className="help">We never release held events to a server we have not just checked. A test must pass within the last {overview.testFreshMinutes} minutes.</div>
            <div className="row" style={{ marginTop: 6 }}>
              <button type="button" className="btn sm" disabled={test.isPending} onClick={() => test.mutate()}><Icon name="send" />{test.isPending ? 'Testing…' : passed ? 'Test again' : 'Send test'}</button>
              {passed && <span className="pill green">Test passed</span>}
            </div>
            <div aria-live="polite">
              {test.data && !test.data.ok && <div className="note red" style={{ marginTop: 8 }}>{test.data.hint ?? 'The test did not pass.'}</div>}
              {test.isError && <div className="note red" style={{ marginTop: 8 }}>{errorText(test.error)}</div>}
            </div>
          </div>
        </li>
        <li className={passed ? 'cur' : undefined}>
          <div style={{ flex: 1 }}>
            <b>Choose what happens to the {endpoint.held.toLocaleString('en-US')} held events</b>
            <div style={{ marginTop: 8 }}>
              <RadioCards<ResumeMode> legend="What to do with the held events" name="resume-mode" value={mode} onChange={setMode} options={[
                { value: 'deliver_all', title: 'Deliver all, at a steady pace', description: `About ${overview.ratePerSecond} per second, so your server is not hit by one big burst.` },
                { value: 'only_recent', title: `Only the last ${overview.recentHours} hours`, description: 'Older events are discarded. Useful when old events would no longer make sense.' },
                { value: 'discard', title: 'Discard them all', description: 'They are kept as dead events. You can still bring them back later with a replay job.' },
              ]} />
            </div>
            {discarding && (
              <div className="field" style={{ marginTop: 10 }}>
                <label className="f" htmlFor={typedId}>Type <b className="mono">DISCARD</b> to confirm</label>
                <input id={typedId} className="input mono" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
              </div>
            )}
          </div>
        </li>
      </ol>
      {resume.isError && <div className="banner red" role="alert"><div className="grow">{errorText(resume.error)}</div></div>}
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------- dead

function DeadTab({ canSingle, canRun, onReplayAll }: { canSingle: boolean; canRun: boolean; onReplayAll: (endpointId: string) => void }) {
  const client = useQueryClient();
  const toast = useToast();
  const dead = useQuery({ queryKey: ['logs-dead'], queryFn: ({ signal }) => logsApi.list({ status: 'dead' }, null, signal), refetchInterval: 8_000 });
  const replay = useMutation({
    mutationFn: (id: string) => logsApi.replay(id),
    onSuccess: async () => { toast.show('Replay queued.'); await Promise.all([client.invalidateQueries({ queryKey: ['logs-dead'] }), client.invalidateQueries({ queryKey: ['replay-overview'] })]); },
    onError: (e) => toast.show(errorText(e)),
  });
  if (dead.isPending) return <Loading what="dead events" />;
  const items = dead.data?.items ?? [];
  if (items.length === 0) return <div className="card"><div className="empty"><b>No dead events</b>An event becomes dead when we gave up: the retry window ended, or you discarded it. They stay here so you can bring them back.</div></div>;
  return (
    <div className="card">
      <div className="table-wrap">
        <table aria-label="Dead events">
          <thead><tr><th scope="col">When</th><th scope="col">Event</th><th scope="col">Endpoint</th><th scope="col">Why</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {items.map((d) => (
              <tr key={d.id}>
                <td className="nw" title={formatUtc(d.createdAt)}>{timeAgo(d.createdAt)}</td>
                <td><span className="mono">{d.eventType}</span>{d.aggregateId && <div className="muted sm mono">{d.aggregateId}</div>}</td>
                <td>{d.endpointName}</td>
                <td className="sm">{(d.heldReason ?? 'gave up').replaceAll('_', ' ')}{d.lastStatusCode ? ` · HTTP ${d.lastStatusCode}` : ''}</td>
                <td>
                  <div className="acts">
                    {canSingle && <button type="button" className="btn sm" disabled={replay.isPending} aria-label={`Replay ${d.eventType} ${d.aggregateId ?? ''}`} onClick={() => replay.mutate(d.id)}>Replay</button>}
                    {canRun && <button type="button" className="btn sm gh" aria-label={`Replay all dead events of ${d.endpointName}`} onClick={() => onReplayAll(d.endpointId)}>Replay all…</button>}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {dead.data?.nextCursor && <div style={{ padding: 12, textAlign: 'center', borderTop: '1px solid var(--border)' }}><Link to="/logs?status=dead&range=30d">See all dead events in the delivery log</Link></div>}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------- jobs

function JobsTab({ jobs, loading, canRun }: { jobs: ReplayJob[]; loading: boolean; canRun: boolean }) {
  const client = useQueryClient();
  const toast = useToast();
  const cancel = useMutation({
    mutationFn: (id: string) => replayApi.cancel(id),
    onSuccess: async (r) => { toast.show(`Job cancelled. ${r.reverted} events were put back as they were.`); await Promise.all(['replay-jobs', 'replay-overview', 'logs'].map((k) => client.invalidateQueries({ queryKey: [k] }))); },
    onError: (e) => toast.show(errorText(e)),
  });
  if (loading) return <Loading what="replay jobs" />;
  if (jobs.length === 0) return <div className="card"><div className="empty"><b>No replay jobs yet</b>A job re-sends a chosen set of events at a steady pace. Resuming an endpoint and replaying single events appear here too.</div></div>;
  return (
    <div className="stack">
      {jobs.map((j) => {
        const done = j.delivered + j.failed;
        const pct = j.total === 0 ? 100 : Math.min(100, Math.round((done / j.total) * 100));
        return (
          <section key={j.id} className="card" aria-label={`Job for ${j.endpointName}`} data-testid="job-card">
            <div className="hd">
              <div className="row wrap">
                <h2>{j.kind === 'resume' ? 'Resume' : j.kind === 'single' ? 'Single replay' : 'Replay'} · {j.endpointName}</h2>
                <span className={`pill ${j.status === 'running' ? 'blue' : j.status === 'completed' ? 'green' : 'gray'}`}>{j.status === 'running' ? 'Running' : j.status === 'completed' ? 'Completed' : 'Cancelled'}</span>
              </div>
              {canRun && j.status === 'running' && j.kind !== 'single' && <button type="button" className="btn dng sm" disabled={cancel.isPending} onClick={() => cancel.mutate(j.id)}>Cancel job</button>}
            </div>
            <div className="bd">
              <div className="progress" role="progressbar" aria-label="Progress" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}><i style={{ width: `${pct}%` }} /></div>
              <p className="sm" style={{ margin: '8px 0 0' }}>
                {j.delivered.toLocaleString('en-US')} of {j.total.toLocaleString('en-US')} delivered{j.failed > 0 ? ` · ${j.failed} failed again` : ''}{j.remaining > 0 ? ` · ${j.remaining} still waiting` : ''}{j.discarded > 0 ? ` · ${j.discarded} discarded` : ''}
              </p>
              <p className="muted sm" style={{ margin: '4px 0 0' }}>
                Started {timeAgo(j.createdAt)} by {j.createdBy}{j.kind === 'replay' ? ` · ${j.states.replaceAll(',', ', ')}${j.eventType ? ` · ${j.eventType}` : ''} · ${j.ratePerSecond}/s` : ''}
              </p>
            </div>
          </section>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------- new replay job

const STATE_LABEL: Record<ReplayState, string> = { dead: 'Dead events', held: 'Held events', succeeded: 'Events already delivered' };

function ReplayJobDialog({ initial, overview, onClose, onStarted }: { initial: Partial<ReplaySelection>; overview: ReplayOverview; onClose: () => void; onStarted: () => void }) {
  const client = useQueryClient();
  const toast = useToast();
  const catalog = useCatalog();
  const ids = { ep: useId(), type: useId(), from: useId(), to: useId(), rate: useId(), confirm: useId() };
  const [endpointId, setEndpointId] = useState(initial.endpointId ?? overview.endpoints[0]?.id ?? '');
  const [states, setStates] = useState<ReplayState[]>(initial.states ?? ['dead']);
  const [eventType, setEventType] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [rate, setRate] = useState(overview.ratePerSecond);
  const [typed, setTyped] = useState('');

  const selection = (): ReplaySelection => ({
    endpointId, states, eventType: eventType || undefined, ratePerSecond: rate,
    from: from ? new Date(from).toISOString() : undefined, to: to ? new Date(to).toISOString() : undefined,
  });
  const dryRun = useMutation({ mutationFn: () => replayApi.preview(selection()), onSuccess: () => setTyped('') });
  const start = useMutation({
    mutationFn: (phrase: string) => replayApi.start(selection(), phrase),
    onSuccess: async () => {
      toast.show('Replay job started.');
      await Promise.all(['replay-jobs', 'replay-overview', 'logs'].map((k) => client.invalidateQueries({ queryKey: [k] })));
      onStarted();
    },
  });
  const p = dryRun.data;
  const invalidate = () => { dryRun.reset(); start.reset(); };
  const toggle = (s: ReplayState) => { invalidate(); setStates((cur) => (cur.includes(s) ? cur.filter((x) => x !== s) : [...cur, s])); };
  const confirmed = p !== undefined && typed.trim() === p.confirmPhrase;

  return (
    <Modal open onClose={onClose} wide title="New replay job"
      footer={<>
        <button type="button" className="btn" onClick={onClose}>Cancel</button>
        {!p
          ? <button type="button" className="btn pri" disabled={!endpointId || states.length === 0 || dryRun.isPending} onClick={() => dryRun.mutate()}>{dryRun.isPending ? 'Counting…' : 'Dry run: how many events?'}</button>
          : <button type="button" className="btn pri" disabled={!confirmed || p.count === 0 || p.cap.exceeded || !p.endpoint.canReceive || start.isPending} onClick={() => start.mutate(typed.trim())}>{start.isPending ? 'Starting…' : `Replay ${p.count.toLocaleString('en-US')} events`}</button>}
      </>}>
      <div className="note">Replay sends events again with the <b>same webhook-id</b>. A receiver that already handled an event should ignore it; one that missed it gets it now. Nothing is sent until you confirm below.</div>
      <div className="field" style={{ marginTop: 14 }}>
        <label className="f" htmlFor={ids.ep}>Endpoint</label>
        <select id={ids.ep} className="select" value={endpointId} onChange={(e) => { invalidate(); setEndpointId(e.target.value); }}>
          {overview.endpoints.map((e) => <option key={e.id} value={e.id}>{e.name}{e.status !== 'active' ? ` (${e.status})` : ''}</option>)}
        </select>
      </div>
      <fieldset className="rcgroup" style={{ margin: '0 0 14px' }}>
        <legend>Which events</legend>
        <div className="row wrap gap">
          {(Object.keys(STATE_LABEL) as ReplayState[]).map((s) => (
            <label key={s} className="row check" style={{ margin: 0 }}><input type="checkbox" checked={states.includes(s)} onChange={() => toggle(s)} /><span>{STATE_LABEL[s]}</span></label>
          ))}
        </div>
      </fieldset>
      <div className="row two">
        <div className="field">
          <label className="f" htmlFor={ids.type}>Event type (optional)</label>
          <select id={ids.type} className="select" value={eventType} onChange={(e) => { invalidate(); setEventType(e.target.value); }}>
            <option value="">All types</option>
            {(catalog.data?.categories ?? []).flatMap((c) => c.events).map((e) => <option key={e.type} value={e.type}>{e.type}</option>)}
          </select>
        </div>
        <div className="field">
          <label className="f" htmlFor={ids.rate}>Pace</label>
          <select id={ids.rate} className="select" value={rate} onChange={(e) => { invalidate(); setRate(Number(e.target.value)); }}>
            {[1, 5, 20, 50].map((r) => <option key={r} value={r}>{r} per second</option>)}
          </select>
        </div>
      </div>
      <div className="row two">
        <div className="field"><label className="f" htmlFor={ids.from}>From (optional)</label><input id={ids.from} className="input" type="datetime-local" value={from} onChange={(e) => { invalidate(); setFrom(e.target.value); }} /></div>
        <div className="field"><label className="f" htmlFor={ids.to}>Until (optional)</label><input id={ids.to} className="input" type="datetime-local" value={to} onChange={(e) => { invalidate(); setTo(e.target.value); }} /></div>
      </div>

      <div aria-live="polite">
        {dryRun.isError && <div className="banner red" role="alert"><div className="grow">{errorText(dryRun.error)}</div></div>}
        {p && (
          <section className="sub-panel" aria-label="Dry run result" data-testid="dry-run">
            <h3>Dry run: nothing was sent</h3>
            <p style={{ margin: '8px 0' }}><b>{p.count.toLocaleString('en-US')}</b> events match{p.count > 0 ? `, about ${formatDuration(p.estimatedSeconds)} at ${p.ratePerSecond} per second` : ''}.</p>
            {p.byType.length > 0 && <p className="sm muted" style={{ margin: '0 0 8px' }}>{p.byType.map((t) => `${t.type} ×${t.count}`).join(' · ')}</p>}
            {!p.endpoint.canReceive && <div className="note warn">“{p.endpoint.name}” is {p.endpoint.status}. Resume it first (Held tab), then replay.</div>}
            {p.cap.exceeded && <div className="note red">This is more than today’s allowance ({p.cap.remaining.toLocaleString('en-US')} left). Narrow the filter.</div>}
            {p.count > 0 && p.endpoint.canReceive && !p.cap.exceeded && (
              <div className="field" style={{ marginTop: 10 }}>
                <label className="f" htmlFor={ids.confirm}>Type <b className="mono">{p.confirmPhrase}</b> to confirm</label>
                <input id={ids.confirm} className="input mono" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
                <div className="help">{p.count.toLocaleString('en-US')} events will be sent again to {p.endpoint.name}.</div>
              </div>
            )}
          </section>
        )}
        {start.isError && <div className="banner red" role="alert"><div className="grow">{errorText(start.error)}</div></div>}
      </div>
    </Modal>
  );
}

