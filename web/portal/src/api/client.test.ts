import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../auth/oidc', () => ({
  userManager: { getUser: vi.fn(async () => ({ expired: false, access_token: 'tok-123' })) },
}));

import { ApiError, api } from './client';
import { setSelectedTenant } from '../auth/selectedTenant';

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

describe('api client', () => {
  const fetchMock = vi.fn();
  beforeEach(() => vi.stubGlobal('fetch', fetchMock));
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('sends the bearer token and the selected tenant on authenticated calls', async () => {
    setSelectedTenant('contoso');
    fetchMock.mockResolvedValue(json(200, { ok: true }));
    await api('/api/me');
    const init = fetchMock.mock.calls[0]![1] as RequestInit;
    expect(init.headers).toMatchObject({ Authorization: 'Bearer tok-123', 'X-Selected-Tenant': 'contoso' });
  });

  it('sends nothing identifying on anonymous calls', async () => {
    setSelectedTenant('contoso');
    fetchMock.mockResolvedValue(json(200, []));
    await api('/api/public/tenants', { anonymous: true });
    const headers = (fetchMock.mock.calls[0]![1] as RequestInit).headers as Record<string, string>;
    expect(headers.Authorization).toBeUndefined();
    expect(headers['X-Selected-Tenant']).toBeUndefined();
  });

  it('turns a problem response into an ApiError with its stable code', async () => {
    fetchMock.mockResolvedValue(json(403, { code: 'tenant_mismatch', detail: "You selected 'fabrikam', but this account belongs to 'Contoso Ltd'." }));
    await expect(api('/api/me')).rejects.toMatchObject({ name: 'ApiError', status: 403, code: 'tenant_mismatch', message: expect.stringContaining('Contoso Ltd') });
  });

  it('keeps the field errors of a validation problem', async () => {
    fetchMock.mockResolvedValue(json(400, { code: 'validation_failed', title: 'One or more validation errors occurred.', errors: { slug: ['Reserved.'] } }));
    const error = await api('/api/public/registrations', { method: 'POST', body: {}, anonymous: true }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).errors).toEqual({ slug: ['Reserved.'] });
  });

  it('reports an unreachable platform as a friendly network error', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));
    await expect(api('/api/me')).rejects.toMatchObject({ status: 0, code: 'network_error' });
  });

  it('survives a non-JSON error body', async () => {
    fetchMock.mockResolvedValue(new Response('<html>bad gateway</html>', { status: 502 }));
    await expect(api('/api/me')).rejects.toMatchObject({ status: 502 });
  });

  it('serialises the body and sets the content type', async () => {
    fetchMock.mockResolvedValue(json(201, {}));
    await api('/api/public/registrations', { method: 'POST', body: { a: 1 }, anonymous: true });
    const init = fetchMock.mock.calls[0]![1] as RequestInit;
    expect(init.body).toBe('{"a":1}');
    expect(init.headers).toMatchObject({ 'Content-Type': 'application/json' });
  });
});
