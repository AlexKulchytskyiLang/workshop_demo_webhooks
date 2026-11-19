import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useAuth } from 'react-oidc-context';
import { Link, Navigate } from 'react-router-dom';
import { tenancyApi } from '../api/tenancy';
import { getLastTenant, getSelectedTenant, setSelectedTenant } from '../auth/selectedTenant';
import { config } from '../config';
import { AuthLayout } from '../shell/AuthLayout';

const DEMO_ACCOUNTS = [
  { tenant: 'Contoso Ltd', users: 'owner@contoso.test · admin@ · developer@ · support@ · viewer@' },
  { tenant: 'Fabrikam Inc', users: 'owner@fabrikam.test · admin@ · developer@ · support@ · viewer@' },
];

/** Step one of signing in: choose the organisation, then enter credentials on the identity provider's page. */
export function SelectTenant() {
  const auth = useAuth();
  const tenants = useQuery({ queryKey: ['tenants'], queryFn: ({ signal }) => tenancyApi.listTenants(signal), retry: 2 });
  const [selected, setSelected] = useState<string | null>(() => getSelectedTenant() ?? getLastTenant());
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);

  // forget a remembered tenant that no longer exists
  useEffect(() => {
    if (tenants.data && selected && !tenants.data.some((t) => t.slug === selected)) setSelected(null);
  }, [tenants.data, selected]);

  if (auth.isAuthenticated) return <Navigate to="/" replace />;

  async function continueToSignIn() {
    if (!selected) return;
    setStarting(true);
    setStartError(null);
    try {
      setSelectedTenant(selected, tenants.data?.find((t) => t.slug === selected)?.name);
      await auth.signinRedirect();
    } catch {
      setStarting(false);
      setStartError('The sign-in service could not be reached. Is Keycloak running? Try again in a moment.');
    }
  }

  return (
    <AuthLayout title="Sign in to InvoiceHub" subtitle="Webhook management for your organisation">
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void continueToSignIn();
        }}
        aria-describedby="tenant-help"
      >
        <fieldset className="rcgroup" disabled={starting} aria-busy={tenants.isPending}>
          <legend>1. Choose your organisation</legend>

          {tenants.isPending && <p className="help" role="status">Loading organisations…</p>}

          {tenants.isError && (
            <div className="banner red" role="alert">
              <div className="grow">
                <b>The organisation list could not be loaded.</b>
                <div className="help">{tenants.error.message}</div>
              </div>
              <button type="button" className="btn sm" onClick={() => void tenants.refetch()}>Try again</button>
            </div>
          )}

          {tenants.data?.length === 0 && (
            <p className="help">No organisation exists yet. <Link to="/register">Register the first one</Link>.</p>
          )}

          {tenants.data?.map((t) => (
            <label key={t.slug} className={`rc${selected === t.slug ? ' on' : ''}`}>
              <input type="radio" name="tenant" value={t.slug} checked={selected === t.slug} onChange={() => setSelected(t.slug)} />
              <span>
                <b>{t.name}</b>
                <span className="help block">{t.slug}</span>
              </span>
            </label>
          ))}
        </fieldset>

        <p id="tenant-help" className="help">
          Your account belongs to exactly one organisation. If you pick the wrong one, sign-in works but the portal will refuse access.
        </p>

        {startError && <div className="banner red" role="alert"><div className="grow">{startError}</div></div>}

        <button type="submit" className="btn pri wide" disabled={!selected || starting}>
          {starting ? 'Opening the sign-in page…' : '2. Continue to sign in'}
        </button>

        <p className="help center">
          New here? <Link to="/register">Register your organisation</Link>
        </p>
      </form>

      {config.showDemoHints && (
        <details className="demo-hint">
          <summary>Demo accounts</summary>
          <p className="help">
            Password for every demo account: <code>Passw0rd!2026</code>. Roles: owner, admin, developer, support, viewer.
          </p>
          <ul>
            {DEMO_ACCOUNTS.map((a) => (
              <li key={a.tenant}><b>{a.tenant}</b>: <span className="mono">{a.users}</span></li>
            ))}
          </ul>
        </details>
      )}
    </AuthLayout>
  );
}
