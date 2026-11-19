import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { keysApi, type ApiKeyRow, type CreatedKey } from '../../api/advanced';
import { useCan } from '../../auth/me';
import { withStepUp } from '../../auth/stepUp';
import { config } from '../../config';
import { formatUtc, timeAgo } from '../../lib/format';
import { usePageTitle } from '../../lib/usePageTitle';
import { CodeBlock, CopyButton, NoAccess } from '../../ui/Display';
import { ErrorBanner, Loading, errorText, useToast } from '../../ui/Feedback';
import { Icon } from '../../ui/Icon';
import { ConfirmTyped, Modal } from '../../ui/Modal';

/** API keys for the pull API, and the fixed addresses our webhooks come from. A key is shown once, after a fresh sign-in; only a hash is stored. */
export function ApiKeys() {
  usePageTitle('API keys & IPs');
  const can = useCan();
  const client = useQueryClient();
  const toast = useToast();
  const keys = useQuery({ queryKey: ['api-keys'], queryFn: ({ signal }) => keysApi.list(signal), enabled: can('apikeys:manage') });
  const ips = useQuery({ queryKey: ['outbound-ips'], queryFn: ({ signal }) => keysApi.outboundIps(signal), staleTime: 60_000 });
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<CreatedKey | null>(null);
  const [revoking, setRevoking] = useState<ApiKeyRow | null>(null);
  const revoke = useMutation({
    mutationFn: (id: string) => keysApi.revoke(id),
    onSuccess: async () => { toast.show('Key revoked. It stops working immediately.'); setRevoking(null); await client.invalidateQueries({ queryKey: ['api-keys'] }); },
  });

  return (
    <>
      <div className="page-h">
        <div>
          <h1>API keys &amp; IPs</h1>
          <p className="sub">Keys let your systems read from the platform (for example to reconcile events). Addresses let you allow our servers through your firewall.</p>
        </div>
        {can('apikeys:manage') && <button type="button" className="btn pri" onClick={() => setCreating(true)}><Icon name="key" />Create API key</button>}
      </div>

      {!can('apikeys:manage') ? <NoAccess what="manage API keys" /> : (
        <section className="card" aria-labelledby="keys-h">
          <div className="hd"><h2 id="keys-h">API keys</h2></div>
          {keys.isPending && <div className="bd"><Loading what="API keys" /></div>}
          {keys.isError && <div className="bd"><ErrorBanner error={keys.error} onRetry={() => void keys.refetch()} /></div>}
          {keys.data && keys.data.items.length === 0 && <div className="empty"><b>No API keys yet</b>Create one to call the pull API, for example <span className="mono">GET /v1/events</span>.</div>}
          {keys.data && keys.data.items.length > 0 && (
            <div className="table-wrap">
              <table aria-label="API keys">
                <thead><tr><th scope="col">Name</th><th scope="col">Key</th><th scope="col">Can</th><th scope="col">Created</th><th scope="col">Expires</th><th scope="col">Last used</th><th scope="col">Status</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
                <tbody>
                  {keys.data.items.map((k) => (
                    <tr key={k.id}>
                      <td><b>{k.name}</b></td>
                      <td className="mono sm">{k.prefix}…</td>
                      <td>{k.scopes.map((s) => <span key={s} className="chip" style={{ marginRight: 4 }}>{s}</span>)}</td>
                      <td className="sm" title={formatUtc(k.createdAt)}>{timeAgo(k.createdAt)} by {k.createdBy}</td>
                      <td className="sm">{k.expiresAt ? formatUtc(k.expiresAt) : 'never'}</td>
                      <td className="sm">{k.lastUsedAt ? timeAgo(k.lastUsedAt) : 'never used'}</td>
                      <td><span className={`pill ${k.status === 'active' ? 'green' : 'gray'}`}>{k.status === 'active' ? 'Active' : k.status === 'revoked' ? 'Revoked' : 'Expired'}</span></td>
                      <td><div className="acts">{k.status === 'active' && <button type="button" className="btn sm dng" aria-label={`Revoke ${k.name}`} onClick={() => setRevoking(k)}>Revoke</button>}</div></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      <section className="card" aria-labelledby="ips-h">
        <div className="hd"><h2 id="ips-h">Our outbound addresses</h2></div>
        <div className="bd">
          {ips.isPending && <Loading what="addresses" />}
          {ips.data && (
            <>
              <p style={{ margin: '0 0 10px' }}>{ips.data.note}</p>
              <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }} aria-label="Outbound addresses">
                {ips.data.addresses.map((a) => <li key={a} className="row"><span className="copy">{a}</span><CopyButton text={a} what="The address" /></li>)}
              </ul>
            </>
          )}
        </div>
      </section>

      <section className="card" aria-labelledby="use-h">
        <div className="hd"><h2 id="use-h">Using a key</h2></div>
        <div className="bd stack">
          <p style={{ margin: 0 }}>Send the key as a bearer token. The pull API returns your events oldest first, so a job can walk forward and compare them with what it received.</p>
          <CodeBlock label="Example request" code={`curl "${config.apiUrl}/v1/events?limit=50" \\\n  -H "Authorization: Bearer ihk_…your key…"`} />
        </div>
      </section>

      {creating && keys.data && (
        <CreateKeyDialog scopes={keys.data.scopes} expiryChoices={keys.data.expiryChoices} onClose={() => setCreating(false)}
          onCreated={(k) => { setCreating(false); setCreated(k); void client.invalidateQueries({ queryKey: ['api-keys'] }); }} />
      )}
      {created && (
        <Modal open title="Your new API key" onClose={() => setCreated(null)} footer={<button type="button" className="btn pri" onClick={() => setCreated(null)}>I have copied it</button>}>
          <div className="banner amber" role="note"><Icon name="alert" /><div className="grow"><b>Copy it now.</b> This is the only time the key is shown. We store only a fingerprint of it, so we cannot show it again.</div></div>
          <div className="secret-box" data-testid="new-key">{created.key}</div>
          <div className="row" style={{ marginTop: 10 }}><CopyButton text={created.key} what="The API key" label="Copy key" /></div>
        </Modal>
      )}
      <ConfirmTyped open={revoking !== null} title="Revoke this key?" phrase={revoking?.name ?? ''} confirmLabel="Revoke key" busy={revoke.isPending} error={revoke.isError ? errorText(revoke.error) : null}
        consequence={<>Anything that uses <b>{revoking?.name}</b> stops working immediately. This cannot be undone; you can create a new key.</>}
        onConfirm={() => revoking && revoke.mutate(revoking.id)} onClose={() => setRevoking(null)} />
    </>
  );
}

function CreateKeyDialog({ scopes, expiryChoices, onClose, onCreated }: { scopes: { scope: string; description: string }[]; expiryChoices: number[]; onClose: () => void; onCreated: (k: CreatedKey) => void }) {
  const nameId = useId();
  const expiryId = useId();
  const [name, setName] = useState('');
  const [chosen, setChosen] = useState<string[]>([]);
  const [expires, setExpires] = useState<string>('90');
  const create = useMutation({
    mutationFn: () => withStepUp(() => keysApi.create({ name: name.trim(), scopes: chosen, expiresInDays: expires === 'never' ? undefined : Number(expires) })),
    onSuccess: onCreated,
  });
  const toggle = (s: string) => setChosen((c) => (c.includes(s) ? c.filter((x) => x !== s) : [...c, s]));

  return (
    <Modal open onClose={onClose} title="Create an API key"
      footer={<><button type="button" className="btn" onClick={onClose}>Cancel</button><button type="button" className="btn pri" disabled={name.trim() === '' || chosen.length === 0 || create.isPending} onClick={() => create.mutate()}>{create.isPending ? 'Creating…' : 'Create key'}</button></>}>
      <div className="field">
        <label className="f" htmlFor={nameId}>Name</label>
        <input id={nameId} className="input" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} placeholder="Nightly reconciliation" autoComplete="off" />
        <div className="help">What it is used for, so you can recognise it later.</div>
      </div>
      <fieldset className="rcgroup">
        <legend>What the key may do</legend>
        {scopes.map((s) => (
          <label key={s.scope} className="row check" style={{ alignItems: 'flex-start' }}>
            <input type="checkbox" checked={chosen.includes(s.scope)} onChange={() => toggle(s.scope)} />
            <span><b className="mono">{s.scope}</b><span className="help block" style={{ marginTop: 0 }}>{s.description}</span></span>
          </label>
        ))}
      </fieldset>
      <div className="field">
        <label className="f" htmlFor={expiryId}>Expires</label>
        <select id={expiryId} className="select" value={expires} onChange={(e) => setExpires(e.target.value)}>
          {expiryChoices.map((d) => <option key={d} value={d}>after {d} days</option>)}
          <option value="never">never</option>
        </select>
      </div>
      <p className="help">Creating a key asks you to sign in again, because a key is a secret that opens your data.</p>
      {create.isError && <div className="banner red" role="alert"><div className="grow">{errorText(create.error)}</div></div>}
    </Modal>
  );
}
