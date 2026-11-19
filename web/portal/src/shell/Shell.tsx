import { useAuth } from 'react-oidc-context';
import { Link, NavLink, Outlet, useLocation, useOutletContext } from 'react-router-dom';
import { MeProvider } from '../auth/me';
import { clearSelectedTenant } from '../auth/selectedTenant';
import type { Me } from '../api/tenancy';
import { NotificationBell } from './NotificationBell';
import { ThemeToggle } from './ThemeToggle';

const ROLE_COLOUR: Record<string, string> = { owner: 'purple', admin: 'blue', developer: 'green', support: 'amber', viewer: 'gray' };

/** Navigation of the signed-in portal. Items for screens that are not built yet are visible but disabled, so the target shape is clear. */
const NAV: { group?: string; items: { to?: string; label: string }[] }[] = [
  { items: [{ to: '/', label: 'Overview' }, { to: '/logs', label: 'Delivery logs' }, { to: '/playground', label: 'Playground' }, { to: '/replay', label: 'Replay center' }] },
  { group: 'Settings', items: [{ to: '/settings/alerts', label: 'Alerts' }, { to: '/settings/api-keys', label: 'API keys & IPs' }, { to: '/settings/audit', label: 'Audit log' }, { to: '/settings/team', label: 'Team & roles' }] },
];

export function Shell() {
  const auth = useAuth();
  const me = useOutletContext<Me>();
  const { pathname } = useLocation();

  const signOut = () => {
    clearSelectedTenant();
    void auth.signoutRedirect();
  };

  return (
    <div className="app">
      <a className="skip-link" href="#main">Skip to content</a>
      <header className="topbar">
        <div className="brand">
          <span className="logo" aria-hidden="true">↗</span>
          InvoiceHub <span className="muted">Webhooks</span>
        </div>
        <div className="top-right">
          <span className="tenant" title={`Organisation address: ${me.tenant.slug}`}>
            <span className="muted">Organisation</span> <b>{me.tenant.name}</b>
          </span>
          <NotificationBell />
          <ThemeToggle />
          <Link to="/account" className="who" title="Your access">
            <span className="avatar" aria-hidden="true">{initials(me.user.name)}</span>
            <span className="who-text">
              <span>{me.user.name}</span>
              <span className={`pill ${ROLE_COLOUR[me.user.role] ?? 'gray'}`}>{me.user.role}</span>
            </span>
            <span className="sr-only">, your access</span>
          </Link>
          <button type="button" className="btn sm" onClick={signOut}>Sign out</button>
        </div>
      </header>

      <nav className="sidenav" aria-label="Main">
        {NAV.map((section) => (
          <div key={section.group ?? 'main'} className="navgrp">
            {section.group && <div className="grp">{section.group}</div>}
            {section.items.map((item) =>
              item.to ? (
                <NavLink key={item.label} to={item.to} end className={({ isActive }) => (isActive || (item.to === '/' && pathname.startsWith('/endpoints')) ? 'on' : undefined)}>
                  {item.label}
                </NavLink>
              ) : (
                <a key={item.label} className="todo" aria-disabled="true" title="Built in a later milestone" tabIndex={-1}>
                  {item.label}
                </a>
              ),
            )}
          </div>
        ))}
      </nav>

      <main id="main">
        <MeProvider me={me}>
          <Outlet context={me} />
        </MeProvider>
      </main>
    </div>
  );
}

function initials(name: string): string {
  const parts = name.split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '?') + (parts.length > 1 ? (parts[parts.length - 1]?.[0] ?? '') : '')).toUpperCase();
}
