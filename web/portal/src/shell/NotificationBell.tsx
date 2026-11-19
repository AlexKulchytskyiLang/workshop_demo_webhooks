import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { alertsApi, type InboxItem } from '../api/advanced';
import { timeAgo } from '../lib/format';
import { Icon } from '../ui/Icon';

const SEVERITY: Record<InboxItem['severity'], { colour: string; label: string }> = {
  info: { colour: 'blue', label: 'Info' },
  warning: { colour: 'amber', label: 'Warning' },
  critical: { colour: 'red', label: 'Needs action' },
};

/**
 * The bell: alerts addressed to your role, newest first, with an unread count. It polls gently; the count is also in the button's name,
 * so a screen reader hears "Notifications, 3 unread" without opening anything.
 */
export function NotificationBell() {
  const client = useQueryClient();
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const root = useRef<HTMLDivElement>(null);
  const inbox = useQuery({ queryKey: ['inbox'], queryFn: ({ signal }) => alertsApi.inbox(signal), refetchInterval: 15_000 });
  const refresh = () => client.invalidateQueries({ queryKey: ['inbox'] });
  const read = useMutation({ mutationFn: (id: number) => alertsApi.markRead(id), onSuccess: refresh });
  const readAll = useMutation({ mutationFn: () => alertsApi.readAll(), onSuccess: refresh });

  useEffect(() => {
    if (!open) return undefined;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    const onClick = (e: MouseEvent) => { if (root.current && !root.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onClick);
    return () => { document.removeEventListener('keydown', onKey); document.removeEventListener('mousedown', onClick); };
  }, [open]);

  const unread = inbox.data?.unread ?? 0;
  const items = inbox.data?.items ?? [];

  return (
    <div className="bell" ref={root}>
      <button type="button" className="btn icon" aria-expanded={open} aria-controls={panelId} aria-label={`Notifications, ${unread} unread`} onClick={() => setOpen((o) => !o)}>
        <Icon name="bell" />
        {unread > 0 && <span className="count" aria-hidden="true">{unread > 99 ? '99+' : unread}</span>}
      </button>
      {open && (
        <div id={panelId} className="card bell-panel" role="region" aria-label="Notifications">
          <div className="hd">
            <h2>Notifications</h2>
            <button type="button" className="btn sm" disabled={unread === 0 || readAll.isPending} onClick={() => readAll.mutate()}>Mark all as read</button>
          </div>
          {items.length === 0 ? (
            <div className="empty"><b>All quiet</b>Alerts about your endpoints appear here.</div>
          ) : (
            <ul>
              {items.map((i) => (
                <li key={i.id} className={i.read ? undefined : 'unread'}>
                  <div className="row wrap">
                    <span className={`pill ${SEVERITY[i.severity].colour}`}>{SEVERITY[i.severity].label}</span>
                    <span className="muted sm">{timeAgo(i.at)}</span>
                    {!i.read && <span className="sr-only">Unread</span>}
                  </div>
                  <div className="t" style={{ marginTop: 4 }}>{i.title}</div>
                  <div className="sm muted" style={{ margin: '2px 0 6px' }}>{i.body}</div>
                  <div className="row">
                    {i.endpointId && <Link className="btn sm" to={i.kind === 'auto_disabled' ? '/replay' : `/endpoints/${i.endpointId}/logs`} onClick={() => { if (!i.read) read.mutate(i.id); setOpen(false); }}>{i.kind === 'auto_disabled' ? 'Open the Replay center' : 'Open the delivery log'}</Link>}
                    {!i.read && <button type="button" className="btn sm gh" onClick={() => read.mutate(i.id)}>Mark as read</button>}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
