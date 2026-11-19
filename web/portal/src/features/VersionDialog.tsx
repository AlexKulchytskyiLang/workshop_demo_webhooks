import { useQuery } from '@tanstack/react-query';
import { useId, useMemo, useState } from 'react';
import { versionsApi, type ApiVersionInfo } from '../api/advanced';
import { useEndpointMutation } from '../api/queries';
import { webhooksApi } from '../api/webhooks';
import { diffJson } from '../lib/jsonDiff';
import { CodeBlock } from '../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../ui/Feedback';
import { Icon } from '../ui/Icon';
import { Modal } from '../ui/Modal';

interface Props {
  endpointId: string;
  current: string;
  events: string[];
  versions: ApiVersionInfo[];
  canWrite: boolean;
  onClose: () => void;
}

const show = (v: unknown) => (v === undefined ? '' : typeof v === 'string' ? `"${v}"` : JSON.stringify(v));

/**
 * The upgrade flow (UX-SPEC D5): see exactly what changes ON A REAL SAMPLE of the event, then decide. Nothing moves on its own.
 * Switching affects only deliveries created from now on; what is already queued, and its retries, keeps the version it was created with.
 */
export function VersionDialog({ endpointId, current, events, versions, canWrite, onClose }: Props) {
  const toast = useToast();
  const [target, setTarget] = useState(versions.find((v) => v.version !== current)?.version ?? current);
  const [type, setType] = useState(events.find((e) => e.startsWith('invoice.')) ?? events.find((e) => e.startsWith('payment.')) ?? events[0] ?? 'invoice.paid');
  const targetId = useId();
  const typeId = useId();
  const diff = useQuery({ queryKey: ['version-diff', current, target, type], queryFn: ({ signal }) => versionsApi.diff(current, target, type, signal), enabled: target !== current });
  const switchTo = useEndpointMutation(endpointId, () => webhooksApi.updateEndpoint(endpointId, { apiVersion: target }));
  const info = versions.find((v) => v.version === target);
  const lines = useMemo(() => (diff.data ? diffJson(diff.data.from.data, diff.data.to.data) : []), [diff.data]);

  return (
    <Modal open wide onClose={onClose} title="Compare payload versions"
      footer={<>
        <button type="button" className="btn" onClick={onClose}>Close</button>
        {canWrite && <button type="button" className="btn pri" disabled={target === current || switchTo.isPending}
          onClick={() => switchTo.mutate(undefined, { onSuccess: () => { toast.show(`Now on ${target}. New events use it; queued ones keep their version.`); onClose(); } })}>
          {switchTo.isPending ? 'Switching…' : `Switch to ${target}`}
        </button>}
      </>}>
      <div className="row two">
        <div className="field"><span className="f" style={{ display: 'block', fontSize: 12, fontWeight: 600, marginBottom: 4 }}>Pinned now</span><b className="mono">{current}</b></div>
        <div className="field">
          <label className="f" htmlFor={targetId}>Compare with</label>
          <select id={targetId} className="select" value={target} onChange={(e) => setTarget(e.target.value)}>
            {versions.filter((v) => v.version !== current).map((v) => <option key={v.version} value={v.version}>{v.version} · {v.status}</option>)}
          </select>
        </div>
      </div>
      <div className="field">
        <label className="f" htmlFor={typeId}>On this event</label>
        <select id={typeId} className="select mono" value={type} onChange={(e) => setType(e.target.value)}>
          {(events.length > 0 ? events : [type]).map((e) => <option key={e} value={e}>{e}</option>)}
        </select>
      </div>

      {info?.status === 'preview' && <div className="banner amber" role="note"><Icon name="alert" /><div className="grow"><b>Preview.</b> This version can still change before it is released. Try it in the Playground; do not point production at it.</div></div>}
      {info?.sunsetOn && <div className="banner amber" role="note"><Icon name="clock" /><div className="grow"><b>{info.version} ends on {info.sunsetOn}.</b> Plan the move before then.</div></div>}
      {info && info.changes.length > 0 && (
        <section aria-labelledby="chg-h">
          <h3 id="chg-h" style={{ marginBottom: 6 }}>What {info.version} does differently from the current shape</h3>
          <ul style={{ margin: '0 0 12px', paddingLeft: 18 }}>{info.changes.map((c) => <li key={c}>{c}</li>)}</ul>
        </section>
      )}

      {diff.isPending && target !== current && <Loading what="the comparison" />}
      {diff.isError && <ErrorBanner error={diff.error} />}
      {diff.data && (
        <div data-testid="version-diff">
          <h3 style={{ marginBottom: 6 }}>Field by field, on a sample <span className="mono">{type}</span></h3>
          {lines.length === 0 ? <p className="help">No field differs for this event: the versions only differ for other events.</p> : (
            <table aria-label="Differences" className="test-table">
              <thead><tr><th scope="col">Field</th><th scope="col">{current}</th><th scope="col">{target}</th></tr></thead>
              <tbody>
                {lines.map((l) => (
                  <tr key={l.path}>
                    <td className="mono">{l.path}</td>
                    <td className="mono">{l.kind === 'added' ? <span className="muted">(absent)</span> : <span style={{ color: l.kind === 'removed' ? 'var(--red)' : undefined }}>{show(l.before)}</span>}</td>
                    <td className="mono">{l.kind === 'removed' ? <span className="muted">(absent)</span> : <span style={{ color: l.kind === 'added' ? 'var(--green)' : undefined }}>{show(l.after)}</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <div className="row two" style={{ marginTop: 12 }}>
            <CodeBlock code={JSON.stringify(diff.data.from.data, null, 2)} label={`Sample in ${current}`} />
            <CodeBlock code={JSON.stringify(diff.data.to.data, null, 2)} label={`Sample in ${target}`} />
          </div>
          <p className="help">Filters run on the canonical event, so switching never changes <i>which</i> events you receive. Events that are already queued, and their retries, keep the version they were created with.</p>
        </div>
      )}
      {switchTo.isError && <div className="banner red" role="alert"><div className="grow">{errorText(switchTo.error)}</div></div>}
    </Modal>
  );
}
