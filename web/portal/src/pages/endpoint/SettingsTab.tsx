import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useEndpointMutation } from '../../api/queries';
import { webhooksApi, type Environment } from '../../api/webhooks';
import { UrlChecks } from '../../features/UrlChecks';
import { formatNumber } from '../../lib/format';
import { ErrorBanner, errorText, useToast } from '../../ui/Feedback';
import { ConfirmTyped } from '../../ui/Modal';
import { useEndpointCtx } from './EndpointLayout';

export function SettingsTab() {
  const { detail, canWrite, canDelete } = useEndpointCtx();
  const e = detail.endpoint;
  const toast = useToast();
  const navigate = useNavigate();

  const [name, setName] = useState(e.name);
  const [description, setDescription] = useState(e.description ?? '');
  const [environment, setEnvironment] = useState<Environment>(e.environment);
  const [url, setUrl] = useState(e.url);
  const [urlOk, setUrlOk] = useState<boolean | null>(true);
  const [confirming, setConfirming] = useState(false);

  const save = useEndpointMutation(e.id, () => webhooksApi.updateEndpoint(e.id, { name: name.trim(), description: description.trim(), environment, url: url.trim() }));
  const remove = useEndpointMutation(e.id, () => webhooksApi.deleteEndpoint(e.id));
  const dirty = name !== e.name || description !== (e.description ?? '') || environment !== e.environment || url !== e.url;
  const waiting = e.backlog.pending + e.backlog.retrying + e.backlog.held;

  return (
    <div className="stack">
      <section className="card" aria-labelledby="set-h">
        <div className="hd"><h2 id="set-h">General</h2></div>
        <form className="bd" noValidate onSubmit={(ev) => { ev.preventDefault(); save.mutate(undefined, { onSuccess: () => toast.show('Settings saved'), onError: (err) => toast.show(errorText(err)) }); }}>
          <div className="grid g2">
            <div className="field"><label className="f" htmlFor="s-name">Name</label><input id="s-name" className="input" value={name} maxLength={120} disabled={!canWrite} onChange={(ev) => setName(ev.target.value)} /></div>
            <div className="field"><label className="f" htmlFor="s-env">Environment</label>
              <select id="s-env" className="select" value={environment} disabled={!canWrite} onChange={(ev) => setEnvironment(ev.target.value as Environment)}>
                <option value="production">Production</option><option value="staging">Staging</option><option value="development">Development</option>
              </select></div>
          </div>
          <div className="field"><label className="f" htmlFor="s-desc">Description</label><input id="s-desc" className="input" value={description} maxLength={400} disabled={!canWrite} onChange={(ev) => setDescription(ev.target.value)} /></div>
          <div className="field">
            <label className="f" htmlFor="s-url">Endpoint URL</label>
            <input id="s-url" className={`input mono${urlOk === false ? ' bad' : ''}`} value={url} disabled={!canWrite} onChange={(ev) => setUrl(ev.target.value)} aria-describedby="s-url-checks" />
            <div id="s-url-checks"><UrlChecks url={url} endpointId={e.id} onResult={setUrlOk} /></div>
            {url !== e.url && <div className="note warn" style={{ marginTop: 8 }}>A new address must be verified again before real events go to it. Events wait meanwhile, they are not lost.</div>}
          </div>
          {save.isError && <ErrorBanner error={save.error} />}
          {canWrite && <button type="submit" className="btn pri" disabled={!dirty || save.isPending || urlOk === false || name.trim() === ''}>{save.isPending ? 'Saving…' : 'Save settings'}</button>}
        </form>
      </section>

      {canDelete && (
        <section className="card" aria-labelledby="danger-h" style={{ borderColor: 'color-mix(in srgb, var(--red) 45%, var(--border))' }}>
          <div className="hd"><h2 id="danger-h">Danger zone</h2></div>
          <div className="bd row sb">
            <div><b>Delete this endpoint</b><div className="help">Stops all deliveries and invalidates its keys. Delivery history stays for audit.</div></div>
            <button type="button" className="btn dng" onClick={() => setConfirming(true)}>Delete endpoint…</button>
          </div>
        </section>
      )}

      {confirming && (
        <ConfirmTyped open title={`Delete “${e.name}”`} phrase={e.name} confirmLabel="Delete endpoint" busy={remove.isPending} error={remove.isError ? errorText(remove.error) : null}
          consequence={<>{waiting > 0 ? <><b>{formatNumber(waiting)} events</b> are waiting for this endpoint ({e.backlog.pending} pending, {e.backlog.retrying} retrying, {e.backlog.held} held) and will <b>not</b> be delivered.</> : 'No events are waiting for this endpoint.'} Its signing keys stop working at once.</>}
          onClose={() => setConfirming(false)}
          onConfirm={() => remove.mutate(undefined, { onSuccess: () => { toast.show(`“${e.name}” was deleted`); navigate('/'); } })} />
      )}
    </div>
  );
}
