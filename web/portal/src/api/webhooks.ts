import { api } from './client';

export type Environment = 'production' | 'staging' | 'development';
export type PayloadStyle = 'fat' | 'thin' | 'auto';
export type SigningMode = 'hmac' | 'es256' | 'both';
export type Health = 'healthy' | 'degraded' | 'circuit_open' | 'paused' | 'auto_disabled' | 'disabled' | 'pending_verification';

export interface EndpointSummary {
  id: string;
  name: string;
  url: string;
  description: string | null;
  environment: Environment;
  status: 'pending' | 'active' | 'paused' | 'disabled';
  health: Health;
  disabledReason: string | null;
  payloadStyle: PayloadStyle;
  apiVersion: string;
  signing: SigningMode;
  retryPreset: string;
  timeoutSeconds: number;
  maxInflight: number;
  verifiedAt: string | null;
  consecutiveFailures: number;
  circuitFailures: number;
  autoDisableFailures: number;
  circuitOpenUntil: string | null;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  subscriptions: number;
  backlog: { pending: number; retrying: number; held: number };
  jwksUrl: string;
  createdAt: string;
}

export interface EndpointListItem {
  summary: EndpointSummary;
  events: string[];
  successRate24h: number | null;
  attempts24h: number;
  series: (number | null)[];
}

export interface FilterCondition {
  path: string;
  op: string;
  value?: unknown;
}
export interface FilterDoc {
  groups: FilterCondition[][];
}

export interface RetryParameters {
  attempts: number;
  backoff: string;
  initialSeconds: number;
  capSeconds: number;
  windowSeconds: number;
}

export interface DeliverySettings {
  preset: string;
  custom: RetryParameters | null;
  circuitFailures: number;
  autoDisable: { failures: number; withinHours: number; days: number };
  holdDays: number;
  timeoutSeconds: number;
  maxInflight: number;
}

export interface HeaderView {
  name: string;
  value: string | null;
  secret: boolean;
  hasValue: boolean;
}

export interface KeyInfo {
  keyId: string;
  algorithm: 'hmac' | 'es256';
  validFrom: string;
  validUntil: string | null;
  current: boolean;
}

export interface EndpointDetail {
  endpoint: EndpointSummary;
  subscriptions: { eventType: string; filter: FilterDoc | null }[];
  headers: HeaderView[];
  auth: { type: 'none' | 'header'; headerName: string | null; hasValue: boolean };
  delivery: DeliverySettings;
  secrets: KeyInfo[];
}

export interface CatalogEvent {
  type: string;
  description: string;
  payload: string;
  containsPii: boolean;
  referenceKeys: string[];
}
export interface CatalogCategory {
  name: string;
  pii: boolean;
  events: CatalogEvent[];
}
export interface Catalog {
  categories: CatalogCategory[];
  bundles: Record<string, string[]>;
  templates: Record<string, { name?: string | null; description?: string | null; events: string[] }>;
}

export interface Overview {
  kpis: {
    deliveries24h: number;
    deliveriesPrevious24h: number;
    succeeded: number;
    successRate: number | null;
    firstAttemptRate: number | null;
    avgLatencyMs: number | null;
    p95LatencyMs: number | null;
    retrying: number;
    dead: number;
    held: number;
    oldestHeldSeconds: number | null;
  };
  attention: { kind: 'auto_disabled' | 'gone' | 'circuit_open' | 'key_expiring'; endpointId: string; name: string; held: number }[];
  endpointCounts: { total: number; healthy: number; needsAttention: number };
}

export interface UrlCheck {
  id: string;
  status: 'ok' | 'error' | 'warn' | 'todo';
  text: string;
}

export interface RetryPresetInfo {
  id: string;
  name: string;
  description: string;
  windowSeconds: number;
  attempts: number;
}
export interface RetryPresets {
  presets: RetryPresetInfo[];
  customDefault: RetryParameters;
  backoffs: string[];
  maxWindowSeconds: number;
}
export interface RetryPreview {
  valid: boolean;
  errors: string[];
  attempts: { number: number; delaySeconds: number; atSeconds: number }[];
  windowSeconds: number;
  maxAttempts: number | null;
  jitterPercent: number;
}

export interface PingResult {
  ok: boolean;
  statusCode: number | null;
  durationMs: number;
  timedOut: boolean;
  error: string | null;
  blockedBy: string | null;
  responseSnippet: string | null;
  requestId: string;
  hint: string | null;
}

export type Scenario = 'normal' | 'bad_signature' | 'old_timestamp' | 'duplicate';
export interface TestResult {
  ok: boolean;
  scenario: Scenario;
  statusCode: number | null;
  durationMs: number;
  error: string | null;
  blockedBy: string | null;
  responseSnippet: string | null;
  secondStatusCode: number | null;
  requestId: string;
  request: { url: string; headers: Record<string, string>; body: string };
  checks: { status: 'ok' | 'warn' | 'bad'; text: string }[];
}

