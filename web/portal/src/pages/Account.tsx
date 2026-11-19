import { useMe } from '../auth/me';
import { usePageTitle } from '../lib/usePageTitle';

const PERMISSION_LABELS: Record<string, string> = {
  'endpoints:read': 'View endpoints and metrics',
  'endpoints:write': 'Create and edit endpoints',
  'endpoints:delete': 'Delete endpoints',
  'secrets:reveal': 'Reveal and rotate secrets',
  'payloads:view': 'View event payloads',
  'replay:run': 'Replay in bulk',
  'replay:single': 'Replay a single event',
  'alerts:manage': 'Manage alert rules',
  'roles:manage': 'Manage team roles',
  'audit:view': 'View the audit log',
  'apikeys:manage': 'Manage API keys',
};

/** Who you are signed in as and what your role allows. The API enforces every one of these; the screens hide what you cannot use. */
export function Account() {
  usePageTitle('Your access');
  const me = useMe();
  const granted = new Set(me.permissions);

  return (
    <>
      <div className="page-h">
        <div>
          <h1>Welcome, {me.user.name.split(' ')[0]}</h1>
          <p className="sub">You are signed in to <b>{me.tenant.name}</b> as <b>{me.user.role}</b>.</p>
        </div>
      </div>

      <div className="grid g2">
        <section className="card" aria-labelledby="who-h">
          <div className="hd"><h2 id="who-h">Your session</h2></div>
          <dl className="bd facts">
            <dt>Organisation</dt><dd>{me.tenant.name} <span className="chip">{me.tenant.slug}</span></dd>
            <dt>Name</dt><dd>{me.user.name}</dd>
            <dt>Email</dt><dd>{me.user.email ?? 'not shared'}</dd>
            <dt>Role</dt><dd>{me.user.role}</dd>
          </dl>
        </section>

        <section className="card" aria-labelledby="perm-h">
          <div className="hd"><h2 id="perm-h">What your role allows</h2><span className="muted sm">{granted.size} of {Object.keys(PERMISSION_LABELS).length}</span></div>
          <ul className="bd perm-list">
            {Object.entries(PERMISSION_LABELS).map(([key, label]) => (
              <li key={key} className={granted.has(key) ? 'yes' : 'no'}>
                <span aria-hidden="true">{granted.has(key) ? '✓' : '–'}</span>
                <span>{label}</span>
                <span className="sr-only">{granted.has(key) ? 'allowed' : 'not allowed'}</span>
              </li>
            ))}
          </ul>
        </section>
      </div>
    </>
  );
}
