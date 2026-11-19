// The tenant the user picked on the first screen. It travels with every API call as X-Selected-Tenant so the API can
// refuse an account that belongs to another tenant. The token, not this value, decides which data the user sees.
const SESSION_KEY = 'ih.selectedTenant';
const LAST_KEY = 'ih.lastTenant';

function read(store: Storage | undefined, key: string): string | null {
  try {
    return store?.getItem(key) ?? null;
  } catch {
    return null; // storage can be blocked (private windows, site data off)
  }
}

function write(store: Storage | undefined, key: string, value: string | null) {
  try {
    if (value === null) store?.removeItem(key);
    else store?.setItem(key, value);
  } catch {
    /* the page keeps working without persistence */
  }
}

export const getSelectedTenant = () => read(globalThis.sessionStorage, SESSION_KEY);

export const getLastTenant = () => read(globalThis.localStorage, LAST_KEY);

export function setSelectedTenant(slug: string, name?: string) {
  write(globalThis.sessionStorage, SESSION_KEY, slug);
  write(globalThis.localStorage, LAST_KEY, slug);
  if (name) announceTenantToIdentityProvider(slug, name);
}

/**
 * Demo technique: leave a short-lived, display-only cookie so the Keycloak login theme can say "Signing in to <name>".
 * Cookies are shared between ports of the same host (localhost:5173 and localhost:8085). The API never trusts it.
 */
function announceTenantToIdentityProvider(slug: string, name: string) {
  try {
    const value = encodeURIComponent(`${slug}|${name}|${globalThis.location.origin}`);
    document.cookie = `ih_tenant=${value}; path=/; max-age=900; SameSite=Lax`;
  } catch {
    /* cookies blocked: the sign-in page simply has no banner */
  }
}

export function clearSelectedTenant() {
  write(globalThis.sessionStorage, SESSION_KEY, null);
}
