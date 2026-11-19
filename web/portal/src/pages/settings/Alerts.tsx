import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { alertsApi, type AlertRule } from '../../api/advanced';
import { useCan } from '../../auth/me';
import { timeAgo } from '../../lib/format';
import { usePageTitle } from '../../lib/usePageTitle';
import { NoAccess } from '../../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../../ui/Feedback';

const ROLE_LABEL: Record<string, string> = { owner: 'Owners', admin: 'Admins', developer: 'Developers', support: 'Support', viewer: 'Viewers' };
const MAIL_URL = (import.meta.env as Record<string, string | undefined>).VITE_MAIL_URL ?? 'http://localhost:8025';

/** Who is told when something goes wrong with an endpoint (UX-SPEC D10): by role, in the portal and/or by email. Every alert starts from a platform event, so it is the same pipeline as everything else. */
export function Alerts() {
  usePageTitle('Alerts');
  const can = useCan();
  const rules = useQuery({ queryKey: ['alert-rules'], queryFn: ({ signal }) => alertsApi.rules(signal) });
  return (
    <>
      <div className="page-h">
        <div>
          <h1>Alerts</h1>
          <p className="sub">Choose who hears about problems, and how. The defaults protect you from day one.</p>
        </div>
      </div>
      {!can('alerts:manage') && <NoAccess what="change alert rules (you can see them)" />}
      <div className="banner blue" role="note"><div className="grow">In this demo, alert emails are caught by Mailpit and never leave your machine. Read them at <a href={MAIL_URL} target="_blank" rel="noreferrer">{MAIL_URL}</a>.</div></div>
      {rules.isPending && <Loading what="alert rules" />}
      {rules.isError && <ErrorBanner error={rules.error} onRetry={() => void rules.refetch()} />}
      <div className="stack">
        {rules.data?.items.map((r) => <RuleCard key={`${r.trigger}-${r.updatedAt ?? 'default'}`} rule={r} roles={rules.data.roles} canEdit={can('alerts:manage')} />)}
      </div>
    </>
  );
}

function RuleCard({ rule, roles, canEdit }: { rule: AlertRule; roles: string[]; canEdit: boolean }) {
  const client = useQueryClient();
  const toast = useToast();
  const id = useId();
  const [enabled, setEnabled] = useState(rule.enabled);
  const [inApp, setInApp] = useState(rule.inApp);
  const [email, setEmail] = useState(rule.email);
  const [chosen, setChosen] = useState<string[]>(rule.roles);
  const dirty = enabled !== rule.enabled || inApp !== rule.inApp || email !== rule.email || chosen.slice().sort().join() !== rule.roles.slice().sort().join();
  const save = useMutation({
    mutationFn: () => alertsApi.setRule(rule.trigger, { enabled, inApp, email, roles: chosen }),
    onSuccess: async () => { toast.show(`Saved: ${rule.label}`); await client.invalidateQueries({ queryKey: ['alert-rules'] }); },
  });
  const toggleRole = (role: string) => setChosen((cur) => (cur.includes(role) ? cur.filter((r) => r !== role) : [...cur, role]));

  return (
    <section className="card" aria-labelledby={`${id}-h`}>
      <div className="hd">
        <div>
          <h2 id={`${id}-h`}>{rule.label}</h2>
          <div className="muted sm">{rule.isDefault ? 'Platform default' : `Changed by ${rule.updatedBy ?? 'someone'} ${timeAgo(rule.updatedAt)}`}</div>
        </div>
        <label className="row" style={{ cursor: canEdit ? 'pointer' : 'default' }}>
          <input type="checkbox" role="switch" checked={enabled} disabled={!canEdit} onChange={(e) => setEnabled(e.target.checked)} />
          <span>{enabled ? 'On' : 'Off'}<span className="sr-only"> for {rule.label}</span></span>
        </label>
      </div>
      <div className="bd">
        <p style={{ margin: '0 0 12px' }}>{rule.description}</p>
        <div className="row two">
          <fieldset className="rcgroup">
            <legend>How</legend>
            <label className="row check"><input type="checkbox" checked={inApp} disabled={!canEdit || !enabled} onChange={(e) => setInApp(e.target.checked)} /><span>In the portal (the bell)</span></label>
            <label className="row check"><input type="checkbox" checked={email} disabled={!canEdit || !enabled} onChange={(e) => setEmail(e.target.checked)} /><span>By email</span></label>
          </fieldset>
          <fieldset className="rcgroup">
            <legend>Who</legend>
            {roles.map((role) => (
              <label key={role} className="row check"><input type="checkbox" checked={chosen.includes(role)} disabled={!canEdit || !enabled} onChange={() => toggleRole(role)} /><span>{ROLE_LABEL[role] ?? role}</span></label>
            ))}
          </fieldset>
        </div>
        {save.isError && <div className="error-text" role="alert">{errorText(save.error)}</div>}
        {canEdit && <button type="button" className="btn pri" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>{save.isPending ? 'Saving…' : `Save ${rule.label}`}</button>}
      </div>
    </section>
  );
}
