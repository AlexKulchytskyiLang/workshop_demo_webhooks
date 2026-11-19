import { api } from './client';

export interface TenantSummary {
  slug: string;
  name: string;
}

export interface RegistrationRequest {
  tenantName: string;
  slug: string;
  adminEmail: string;
  adminFirstName: string;
  adminLastName: string;
  password: string;
}

export interface RegistrationResponse {
  tenantId: string;
  slug: string;
  name: string;
}

export interface Me {
  user: { id: string | null; email: string | null; name: string; role: string; roles: string[] };
  tenant: { id: string; slug: string; name: string };
  permissions: string[];
}

export const tenancyApi = {
  listTenants: (signal?: AbortSignal) => api<TenantSummary[]>('/api/public/tenants', { anonymous: true, signal }),
  register: (body: RegistrationRequest) => api<RegistrationResponse>('/api/public/registrations', { method: 'POST', body, anonymous: true }),
  me: (signal?: AbortSignal) => api<Me>('/api/me', { signal }),
};
