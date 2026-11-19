import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useEndpointMutation, useRetryPresets } from '../../api/queries';
import { webhooksApi, type RetryParameters } from '../../api/webhooks';
import { formatDuration } from '../../lib/format';
import { useDebounced } from '../../lib/useDebounced';
import { RadioCards } from '../../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../../ui/Feedback';
import { useEndpointCtx } from './EndpointLayout';

const BACKOFF_LABEL: Record<string, string> = { exp2: 'Doubles each time (×2)', exp3: 'Triples each time (×3)', exp4: '×4 each time', exp6: '×6 each time', exp8: '×8 each time', linear: 'Grows by the first delay each time', fixed: 'Always the same delay' };

export function DeliveryTab() {
  const { detail, canWrite } = useEndpointCtx();
  const presets = useRetryPresets();
  const e = detail.endpoint;
  const d = detail.delivery;
  const toast = useToast();

  const [preset, setPreset] = useState(d.preset);
  const [custom, setCustom] = useState<RetryParameters | null>(d.custom);
  const [timeout, setTimeoutSeconds] = useState(d.timeoutSeconds);
  const [inflight, setInflight] = useState(d.maxInflight);
  const [circuit, setCircuit] = useState(d.circuitFailures);
  const [disable, setDisable] = useState(d.autoDisable);
  const [hold, setHold] = useState(d.holdDays);

  const effectiveCustom = custom ?? presets.data?.customDefault ?? null;
  const save = useEndpointMutation(e.id, () => webhooksApi.setDelivery(e.id, {
    preset, custom: preset === 'custom' ? effectiveCustom : undefined, circuitFailures: circuit, autoDisable: disable, holdDays: hold, timeoutSeconds: timeout, maxInflight: inflight,
  }));

  const key = useDebounced(JSON.stringify({ preset, custom: preset === 'custom' ? effectiveCustom : null }), 300);
  const preview = useQuery({
    queryKey: ['retry-preview', key],
    queryFn: ({ signal }) => { const k = JSON.parse(key) as { preset: string; custom: RetryParameters | null }; return webhooksApi.retryPreview(k.preset, k.custom, signal); },
    enabled: presets.isSuccess,
    retry: false,
  });

  const setC = (patch: Partial<RetryParameters>) => { setPreset('custom'); setCustom({ ...(effectiveCustom ?? presets.data!.customDefault), ...patch }); };
  const failuresPct = Math.min(100, Math.round((e.consecutiveFailures / Math.max(1, d.autoDisable.failures)) * 100));
  const num = (v: string, fallback: number) => (Number.isFinite(Number(v)) && v.trim() !== '' ? Number(v) : fallback);

  if (presets.isPending) return <Loading what="retry presets" />;
  if (presets.isError) return <ErrorBanner error={presets.error} onRetry={() => void presets.refetch()} />;
  const last = preview.data?.attempts.at(-1);

  return (
    <div className="split">
      <div className="stack">
        <section className="card" aria-labelledby="retry-h">
          <div className="hd"><div><h2 id="retry-h">Retries</h2><div className="muted sm">When your server fails, we try again, with growing pauses and some randomness so a recovering server is not hit all at once.</div></div></div>
          <div className="bd">
            <RadioCards legend="Retry preset" name="retry-preset" value={preset} onChange={(v) => { setPreset(v); if (v === 'custom' && !custom) setCustom(presets.data.customDefault); }} columns={2}
              options={[
                ...presets.data.presets.map((p) => ({ value: p.id, title: p.name, description: p.description, disabled: !canWrite })),
                { value: 'custom', title: 'Custom', description: 'Choose the number of attempts, the growth of the delays and how long we keep trying.', disabled: !canWrite },
              ]} />

            {preset === 'custom' && effectiveCustom && (
              <div className="sub-panel">
                <div className="grid g3">
                  <div className="field"><label className="f" htmlFor="p-attempts">Attempts (including the first)</label><input id="p-attempts" className="input" type="number" min={2} max={30} value={effectiveCustom.attempts} disabled={!canWrite} onChange={(e) => setC({ attempts: num(e.target.value, effectiveCustom.attempts) })} /></div>
                  <div className="field"><label className="f" htmlFor="p-backoff">Delay growth</label>
                    <select id="p-backoff" className="select" value={effectiveCustom.backoff} disabled={!canWrite} onChange={(e) => setC({ backoff: e.target.value })}>
                      {presets.data.backoffs.map((b) => <option key={b} value={b}>{BACKOFF_LABEL[b] ?? b}</option>)}
                    </select></div>
                  <div className="field"><label className="f" htmlFor="p-initial">First retry after (seconds)</label><input id="p-initial" className="input" type="number" min={1} max={3600} value={effectiveCustom.initialSeconds} disabled={!canWrite} onChange={(e) => setC({ initialSeconds: num(e.target.value, effectiveCustom.initialSeconds) })} /></div>
                  <div className="field"><label className="f" htmlFor="p-cap">Longest delay (seconds)</label><input id="p-cap" className="input" type="number" min={1} max={86400} value={effectiveCustom.capSeconds} disabled={!canWrite} onChange={(e) => setC({ capSeconds: num(e.target.value, effectiveCustom.capSeconds) })} /></div>
                  <div className="field"><label className="f" htmlFor="p-window">Give up after (seconds)</label><input id="p-window" className="input" type="number" min={1} max={presets.data.maxWindowSeconds} value={effectiveCustom.windowSeconds} disabled={!canWrite} onChange={(e) => setC({ windowSeconds: num(e.target.value, effectiveCustom.windowSeconds) })} /><div className="help">At most 72 hours ({formatDuration(effectiveCustom.windowSeconds)} now).</div></div>
                </div>
              </div>
            )}

            <div style={{ marginTop: 14 }} aria-live="polite">
              <h3 style={{ marginBottom: 6 }}>Schedule preview</h3>
              {preview.isFetching && !preview.data && <p className="help" role="status">Calculating…</p>}
              {preview.data && !preview.data.valid && <div className="banner amber" role="alert"><div className="grow">{preview.data.errors.map((m) => <div key={m}>{m}</div>)}</div></div>}
              {preview.data?.valid && (
                <>
                  <ol className="tl" style={{ listStyle: 'none', padding: 0 }} data-testid="schedule">
                    {preview.data.attempts.map((a, i, all) => (
                      <li key={a.number} className={`a ${i === 0 ? 'first' : i === all.length - 1 ? 'last' : ''}`}><b>Attempt {a.number}</b><span>{i === 0 ? 'immediately' : `+${formatDuration(a.delaySeconds)} · at ${formatDuration(a.atSeconds)}`}</span></li>
                    ))}
                  </ol>
                  <p className="help" data-testid="schedule-summary"><b>{preview.data.attempts.length} attempts</b>, the last one at <b>{formatDuration(last?.atSeconds ?? 0)}</b>. We stop after <b>{formatDuration(preview.data.windowSeconds)}</b> at the latest and the event moves to <b>Dead</b>, where you can replay it. Each delay gets ±{preview.data.jitterPercent} % jitter. We always honour <span className="mono">Retry-After</span> (up to 12 hours) and never follow redirects.</p>
                </>
              )}
            </div>
          </div>
        </section>

        <section className="card" aria-labelledby="speed-h">
          <div className="hd"><h2 id="speed-h">Speed &amp; limits</h2></div>
          <div className="bd grid g2">
            <div className="field"><label className="f" htmlFor="d-timeout">Wait for your answer (seconds)</label><input id="d-timeout" className="input" type="number" min={1} max={30} value={timeout} disabled={!canWrite} onChange={(e) => setTimeoutSeconds(num(e.target.value, timeout))} /><div className="help">1 to 30. Answer fast and process afterwards.</div></div>
            <div className="field"><label className="f" htmlFor="d-inflight">Requests at the same time</label><input id="d-inflight" className="input" type="number" min={1} max={10} value={inflight} disabled={!canWrite} onChange={(e) => setInflight(num(e.target.value, inflight))} /><div className="help">1 to 10. More at once is faster; fewer is gentler on your server.</div></div>
            <div className="note" style={{ gridColumn: '1 / -1' }}><b>Ordering:</b> events are delivered in parallel and may arrive out of order. Every event carries a <span className="mono">sequence</span> number per object: apply an event only if it is newer than what you hold.</div>
          </div>
        </section>

        <section className="card" aria-labelledby="prot-h">
          <div className="hd"><div><h2 id="prot-h">Protection</h2><div className="muted sm">Two different safety nets: the circuit breaker acts in minutes and heals itself; auto-disable acts over hours or days and needs a person.</div></div></div>
          <div className="bd stack">
            <div className="grid g2">
              <div className="field"><label className="f" htmlFor="d-circuit">Circuit breaker opens after (failures in a row)</label><input id="d-circuit" className="input" type="number" min={2} max={50} value={circuit} disabled={!canWrite} onChange={(e) => setCircuit(num(e.target.value, circuit))} /><div className="help">While open, events wait (they are not lost) and we probe at 30 s, doubling to 15 min. It closes on the first success.</div></div>
              <div className="field"><label className="f" htmlFor="d-hold">Keep held events for (days)</label><input id="d-hold" className="input" type="number" min={1} max={30} value={hold} disabled={!canWrite} onChange={(e) => setHold(num(e.target.value, hold))} /><div className="help">While an endpoint is paused or auto-disabled, events are held this long, then moved to Dead.</div></div>
            </div>
            <fieldset className="rcgroup">
              <legend>Auto-disable when either happens</legend>
              <div className="grid g3">
                <div className="field"><label className="f" htmlFor="d-ad-failures">Failures in a row</label><input id="d-ad-failures" className="input" type="number" min={5} max={10000} value={disable.failures} disabled={!canWrite} onChange={(e) => setDisable({ ...disable, failures: num(e.target.value, disable.failures) })} /></div>
                <div className="field"><label className="f" htmlFor="d-ad-hours">…within (hours)</label><input id="d-ad-hours" className="input" type="number" min={1} max={168} value={disable.withinHours} disabled={!canWrite} onChange={(e) => setDisable({ ...disable, withinHours: num(e.target.value, disable.withinHours) })} /></div>
                <div className="field"><label className="f" htmlFor="d-ad-days">Or failing without a break for (days)</label><input id="d-ad-days" className="input" type="number" min={1} max={30} value={disable.days} disabled={!canWrite} onChange={(e) => setDisable({ ...disable, days: num(e.target.value, disable.days) })} /></div>
              </div>
              <div className="help">Timeouts, connection errors and 5xx count. A 401 or 404 does not: that points at a setting, not a dead server. The second rule exists for quiet endpoints that would never reach the first.</div>
            </fieldset>
            <div className="note">Warnings at 25 %, 50 % and 75 % of the failure limit are recorded; email and in-app alerts for them arrive with the Alerts screen.</div>
          </div>
        </section>

        {save.isError && <ErrorBanner error={save.error} />}
        {canWrite && (
          <div className="row">
            <button type="button" className="btn pri" disabled={save.isPending || preview.data?.valid === false}
              onClick={() => save.mutate(undefined, { onSuccess: () => toast.show('Delivery settings saved'), onError: (err) => toast.show(errorText(err)) })}>
              {save.isPending ? 'Saving…' : 'Save delivery settings'}
            </button>
            <span className="muted sm">Settings apply to events delivered from now on; deliveries already waiting keep their schedule.</span>
          </div>
        )}
      </div>

      <aside className="sticky" aria-label="Live state">
        <section className="card" aria-labelledby="live-h">
          <div className="hd"><h3 id="live-h">Live failure counter</h3></div>
          <div className="bd">
            <div className="row sb"><span>Failures in a row</span><b data-testid="failures">{e.consecutiveFailures}</b></div>
            <div className={`bar ${failuresPct >= 75 ? 'red' : failuresPct >= 25 ? 'amber' : 'green'}`} role="progressbar" aria-label="Failures against the auto-disable limit" aria-valuemin={0} aria-valuemax={d.autoDisable.failures} aria-valuenow={e.consecutiveFailures} style={{ margin: '6px 0' }}><i style={{ width: `${failuresPct}%` }} /></div>
            <div className="help">{e.consecutiveFailures} of {d.autoDisable.failures} before auto-disable · the circuit opens at {d.circuitFailures}.</div>
            {e.circuitOpenUntil && <div className="note warn" style={{ marginTop: 8 }}>The circuit is open. We try again at {new Date(e.circuitOpenUntil).toLocaleTimeString()}.</div>}
            <dl className="facts" style={{ gridTemplateColumns: '110px 1fr', marginTop: 10 }}>
              <dt>Waiting</dt><dd>{e.backlog.pending} pending</dd>
              <dt>Retrying</dt><dd>{e.backlog.retrying}</dd>
              <dt>Held</dt><dd>{e.backlog.held}</dd>
            </dl>
          </div>
        </section>
      </aside>
    </div>
  );
}
