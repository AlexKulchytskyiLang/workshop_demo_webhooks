import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const auth = vi.hoisted(() => ({ isAuthenticated: false, signinRedirect: vi.fn(async () => undefined) }));
vi.mock('react-oidc-context', () => ({ useAuth: () => auth }));
vi.mock('../auth/oidc', () => ({ userManager: { getUser: vi.fn(async () => null) } }));

import { getSelectedTenant, setSelectedTenant } from '../auth/selectedTenant';
import { SelectTenant } from './SelectTenant';

const TENANTS = [{ slug: 'contoso', name: 'Contoso Ltd' }, { slug: 'fabrikam', name: 'Fabrikam Inc' }];

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><SelectTenant /></MemoryRouter>
    </QueryClientProvider>,
  );
}

const respond = (body: unknown, status = 200) => vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

describe('SelectTenant', () => {
  beforeEach(() => {
    auth.isAuthenticated = false;
    auth.signinRedirect.mockClear();
  });

  it('lists the organisations and keeps Continue disabled until one is chosen', async () => {
    vi.stubGlobal('fetch', respond(TENANTS));
    renderPage();
    expect(await screen.findByRole('radio', { name: /Contoso Ltd/ })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: /Fabrikam Inc/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /continue to sign in/i })).toBeDisabled();
  });

  it('remembers the tenant the user chose last time', async () => {
    setSelectedTenant('fabrikam');
    vi.stubGlobal('fetch', respond(TENANTS));
    renderPage();
    expect(await screen.findByRole('radio', { name: /Fabrikam Inc/ })).toBeChecked();
    expect(screen.getByRole('button', { name: /continue to sign in/i })).toBeEnabled();
  });

  it('stores the chosen tenant and starts the sign-in redirect', async () => {
    vi.stubGlobal('fetch', respond(TENANTS));
    renderPage();
    await userEvent.click(await screen.findByRole('radio', { name: /Contoso Ltd/ }));
    await userEvent.click(screen.getByRole('button', { name: /continue to sign in/i }));
    await waitFor(() => expect(auth.signinRedirect).toHaveBeenCalledTimes(1));
    expect(getSelectedTenant()).toBe('contoso');
  });

  it('ignores a remembered tenant that no longer exists', async () => {
    setSelectedTenant('gone');
    vi.stubGlobal('fetch', respond(TENANTS));
    renderPage();
    await screen.findByRole('radio', { name: /Contoso Ltd/ });
    await waitFor(() => expect(screen.getByRole('button', { name: /continue to sign in/i })).toBeDisabled());
  });

  it('shows a retryable error when the organisation list cannot be loaded', async () => {
    vi.stubGlobal('fetch', respond({ title: 'down' }, 503));
    renderPage();
    // the page retries twice with back-off (1 s, 2 s) before it gives up
    expect(await screen.findByRole('alert', {}, { timeout: 6000 })).toHaveTextContent(/could not be loaded/i);
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
  }, 10_000);

  it('offers registration when no organisation exists yet', async () => {
    vi.stubGlobal('fetch', respond([]));
    renderPage();
    expect(await screen.findByRole('link', { name: /register the first one/i })).toHaveAttribute('href', '/register');
  });

  it('explains when the sign-in service cannot be reached', async () => {
    vi.stubGlobal('fetch', respond(TENANTS));
    auth.signinRedirect.mockRejectedValueOnce(new Error('network'));
    renderPage();
    await userEvent.click(await screen.findByRole('radio', { name: /Contoso Ltd/ }));
    await userEvent.click(screen.getByRole('button', { name: /continue to sign in/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Is Keycloak running/i);
  });
});
