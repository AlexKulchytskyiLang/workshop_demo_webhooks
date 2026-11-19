import { useState } from 'react';
import { useEndpointMutation } from '../../api/queries';
import { webhooksApi, type KeyInfo, type SigningMode } from '../../api/webhooks';
import { withStepUp } from '../../auth/stepUp';
import { es256Snippet, hmacSnippet, LANGUAGES, type Language } from '../../lib/snippets';
import { formatUtc, timeAgo, timeUntil } from '../../lib/format';
import { CodeBlock, CopyButton, RadioCards } from '../../ui/Display';
import { ErrorBanner, errorText, useToast } from '../../ui/Feedback';
import { Icon } from '../../ui/Icon';
import { Modal } from '../../ui/Modal';
import { useEndpointCtx } from './EndpointLayout';

const OVERLAPS = [
  { value: '24', title: '24 hours', description: 'Recommended. Both keys sign, so receivers that have not switched yet keep working.' },
  { value: '168', title: '7 days', description: 'For receivers that deploy slowly.' },
  { value: '0', title: 'Immediately', description: 'The old key stops at once. Receivers still using it will reject our requests.' },
] as const;

export function SecurityTab() {
  const { detail, canReveal } = useEndpointCtx();
  const e = detail.endpoint;

  return (
    <div className="stack">
      <div className="banner blue" role="note">
        <Icon name="shield" />
        <div className="grow"><b>Four independent layers.</b> They answer different questions, so you can mix them: <i>Is the channel private? · Who are we to you? · Can you prove the message is ours? · Must anyone in between be blind to it?</i></div>
      </div>

      <section className="card layer" aria-labelledby="l1">
        <div className="hd"><h2 id="l1">1 · Transport</h2><span className="pill green">Enforced</span></div>
        <div className="bd row sb">
          <div>HTTPS only, TLS 1.2 or newer, certificate validated. Redirects are never followed. Addresses inside private networks, loopback and the cloud metadata address are blocked.</div>
          <div className="muted sm" title="Planned">Mutual TLS: planned</div>
        </div>
      </section>

      <DeliveryAuth />

      <SigningLayer canReveal={canReveal} signing={e.signing} keys={detail.secrets} jwksUrl={e.jwksUrl} endpointId={e.id} />

      <section className="card layer" aria-labelledby="l4">
        <div className="hd"><div><h2 id="l4">4 · Payload encryption</h2><div className="muted sm">Must anyone between us and your server be blind to the body?</div></div><span className="pill gray">Off · planned</span></div>
        <div className="bd">
          <p style={{ margin: 0 }}>Encrypting the body with your public key (JWE) protects against TLS-terminating intermediaries such as CDNs, WAFs and gateway logs. It costs payload size, complicates debugging and moves key management onto you, so it is opt-in and not built yet. For most receivers the signature plus HTTPS is enough.</p>
        </div>
      </section>
    </div>
  );
}

// ---------------------------------------------------------------- layer 2

