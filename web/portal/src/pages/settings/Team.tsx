import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { teamApi, type TeamMember } from '../../api/advanced';
import { useCan } from '../../auth/me';
import { timeAgo } from '../../lib/format';
import { usePageTitle } from '../../lib/usePageTitle';
import { CopyButton, NoAccess } from '../../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../../ui/Feedback';
import { Icon } from '../../ui/Icon';
import { Modal } from '../../ui/Modal';

const ROLE_COLOUR: Record<string, string> = { owner: 'purple', admin: 'blue', developer: 'green', support: 'amber', viewer: 'gray' };
const ROLE_HELP: Record<string, string> = {
  owner: 'Everything, including other owners.',
  admin: 'Everything except changing owners.',
  developer: 'Endpoints, secrets, replay and API keys.',
  support: 'Look at deliveries and payloads, replay one event.',
  viewer: 'Look at endpoints and numbers only.',
};

/** Team and roles. Identity lives in Keycloak; this screen changes it there and keeps the tenant's own view in step. Nobody can lock the organisation out. */
export function Team() {
  usePageTitle('Team & roles');
  const can = useCan();
  const client = useQueryClient();
  const toast = useToast();
  const team = useQuery({ queryKey: ['team'], queryFn: ({ signal }) => teamApi.list(signal), enabled: can('roles:manage') });
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState<{ name: string; email: string; password: string } | null>(null);
  const refresh = () => client.invalidateQueries({ queryKey: ['team'] });
  const role = useMutation({
    mutationFn: ({ id, role }: { id: string; role: string }) => teamApi.setRole(id, role),
    onSuccess: () => { toast.show('Role changed'); return refresh(); },
    onError: (e) => { toast.show(errorText(e)); return refresh(); },
  });
  const toggle = useMutation({
    mutationFn: ({ m }: { m: TeamMember }) => (m.active ? teamApi.disable(m.id) : teamApi.enable(m.id)),
    onSuccess: (_, { m }) => { toast.show(m.active ? `${m.name} was disabled and signed out` : `${m.name} can sign in again`); return refresh(); },
    onError: (e) => toast.show(errorText(e)),
  });

  return (
    <>
      <div className="page-h">
        <div>
          <h1>Team &amp; roles</h1>
          <p className="sub">Who can do what in your organisation. Changes take effect straight away.</p>
        </div>
        {can('roles:manage') && <button type="button" className="btn pri" onClick={() => setAdding(true)}><Icon name="plus" />Add team member</button>}
      </div>

      {!can('roles:manage') ? <NoAccess what="manage the team" /> : (
        <>
          {team.isPending && <Loading what="the team" />}
          {team.isError && <ErrorBanner error={team.error} onRetry={() => void team.refetch()} />}
          {team.data && (
            <div className="card">
              <div className="table-wrap">
                <table aria-label="Team members">
                  <thead><tr><th scope="col">Person</th><th scope="col">Role</th><th scope="col">Last seen</th><th scope="col">Status</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
                  <tbody>
                    {team.data.items.map((m) => {
                      const locked = m.you || (m.role === 'owner' && !team.data.canManageOwners);
                      return (
                        <tr key={m.id}>
                          <td><b>{m.name}</b>{m.you && <span className="tag" style={{ marginLeft: 6 }}>you</span>}<div className="muted sm">{m.email}</div></td>
                          <td>
                            <select className="select role-select" aria-label={`Role of ${m.name}`} value={m.role} disabled={locked || role.isPending}
                              onChange={(e) => role.mutate({ id: m.id, role: e.target.value })}>
                              {team.data.roles.filter((r) => r.role !== 'owner' || team.data.canManageOwners || m.role === 'owner').map((r) => <option key={r.role} value={r.role}>{r.role}</option>)}
                            </select>
                          </td>
                          <td className="sm">{m.lastSeenAt ? timeAgo(m.lastSeenAt) : 'never'}</td>
                          <td><span className={`pill ${m.active ? 'green' : 'gray'}`}>{m.active ? 'Active' : 'Disabled'}</span></td>
                          <td><div className="acts">{!m.you && !(m.role === 'owner' && !team.data.canManageOwners) && (
                            <button type="button" className={`btn sm${m.active ? ' dng' : ''}`} disabled={toggle.isPending} aria-label={`${m.active ? 'Disable' : 'Enable'} ${m.name}`} onClick={() => toggle.mutate({ m })}>{m.active ? 'Disable' : 'Enable'}</button>
                          )}</div></td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          )}
          {team.data && (
            <section className="card" aria-labelledby="roles-h">
              <div className="hd"><h2 id="roles-h">What each role can do</h2></div>
              <div className="bd">
                <dl className="kvlist">
                  {team.data.roles.map((r) => (<div key={r.role} style={{ display: 'contents' }}><dt><span className={`pill ${ROLE_COLOUR[r.role] ?? 'gray'}`}>{r.role}</span></dt><dd>{ROLE_HELP[r.role] ?? ''}</dd></div>))}
                </dl>
                <p className="help">There is always at least one owner: the last one cannot be demoted or disabled. You cannot change your own role or disable yourself.</p>
              </div>
            </section>
          )}
        </>
      )}

      {adding && team.data && <AddDialog roles={team.data.roles.map((r) => r.role).filter((r) => r !== 'owner' || team.data.canManageOwners)} onClose={() => setAdding(false)}
        onAdded={(m) => { setAdding(false); setAdded(m); void refresh(); }} />}
      {added && (
        <Modal open title={`${added.name} was added`} onClose={() => setAdded(null)} footer={<button type="button" className="btn pri" onClick={() => setAdded(null)}>Done</button>}>
          <div className="banner amber" role="note"><Icon name="alert" /><div className="grow">Give {added.name} this <b>temporary password</b>. They must choose their own when they first sign in. It is not shown again.</div></div>
          <dl className="kvlist"><dt>Sign in with</dt><dd className="mono">{added.email}</dd></dl>
          <div className="secret-box" data-testid="temp-password">{added.password}</div>
          <div className="row" style={{ marginTop: 10 }}><CopyButton text={added.password} what="The password" label="Copy password" /></div>
        </Modal>
      )}
    </>
  );
}

function AddDialog({ roles, onClose, onAdded }: { roles: string[]; onClose: () => void; onAdded: (m: { name: string; email: string; password: string }) => void }) {
  const ids = { first: useId(), last: useId(), email: useId(), role: useId() };
  const [first, setFirst] = useState('');
  const [last, setLast] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState(roles.includes('support') ? 'support' : (roles[0] ?? 'viewer'));
  const add = useMutation({
    mutationFn: () => teamApi.invite({ email: email.trim(), firstName: first.trim(), lastName: last.trim(), role }),
    onSuccess: (r) => onAdded({ name: r.name, email: r.email, password: r.temporaryPassword }),
  });
  return (
    <Modal open onClose={onClose} title="Add a team member"
      footer={<><button type="button" className="btn" onClick={onClose}>Cancel</button><button type="button" className="btn pri" disabled={!first.trim() || !last.trim() || !email.includes('@') || add.isPending} onClick={() => add.mutate()}>{add.isPending ? 'Adding…' : 'Add member'}</button></>}>
      <div className="row two">
        <div className="field"><label className="f" htmlFor={ids.first}>First name</label><input id={ids.first} className="input" value={first} onChange={(e) => setFirst(e.target.value)} autoComplete="off" /></div>
        <div className="field"><label className="f" htmlFor={ids.last}>Last name</label><input id={ids.last} className="input" value={last} onChange={(e) => setLast(e.target.value)} autoComplete="off" /></div>
      </div>
      <div className="field"><label className="f" htmlFor={ids.email}>Work email</label><input id={ids.email} className="input" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" /></div>
      <div className="field">
        <label className="f" htmlFor={ids.role}>Role</label>
        <select id={ids.role} className="select" value={role} onChange={(e) => setRole(e.target.value)}>{roles.map((r) => <option key={r} value={r}>{r} — {ROLE_HELP[r]}</option>)}</select>
      </div>
      {add.isError && <div className="banner red" role="alert"><div className="grow">{errorText(add.error)}</div></div>}
    </Modal>
  );
}
