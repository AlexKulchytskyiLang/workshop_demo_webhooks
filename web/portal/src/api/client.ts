import { getSelectedTenant } from '../auth/selectedTenant';
import { userManager } from '../auth/oidc';
import { config } from '../config';

/** A problem response from the API (RFC 9457) with its stable machine-readable code. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string | undefined,
    message: string,
    readonly errors?: Record<string, string[]>,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

interface ApiOptions {
  method?: string;
  body?: unknown;
  /** Public endpoints (tenant list, registration) are called without a token. */
  anonymous?: boolean;
  signal?: AbortSignal;
}

export async function api<T>(path: string, options: ApiOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';

  if (!options.anonymous) {
    const user = await userManager.getUser();
    if (user && !user.expired) headers.Authorization = `Bearer ${user.access_token}`;
    const selected = getSelectedTenant();
    if (selected) headers['X-Selected-Tenant'] = selected;
  }

  let response: Response;
  try {
    response = await fetch(`${config.apiUrl}${path}`, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause;
    throw new ApiError(0, 'network_error', 'The platform could not be reached. Check your connection and try again.');
  }

  if (response.ok) return (response.status === 204 ? undefined : await response.json()) as T;

  const problem = (await response.json().catch(() => ({}))) as {
    code?: string;
    detail?: string;
    title?: string;
    errors?: Record<string, string[]>;
  };
  throw new ApiError(response.status, problem.code, problem.detail ?? problem.title ?? `Request failed (${response.status})`, problem.errors);
}
