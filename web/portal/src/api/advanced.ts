import { api } from './client';
import type { ApiVersionInfo, PingResult, Scenario, TestResult } from './webhooks';

export type { ApiVersionInfo };

/** The M4 screens' API: delivery logs, replay, alerts and the bell, API keys, audit and team. Same conventions as webhooks.ts. */

const base = '/api/portal';
const enc = encodeURIComponent;

function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const parts = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${enc(k)}=${enc(String(v))}`);
  return parts.length === 0 ? '' : `?${parts.join('&')}`;
}

// ------------------------------------------------------------------ delivery log

export type DeliveryStatus = 'pending' | 'in_flight' | 'succeeded' | 'retry_scheduled' | 'held' | 'dead';

export interface DeliveryItem {
  id: string;
  eventId: string;
  webhookId: string;
  endpointId: string;
  endpointName: string | null;
  eventType: string;
  aggregateId: string | null;
  status: DeliveryStatus;
  attemptCount: number;
  lastStatusCode: number | null;
  lastError: string | null;
  heldReason: string | null;
  replayed: boolean;
  nextAttemptAt: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface DeliveryCounts {
  succeeded: number;
  retrying: number;
  pending: number;
  held: number;
  dead: number;
  total: number;
}

export interface DeliveryList {
  items: DeliveryItem[];
  counts: DeliveryCounts | null;
  nextCursor: string | null;
}

export interface AttemptView {
  attemptNo: number;
  startedAt: string;
  durationMs: number;
  outcome: string;
  statusCode: number | null;
  error: string | null;
  traceId: string | null;
  responseSize: number | null;
  responseSnippet: string | null;
}

export interface DeliveryDetail {
  delivery: {
    id: string;
    eventId: string;
    webhookId: string;
    endpointId: string;
    eventType: string;
    aggregateId: string | null;
    status: DeliveryStatus;
    attemptCount: number;
    lastStatusCode: number | null;
    lastError: string | null;
    heldReason: string | null;
    replayCount: number;
    replayedAt: string | null;
    replayJobId: string | null;
    deadlineAt: string;
    nextAttemptAt: string | null;
    heldUntil: string | null;
    createdAt: string;
    completedAt: string | null;
  };
  endpoint: { id: string; name: string; url: string; status: string } | null;
  event: { id: string; type: string; aggregateId: string; sequence: number; occurredAt: string; containsPii: boolean; description: string | null } | null;
  request: { method: string; url: string; headers: Record<string, string> | null; body: string | null; bodyNote: string | null } | null;
  canViewPayload: boolean;
  attempts: AttemptView[];
}

export interface LogFilters {
  endpointId?: string;
  status?: string;
  eventType?: string;
  q?: string;
  from?: string;
  to?: string;
  replayed?: boolean;
}

export const logsApi = {
  list: (filters: LogFilters, cursor?: string | null, signal?: AbortSignal) =>
    api<DeliveryList>(`${base}/deliveries${query({ ...filters, cursor, limit: 25 })}`, { signal }),
  get: (id: string, signal?: AbortSignal) => api<DeliveryDetail>(`${base}/deliveries/${id}`, { signal }),
  replay: (id: string) => api<{ jobId: string; sameWebhookId: boolean }>(`${base}/deliveries/${id}/replay`, { method: 'POST' }),
};

// ------------------------------------------------------------------ replay center

export interface ReplayOverview {
  cap: { limit: number; used: number; remaining: number };
  ratePerSecond: number;
  testFreshMinutes: number;
  recentHours: number;
  endpoints: {
    id: string;
    name: string;
    status: string;
    disabledReason: string | null;
    health: string;
    held: number;
    dead: number;
    holdExpiresAt: string | null;
    oldestHeld: string | null;
    lastTestPassedAt: string | null;
    testFresh: boolean;
  }[];
}

export type ReplayState = 'dead' | 'held' | 'succeeded';

export interface ReplaySelection {
  endpointId: string;
  states: ReplayState[];
  eventType?: string;
  from?: string;
  to?: string;
  ratePerSecond?: number;
}

export interface ReplayPreview {
  count: number;
  byState: Record<string, number>;
  byType: { type: string; count: number }[];
  oldest: string | null;
  newest: string | null;
  ratePerSecond: number;
  estimatedSeconds: number;
  cap: { limit: number; used: number; remaining: number; exceeded: boolean };
  endpoint: { id: string; name: string; status: string; canReceive: boolean };
  confirmPhrase: string;
  sameWebhookId: boolean;
  note: string;
}

export interface ReplayJob {
  id: string;
  endpointId: string;
  endpointName: string;
  kind: 'resume' | 'replay' | 'single';
  mode: string;
  states: string;
  eventType: string | null;
  total: number;
  discarded: number;
  ratePerSecond: number;
  createdBy: string;
  createdAt: string;
  cancelledAt: string | null;
  status: 'running' | 'completed' | 'cancelled';
  remaining: number;
  delivered: number;
  failed: number;
}

export type ResumeMode = 'deliver_all' | 'only_recent' | 'discard';

export const replayApi = {
  overview: (signal?: AbortSignal) => api<ReplayOverview>(`${base}/replay/overview`, { signal }),
  preview: (selection: ReplaySelection, signal?: AbortSignal) => api<ReplayPreview>(`${base}/replay/preview`, { method: 'POST', body: selection, signal }),
  start: (selection: ReplaySelection, confirm: string) => api<{ jobId: string; released: number }>(`${base}/replay/jobs`, { method: 'POST', body: { ...selection, confirm } }),
  jobs: (signal?: AbortSignal) => api<{ items: ReplayJob[] }>(`${base}/replay/jobs`, { signal }),
  cancel: (id: string) => api<{ cancelled: boolean; reverted: number }>(`${base}/replay/jobs/${id}/cancel`, { method: 'POST' }),
  resume: (endpointId: string, mode: ResumeMode, recentHours?: number) =>
    api<{ status: string; released: number; discarded: number; jobId: string | null }>(`${base}/endpoints/${endpointId}/resume-guided`, { method: 'POST', body: { mode, recentHours } }),
};

// ------------------------------------------------------------------ playground

export interface EventSample {
  type: string;
  description: string;
  category: string;
  containsPii: boolean;
  referenceKeys: string[];
  payload: string;
  aggregateId: string;
  data: Record<string, unknown>;
  note: string | null;
}

export interface PlaygroundResult extends TestResult {
  request: TestResult['request'];
}

export interface VersionDiff {
  type: string;
  from: { version: string; data: Record<string, unknown>; changes: string[] };
  to: { version: string; data: Record<string, unknown>; changes: string[] };
  deliveriesKeepTheirVersion: boolean;
}

export const versionsApi = {
  diff: (from: string, to: string, type: string, signal?: AbortSignal) => api<VersionDiff>(`${base}/api-versions/diff${query({ from, to, type })}`, { signal }),
};

export const playgroundApi = {
  sample: (type: string, version?: string, signal?: AbortSignal) => api<EventSample>(`${base}/events/${enc(type)}/sample${query({ version })}`, { signal }),
  send: (endpointId: string, eventType: string, scenario: Scenario, data: unknown, apiVersion?: string) =>
    api<PlaygroundResult>(`${base}/endpoints/${endpointId}/test`, { method: 'POST', body: { eventType, scenario, data, apiVersion } }),
  ping: (endpointId: string) => api<PingResult>(`${base}/endpoints/${endpointId}/ping`, { method: 'POST' }),
};

// ------------------------------------------------------------------ alerts and the bell

export interface InboxItem {
  id: number;
  at: string;
  severity: 'info' | 'warning' | 'critical';
  title: string;
  body: string;
  endpointId: string | null;
  kind: string;
  read: boolean;
}

export interface AlertRule {
  trigger: string;
  label: string;
  description: string;
  enabled: boolean;
  inApp: boolean;
  email: boolean;
  roles: string[];
  isDefault: boolean;
  updatedBy: string | null;
  updatedAt: string | null;
}

export const alertsApi = {
  inbox: (signal?: AbortSignal) => api<{ items: InboxItem[]; unread: number }>(`${base}/notifications?limit=30`, { signal }),
  markRead: (id: number) => api<void>(`${base}/notifications/${id}/read`, { method: 'POST' }),
  readAll: () => api<void>(`${base}/notifications/read-all`, { method: 'POST' }),
  rules: (signal?: AbortSignal) => api<{ items: AlertRule[]; roles: string[] }>(`${base}/alerts`, { signal }),
  setRule: (trigger: string, rule: { enabled: boolean; inApp: boolean; email: boolean; roles: string[] }) =>
    api<void>(`${base}/alerts/${enc(trigger)}`, { method: 'PUT', body: rule }),
};

// ------------------------------------------------------------------ API keys and addresses

export interface ApiKeyRow {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  createdBy: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
  status: 'active' | 'revoked' | 'expired';
}

export interface ApiKeyList {
  items: ApiKeyRow[];
  scopes: { scope: string; description: string }[];
  expiryChoices: number[];
}

export interface CreatedKey {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  expiresAt: string | null;
  key: string;
  note: string;
}

export const keysApi = {
  list: (signal?: AbortSignal) => api<ApiKeyList>(`${base}/api-keys`, { signal }),
  create: (body: { name: string; scopes: string[]; expiresInDays?: number }) => api<CreatedKey>(`${base}/api-keys`, { method: 'POST', body }),
  revoke: (id: string) => api<void>(`${base}/api-keys/${id}`, { method: 'DELETE' }),
  outboundIps: (signal?: AbortSignal) => api<{ addresses: string[]; demo: boolean; note: string }>(`${base}/outbound-ips`, { signal }),
};

// ------------------------------------------------------------------ audit log

export interface AuditLine {
  id: number;
  at: string;
  actor: string;
  actorRole: string | null;
  action: string;
  targetType: string;
  targetId: string | null;
  targetName: string | null;
  ip: string | null;
  traceId: string | null;
  details: Record<string, unknown> | null;
}

export interface AuditFilters {
  action?: string;
  actor?: string;
  targetId?: string;
}

export const auditApi = {
  list: (filters: AuditFilters, cursor?: string | null, signal?: AbortSignal) =>
    api<{ items: AuditLine[]; nextCursor: string | null }>(`${base}/audit${query({ ...filters, cursor, limit: 40 })}`, { signal }),
};

// ------------------------------------------------------------------ team

export interface TeamMember {
  id: string;
  email: string;
  name: string;
  role: string;
  active: boolean;
  you: boolean;
  lastSeenAt: string | null;
  createdAt: string;
  disabledAt: string | null;
}

export interface TeamList {
  items: TeamMember[];
  roles: { role: string; permissions: string[] }[];
  canManageOwners: boolean;
}

export const teamApi = {
  list: (signal?: AbortSignal) => api<TeamList>(`${base}/team`, { signal }),
  invite: (body: { email: string; firstName: string; lastName: string; role: string }) =>
    api<{ id: string; email: string; name: string; role: string; temporaryPassword: string; note: string }>(`${base}/team`, { method: 'POST', body }),
  setRole: (id: string, role: string) => api<void>(`${base}/team/${id}/role`, { method: 'PUT', body: { role } }),
  disable: (id: string) => api<void>(`${base}/team/${id}/disable`, { method: 'POST' }),
  enable: (id: string) => api<void>(`${base}/team/${id}/enable`, { method: 'POST' }),
};
