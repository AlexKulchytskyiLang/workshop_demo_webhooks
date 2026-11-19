import { useInfiniteQuery } from '@tanstack/react-query';
import { useId, useMemo, useState } from 'react';
import { auditApi, type AuditLine } from '../../api/advanced';
import { useCan } from '../../auth/me';
import { AUDIT_AREAS, auditLabel, auditSummary } from '../../lib/audit';
import { formatUtc, timeAgo } from '../../lib/format';
import { useDebounced } from '../../lib/useDebounced';
import { usePageTitle } from '../../lib/usePageTitle';
import { NoAccess } from '../../ui/Display';
import { ErrorBanner, Loading } from '../../ui/Feedback';

/** Who changed what, when and from where. Append-only: the platform's own database logins cannot change or delete a line, and a line is written in the same transaction as the change it describes. */
export function AuditLog() {
  usePageTitle('Audit log');
  const can = useCan();
  const [area, setArea] = useState('');
  const [actorText, setActorText] = useState('');
  const actor = useDebounced(actorText.trim(), 350);
  const areaId = useId();
  const actorId = useId();

  const query = useInfiniteQuery({
    queryKey: ['audit', area, actor],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => auditApi.list({ action: area || undefined, actor: actor || undefined }, pageParam, signal),
    getNextPageParam: (last) => last.nextCursor,
    enabled: can('audit:view'),
    refetchInterval: 15_000,
  });
  const rows = useMemo(() => query.data?.pages.flatMap((p) => p.items) ?? [], [query.data]);

  return (
    <>
      <div className="page-h">
        <div>
          <h1>Audit log</h1>
          <p className="sub">Every change to your endpoints, keys and team, with who made it. It cannot be edited or deleted.</p>
        </div>
      </div>
      {!can('audit:view') ? <NoAccess what="read the audit log" /> : (
        <>
          <div className="filters">
            <div className="field">
              <label className="f" htmlFor={areaId}>Area</label>
              <select id={areaId} className="select" value={area} onChange={(e) => setArea(e.target.value)}>
                {AUDIT_AREAS.map((a) => <option key={a.value} value={a.value}>{a.label}</option>)}
              </select>
            </div>
            <div className="field grow">
              <label className="f" htmlFor={actorId}>Who (exact sign-in name)</label>
              <input id={actorId} className="input" type="search" value={actorText} onChange={(e) => setActorText(e.target.value)} placeholder="developer@yourcompany.com" autoComplete="off" />
            </div>
          </div>
          {query.isError && <ErrorBanner error={query.error} onRetry={() => void query.refetch()} />}
          <div className="card">
            {query.isPending ? <div className="bd"><Loading what="the audit log" /></div>
              : rows.length === 0 ? <div className="empty"><b>Nothing here</b>No recorded changes match.</div>
              : (
                <div className="table-wrap">
                  <table aria-label="Audit log">
                    <thead><tr><th scope="col">When</th><th scope="col">Who</th><th scope="col">What</th><th scope="col">On</th></tr></thead>
                    <tbody>{rows.map((l) => <AuditRow key={l.id} line={l} />)}</tbody>
                  </table>
                </div>
              )}
            {query.hasNextPage && (
              <div style={{ display: 'flex', justifyContent: 'center', padding: 12, borderTop: '1px solid var(--border)' }}>
                <button type="button" className="btn" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>{query.isFetchingNextPage ? 'Loading…' : 'Load more'}</button>
              </div>
            )}
          </div>
        </>
      )}
    </>
  );
}

function AuditRow({ line }: { line: AuditLine }) {
  const summary = auditSummary(line);
  return (
    <tr className="audit-row" data-testid="audit-row">
      <td className="nw" title={formatUtc(line.at)}>{timeAgo(line.at)}</td>
      <td>{line.actor}{line.actorRole && <div className="muted sm">{line.actorRole}</div>}</td>
      <td>
        <b>{auditLabel(line.action)}</b>
        {summary && <div className="muted sm">{summary}</div>}
        <details>
          <summary className="sm muted">Details</summary>
          <pre>{JSON.stringify({ action: line.action, at: line.at, ip: line.ip, traceId: line.traceId, details: line.details }, null, 2)}</pre>
        </details>
      </td>
      <td>{line.targetName ?? line.targetId ?? '—'}<div className="muted sm">{line.targetType.replaceAll('_', ' ')}</div></td>
    </tr>
  );
}
