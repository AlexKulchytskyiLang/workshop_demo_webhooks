import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const auth = vi.hoisted(() => ({
  isLoading: false,
  isAuthenticated: true,
  signoutRedirect: vi.fn(async () => undefined),
  removeUser: vi.fn(async () => undefined),
}));
vi.mock('react-oidc-context', () => ({ useAuth: () => auth }));
vi.mock('../auth/oidc', () => ({ userManager: { getUser: vi.fn(async () => ({ expired: false, access_token: 't' })) } }));

import { getSelectedTenant, setSelectedTenant } from '../auth/selectedTenant';
import { RequireSession } from './RequireSession';

const ME = {
  user: { id: 'u1', email: 'owner@contoso.test', name: 'Olena Kovalenko', role: 'owner', roles: ['owner'] },
  tenant: { id: 't1', slug: 'contoso', name: 'Contoso Ltd' },
  permissions: ['endpoints:read'],
};

function renderGuard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<RequireSession />}><Route index element={<p>PRIVATE AREA</p>} /></Route>
          <Route path="/select-tenant" element={<p>PICK A TENANT</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const problem = (status: number, body: unknown) => vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

describe('RequireSession', () => {
  beforeEach(() => {
    auth.isLoading = false;
    auth.isAuthenticated = true;
    auth.signoutRedirect.mockClear();
    auth.removeUser.mockClear();
  });

  it('sends an anonymous visitor to the tenant picker', async () => {
    auth.isAuthenticated = false;
    vi.stubGlobal('fetch', problem(200, ME));
    renderGuard();
    expect(await screen.findByText('PICK A TENANT')).toBeInTheDocument();
  });

  it('shows the private area once the API confirms the organisation', async () => {
    setSelectedTenant('contoso');
    vi.stubGlobal('fetch', problem(200, ME));
    renderGuard();
    expect(await screen.findByText('PRIVATE AREA')).toBeInTheDocument();
  });

  it('stops with a clear message when the account belongs to another organisation, and signs out on request', async () => {
    setSelectedTenant('fabrikam');
    vi.stubGlobal('fetch', problem(403, { code: 'tenant_mismatch', detail: "You selected 'fabrikam', but this account belongs to 'Contoso Ltd'." }));
    renderGuard();

    expect(await screen.findByRole('heading', { name: /not your organisation/i })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent("belongs to 'Contoso Ltd'");
    expect(screen.queryByText('PRIVATE AREA')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /sign out and choose again/i }));
    expect(auth.signoutRedirect).toHaveBeenCalledTimes(1);
    expect(getSelectedTenant()).toBeNull();
  });

  it('drops the session when the API rejects the token', async () => {
    vi.stubGlobal('fetch', problem(401, { title: 'Unauthorized' }));
    renderGuard();
    await vi.waitFor(() => expect(auth.removeUser).toHaveBeenCalled());
  });

  it('offers a retry when the API is down, without losing the session', async () => {
    vi.stubGlobal('fetch', problem(503, { title: 'Service unavailable' }));
    renderGuard();
    // two retries with back-off (1 s, 2 s) happen before the error screen is shown
    expect(await screen.findByRole('button', { name: /try again/i }, { timeout: 6000 })).toBeInTheDocument();
    expect(screen.queryByText('PRIVATE AREA')).not.toBeInTheDocument();
  }, 10_000);
});