export interface FilterTestResult {
  valid: boolean;
  error: string | null;
  expression: string | null;
  rows: { eventId: string; occurredAt: string; aggregateId: string; summary: Record<string, string>; matched: boolean }[];
  matched: number;
  total: number;
}

export interface ApiVersionInfo {
  version: string;
  status: 'older' | 'current' | 'preview';
  summary: string;
  sunsetOn: string | null;
  changes: string[];
  isDefault: boolean;
}

export interface EndpointInput {
  name: string;
  url: string;
  description?: string;
  environment?: Environment;
  events?: string[];
  payloadStyle?: PayloadStyle;
  apiVersion?: string;
  signing?: SigningMode;
}

const base = '/api/portal';
const enc = encodeURIComponent;

export const webhooksApi = {
  overview: (signal?: AbortSignal) => api<Overview>(`${base}/overview`, { signal }),
  listEndpoints: (signal?: AbortSignal) => api<{ items: EndpointListItem[] }>(`${base}/endpoints`, { signal }),
  getEndpoint: (id: string, signal?: AbortSignal) => api<EndpointDetail>(`${base}/endpoints/${id}`, { signal }),
  createEndpoint: (body: EndpointInput) => api<EndpointSummary>(`${base}/endpoints`, { method: 'POST', body }),
  updateEndpoint: (id: string, body: Partial<EndpointInput>) => api<EndpointSummary>(`${base}/endpoints/${id}`, { method: 'PUT', body }),
  deleteEndpoint: (id: string) => api<void>(`${base}/endpoints/${id}`, { method: 'DELETE' }),
  pause: (id: string) => api<void>(`${base}/endpoints/${id}/pause`, { method: 'POST' }),
  resume: (id: string) => api<void>(`${base}/endpoints/${id}/resume`, { method: 'POST' }),
  setSubscriptions: (id: string, subscriptions: { eventType: string; filter: FilterDoc | null }[]) =>
    api<{ count: number }>(`${base}/endpoints/${id}/subscriptions`, { method: 'PUT', body: { subscriptions } }),
  setHeaders: (id: string, headers: { name: string; value: string | null; secret: boolean }[]) =>
    api<{ headers: HeaderView[] }>(`${base}/endpoints/${id}/headers`, { method: 'PUT', body: { headers } }),
  setAuth: (id: string, body: { type: 'none' | 'header'; headerName?: string; value?: string }) =>
    api<EndpointDetail['auth']>(`${base}/endpoints/${id}/auth`, { method: 'PUT', body }),
  setDelivery: (id: string, body: Partial<Omit<DeliverySettings, 'custom'>> & { custom?: RetryParameters | null }) =>
    api<DeliverySettings>(`${base}/endpoints/${id}/delivery`, { method: 'PUT', body }),
  setSigning: (id: string, mode: SigningMode, overlapHours: number) =>
    api<{ signing: SigningMode; keys: KeyInfo[] }>(`${base}/endpoints/${id}/signing`, { method: 'PUT', body: { mode, overlapHours } }),
  rotate: (id: string, overlapHours: number) => api<{ keys: KeyInfo[] }>(`${base}/endpoints/${id}/secret/rotate`, { method: 'POST', body: { overlapHours } }),
  reveal: (id: string) => api<{ secret: string; keyId: string }>(`${base}/endpoints/${id}/secret/reveal`, { method: 'POST', body: {} }),
  ping: (id: string) => api<PingResult>(`${base}/endpoints/${id}/ping`, { method: 'POST' }),
  test: (id: string, eventType: string, scenario: Scenario, data?: unknown) => api<TestResult>(`${base}/endpoints/${id}/test`, { method: 'POST', body: { eventType, scenario, data } }),
  checkUrl: (url: string, endpointId?: string, signal?: AbortSignal) =>
    api<{ ok: boolean; checks: UrlCheck[] }>(`${base}/endpoints/check-url`, { method: 'POST', body: { url, endpointId }, signal }),
  filterTest: (eventType: string, filter: FilterDoc | null, signal?: AbortSignal) =>
    api<FilterTestResult>(`${base}/endpoints/filter-test`, { method: 'POST', body: { eventType, filter }, signal }),
  catalog: (signal?: AbortSignal) => api<Catalog>(`${base}/events/catalog`, { signal }),
  retryPresets: (signal?: AbortSignal) => api<RetryPresets>(`${base}/retry-presets`, { signal }),
  retryPreview: (preset: string, custom: RetryParameters | null, signal?: AbortSignal) =>
    api<RetryPreview>(`${base}/retry-preview`, { method: 'POST', body: { preset, custom }, signal }),
  egressPolicy: (signal?: AbortSignal) => api<{ strict: boolean; devAllow: string[]; notice: string | null }>(`${base}/egress-policy`, { signal }),
  apiVersions: (signal?: AbortSignal) => api<ApiVersionInfo[]>(`${base}/api-versions`, { signal }),
  enc,
};