function DeliveryAuth() {
  const { detail, canWrite } = useEndpointCtx();
  const toast = useToast();
  const id = detail.endpoint.id;
  const [type, setType] = useState<'none' | 'header'>(detail.auth.type);
  const [headerName, setHeaderName] = useState(detail.auth.headerName ?? 'Authorization');
  const [value, setValue] = useState('');
  const save = useEndpointMutation(id, () => webhooksApi.setAuth(id, type === 'none' ? { type } : { type, headerName, value: value || undefined }));

  return (
    <section className="card layer" aria-labelledby="l2">
      <div className="hd"><div><h2 id="l2">2 · Delivery authentication</h2><div className="muted sm">How InvoiceHub proves its identity <b>to your server</b> on each request.</div></div></div>
      <div className="bd">
        <RadioCards<'none' | 'header' | 'oauth' | 'mtls'> legend="Delivery authentication" name="auth" value={type} columns={4} onChange={(v) => { if (v === 'none' || v === 'header') setType(v); }}
          options={[
            { value: 'none', title: 'None', description: 'Not recommended. Anyone who finds the URL can post.', disabled: !canWrite },
            { value: 'header', title: 'API key or bearer token', description: 'We send a static header you choose.', disabled: !canWrite },
            { value: 'oauth', title: 'OAuth 2.0 client credentials', description: 'We fetch and cache a token from your identity provider.', disabled: true, disabledReason: 'Planned.' },
            { value: 'mtls', title: 'Mutual TLS', description: 'We present a client certificate.', disabled: true, disabledReason: 'Planned.' },
          ]} />
        {type === 'none' && <div className="note red" style={{ marginTop: 12 }}>Signature verification still protects you, but your endpoint stays reachable by anyone. Pair it with an IP allow-list at least.</div>}
        {type === 'header' && (
          <div className="sub-panel">
            <div className="grid g2">
              <div className="field"><label className="f" htmlFor="auth-name">Header name</label><input id="auth-name" className="input mono" value={headerName} disabled={!canWrite} onChange={(e) => setHeaderName(e.target.value)} /></div>
              <div className="field">
                <label className="f" htmlFor="auth-value">Value</label>
                <input id="auth-value" className="input mono" type="password" autoComplete="new-password" value={value} disabled={!canWrite} onChange={(e) => setValue(e.target.value)} placeholder={detail.auth.hasValue ? '•••••••• stored, leave empty to keep' : 'Bearer … or the API key'} />
                <div className="help">Stored encrypted. It is never shown again, and it is masked in logs and test results.</div>
              </div>
            </div>
          </div>
        )}
        {canWrite && (
          <div className="row" style={{ marginTop: 12 }}>
            <button type="button" className="btn" disabled={save.isPending || (type === 'header' && headerName.trim() === '')} onClick={() => save.mutate(undefined, { onSuccess: () => { setValue(''); toast.show('Delivery authentication saved'); }, onError: (e) => toast.show(errorText(e)) })}>{save.isPending ? 'Saving…' : 'Save'}</button>
          </div>
        )}
        {save.isError && <ErrorBanner error={save.error} />}
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- layer 3

function SigningLayer({ endpointId, signing, keys, jwksUrl, canReveal }: {
  endpointId: string; signing: SigningMode; keys: KeyInfo[]; jwksUrl: string; canReveal: boolean;
}) {
  const [mode, setMode] = useState<SigningMode>(signing);
  const [dialog, setDialog] = useState<'reveal' | 'rotate' | 'apply' | null>(null);
  const current = keys.filter((k) => k.current);
  const hasHmac = signing !== 'es256';
  const hasEs = signing !== 'hmac';

  return (
    <section className="card layer" aria-labelledby="l3">
      <div className="hd"><div><h2 id="l3">3 · Message signing</h2><div className="muted sm">How <b>you</b> verify a message really came from us and was not altered.</div></div><span className="pill green">Required</span></div>
      <div className="bd">
        <RadioCards<SigningMode> legend="Signing algorithm" name="signing" value={mode} columns={3} onChange={setMode}
          options={[
            { value: 'hmac', title: <>HMAC-SHA256 <span className="tag">symmetric</span></>, description: 'One shared secret. Simple, fast, supported by every library.', disabled: !canReveal },
            { value: 'es256', title: <>ES256 <span className="tag">asymmetric</span></>, description: 'We sign with a private key. You verify with our public key. Nothing secret on your side.', disabled: !canReveal },
            { value: 'both', title: <>Both <span className="tag">migration</span></>, description: 'Two signatures per request. Switch with zero downtime.', disabled: !canReveal },
          ]} />
        {mode !== signing && (
          <div className="row" style={{ marginTop: 10 }}>
            <button type="button" className="btn pri" onClick={() => setDialog('apply')}>Change signing to {mode === 'both' ? 'HMAC + ES256' : mode === 'hmac' ? 'HMAC-SHA256' : 'ES256'}…</button>
            <span className="muted sm">Needs a fresh sign-in. {signing === 'hmac' && mode === 'es256' ? 'Tip: choose Both first so your receiver can switch without downtime.' : ''}</span>
          </div>
        )}

        {hasHmac && (
          <div className="sub-panel">
            <div className="field">
              <label className="f" htmlFor="secret-mask">Signing secret</label>
              <div className="row wrap">
                <span id="secret-mask" className="copy grow" style={{ fontSize: 13 }}>whsec_••••••••••••••••••••{current.find((k) => k.algorithm === 'hmac')?.keyId.slice(-4) ?? ''}</span>
                {canReveal && <button type="button" className="btn" onClick={() => setDialog('reveal')}><Icon name="eye" />Reveal</button>}
                {canReveal && <button type="button" className="btn" onClick={() => setDialog('rotate')}><Icon name="refresh" />Rotate…</button>}
              </div>
              <div className="help">Revealing needs a fresh sign-in and is logged. Rotation keeps the old secret valid for an overlap you choose, and we send <b>one signature per active key</b>, so nothing breaks.</div>
            </div>
          </div>
        )}

        {hasEs && (
          <div className="sub-panel">
            <div className="field">
              <label className="f" htmlFor="jwks-url">Public keys (JWKS)</label>
              <div className="row wrap"><span id="jwks-url" className="copy grow">{jwksUrl}</span><CopyButton text={jwksUrl} what="The JWKS address" /></div>
              <div className="help">The <span className="mono">webhook-key-id</span> header names the key used. Cache the key set and refresh it when a signature does not verify. The private key is never shown to anyone. {!hasHmac && 'This endpoint has no shared secret.'}</div>
            </div>
            {canReveal && <button type="button" className="btn" onClick={() => setDialog('rotate')}><Icon name="refresh" />Rotate keys…</button>}
          </div>
        )}

        <div className="table-wrap" style={{ marginTop: 12 }}>
          <table className="sch keytable">
            <caption className="sr-only">Signing keys of this endpoint</caption>
            <thead><tr><th scope="col">Key</th><th scope="col">Type</th><th scope="col">Created</th><th scope="col">Status</th></tr></thead>
            <tbody>
              {keys.map((k) => (
                <tr key={k.keyId}>
                  <td className="mono">{k.keyId}</td>
                  <td>{k.algorithm === 'hmac' ? 'HMAC secret' : 'ES256 key pair'}</td>
                  <td title={formatUtc(k.validFrom)}>{timeAgo(k.validFrom)}</td>
                  <td>{k.current
                    ? (k.validUntil ? <span className="pill amber" title={formatUtc(k.validUntil)}>Retires {timeUntil(k.validUntil)}</span> : <span className="pill green">Current</span>)
                    : <span className="pill gray">Retired</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <hr className="s" />
        <div><b>Replay protection</b> <span className="pill gray">Always on</span><div className="help">Every request has a unique <span className="mono">webhook-id</span> (the same across retries: use it to de-duplicate) and a <span className="mono">webhook-timestamp</span> (reject anything older than <b>5 minutes</b>). Both are inside the signature.</div></div>

        <Snippets signing={signing} jwksUrl={jwksUrl} />
      </div>

      <RevealDialog endpointId={endpointId} open={dialog === 'reveal'} onClose={() => setDialog(null)} />
      <RotateDialog endpointId={endpointId} open={dialog === 'rotate'} onClose={() => setDialog(null)} />
      <ApplyDialog endpointId={endpointId} from={signing} to={mode} open={dialog === 'apply'} onClose={() => setDialog(null)} />
    </section>
  );
}

function Snippets({ signing, jwksUrl }: { signing: SigningMode; jwksUrl: string }) {
  const [language, setLanguage] = useState<Language>('csharp');
  const esLanguage = language === 'curl' ? null : language;
  const showEs = signing !== 'hmac' && esLanguage !== null;
  const showHmac = signing !== 'es256' || language === 'curl';
  return (
    <div style={{ marginTop: 14 }}>
      <h3 style={{ marginBottom: 8 }}>Verify in your code</h3>
      <div role="tablist" aria-label="Language" className="tabs sm" style={{ marginBottom: 8 }}>
        {LANGUAGES.map((l) => (
          <button key={l.id} type="button" role="tab" id={`tab-${l.id}`} aria-selected={language === l.id} aria-controls="snippet-panel" className={`tabbtn${language === l.id ? ' on' : ''}`} onClick={() => setLanguage(l.id)}>{l.label}</button>
        ))}
      </div>
      <div role="tabpanel" id="snippet-panel" aria-labelledby={`tab-${language}`} className="stack">
        {showHmac && <CodeBlock label={`${LANGUAGES.find((l) => l.id === language)!.label} HMAC verification code`} code={hmacSnippet(language)} />}
        {showEs && esLanguage && <CodeBlock label={`${LANGUAGES.find((l) => l.id === language)!.label} ES256 verification code`} code={es256Snippet(esLanguage, jwksUrl)} />}
      </div>
      <p className="help">Reference code. Always read the <b>raw</b> request body and compare signatures in constant time.</p>
    </div>
  );
}

// ---------------------------------------------------------------- step-up dialogs

function RevealDialog({ endpointId, open, onClose }: { endpointId: string; open: boolean; onClose: () => void }) {
  const [secret, setSecret] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const close = () => { setSecret(null); setError(null); onClose(); };

  const reveal = async () => {
    setBusy(true);
    setError(null);
    try {
      setSecret((await withStepUp(() => webhooksApi.reveal(endpointId))).secret);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal open={open} title="Reveal the signing secret" onClose={close}
      footer={<><button type="button" className="btn" onClick={close}>{secret ? 'Hide and close' : 'Cancel'}</button>{!secret && <button type="button" className="btn pri" disabled={busy} onClick={() => void reveal()}>{busy ? 'Waiting for sign-in…' : 'Sign in again and reveal'}</button>}</>}>
      {!secret && <p style={{ marginTop: 0 }}>For your protection we ask you to sign in again before showing a secret. A small window opens for that. Revealing is logged.</p>}
      {secret && (
        <div aria-live="polite">
          <div className="secret-box" data-testid="revealed-secret" tabIndex={0} aria-label="Signing secret">{secret}</div>
          <div className="row" style={{ marginTop: 10 }}><CopyButton text={secret} what="The signing secret" /><span className="muted sm">Paste it into your server's configuration. It is not stored in this browser.</span></div>
        </div>
      )}
      {error !== null && <ErrorBanner error={error} />}
    </Modal>
  );
}

function RotateDialog({ endpointId, open, onClose }: { endpointId: string; open: boolean; onClose: () => void }) {
  const toast = useToast();
  const [overlap, setOverlap] = useState<'24' | '168' | '0'>('24');
  const [error, setError] = useState<unknown>(null);
  const rotate = useEndpointMutation(endpointId, () => withStepUp(() => webhooksApi.rotate(endpointId, Number(overlap))));
  const close = () => { setError(null); onClose(); };

  return (
    <Modal open={open} title="Rotate the signing keys" onClose={close}
      footer={<><button type="button" className="btn" onClick={close}>Cancel</button><button type="button" className="btn pri" disabled={rotate.isPending} onClick={() => rotate.mutate(undefined, { onSuccess: () => { toast.show('New keys are active'); close(); }, onError: setError })}>{rotate.isPending ? 'Waiting for sign-in…' : 'Rotate keys'}</button></>}>
      <p style={{ marginTop: 0 }}>We create a new key now and sign with it. How long should the <b>old</b> key keep signing next to it?</p>
      <RadioCards legend="How long the old key keeps working" name="overlap" value={overlap} onChange={setOverlap} options={OVERLAPS.map((o) => ({ ...o }))} />
      <p className="help">Needs a fresh sign-in. The change is logged.</p>
      {error !== null && <ErrorBanner error={error} />}
    </Modal>
  );
}

function ApplyDialog({ endpointId, from, to, open, onClose }: { endpointId: string; from: SigningMode; to: SigningMode; open: boolean; onClose: () => void }) {
  const toast = useToast();
  const [overlap, setOverlap] = useState<'24' | '168' | '0'>('24');
  const [error, setError] = useState<unknown>(null);
  const removing = from === 'both' || (from !== to && to !== 'both'); // an algorithm is being dropped
  const apply = useEndpointMutation(endpointId, () => withStepUp(() => webhooksApi.setSigning(endpointId, to, removing ? Number(overlap) : 24)));
  const close = () => { setError(null); onClose(); };

  return (
    <Modal open={open} title="Change the signing algorithm" onClose={close}
      footer={<><button type="button" className="btn" onClick={close}>Cancel</button><button type="button" className="btn pri" disabled={apply.isPending} onClick={() => apply.mutate(undefined, { onSuccess: () => { toast.show('Signing updated'); close(); }, onError: setError })}>{apply.isPending ? 'Waiting for sign-in…' : 'Change signing'}</button></>}>
      <p style={{ marginTop: 0 }}>
        {to === 'both' && 'Requests will carry two signatures, HMAC and ES256. Receivers verify whichever they support, so you can move them over one by one.'}
        {to === 'es256' && 'Requests will be signed with ES256 only. Receivers that verify with the shared secret will stop working unless they already verify with our public keys.'}
        {to === 'hmac' && 'Requests will be signed with the shared HMAC secret only. Receivers that verify with our public keys will stop working.'}
      </p>
      {removing && (
        <>
          <p>How long should the signature you are leaving keep being sent?</p>
          <RadioCards legend="How long the old signature keeps working" name="apply-overlap" value={overlap} onChange={setOverlap} options={OVERLAPS.map((o) => ({ ...o }))} />
        </>
      )}
      <p className="help">Needs a fresh sign-in. The change is logged.</p>
      {error !== null && <ErrorBanner error={error} />}
    </Modal>
  );
}
