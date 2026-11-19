import type { AuditLine } from '../api/advanced';

/** What each audit action means in words. Unknown actions (a newer server) fall back to the raw name rather than disappearing. */
const LABEL: Record<string, string> = {
  'endpoint.created': 'Created the endpoint',
  'endpoint.updated': 'Changed the endpoint',
  'endpoint.deleted': 'Deleted the endpoint',
  'endpoint.paused': 'Paused the endpoint',
  'endpoint.resumed': 'Resumed the endpoint',
  'endpoint.verified': 'Verified the endpoint',
  'endpoint.disabled': 'Endpoint was switched off',
  'endpoint.events_changed': 'Changed the events and filters',
  'endpoint.delivery_changed': 'Changed delivery and retry settings',
  'endpoint.headers_changed': 'Changed the custom headers',
  'endpoint.auth_changed': 'Changed the delivery credential',
  'endpoint.signing_changed': 'Changed the signing method',
  'secret.rotated': 'Rotated the signing keys',
  'secret.revealed': 'Revealed the signing secret',
  'replay.started': 'Started a replay job',
  'replay.single': 'Replayed one event',
  'replay.cancelled': 'Cancelled a replay job',
  'apikey.created': 'Created an API key',
  'apikey.revoked': 'Revoked an API key',
  'alerts.changed': 'Changed an alert rule',
  'team.member_added': 'Added a team member',
  'team.role_changed': 'Changed a role',
  'team.member_disabled': 'Disabled a team member',
  'team.member_enabled': 'Re-enabled a team member',
};

export const auditLabel = (action: string): string => LABEL[action] ?? action;

/** The filter choices of the audit screen: a prefix per area. */
export const AUDIT_AREAS: { value: string; label: string }[] = [
  { value: '', label: 'Everything' },
  { value: 'endpoint.*', label: 'Endpoints' },
  { value: 'secret.*', label: 'Secrets and keys' },
  { value: 'replay.*', label: 'Replay' },
  { value: 'apikey.*', label: 'API keys' },
  { value: 'alerts.*', label: 'Alerts' },
  { value: 'team.*', label: 'Team and roles' },
];

/** A short, readable summary of the details a line carries (never secrets: the server only records names and safe values). */
export function auditSummary(line: AuditLine): string {
  const d = line.details;
  if (!d) return '';
  const parts: string[] = [];
  if (Array.isArray(d.changed)) parts.push(`changed: ${(d.changed as string[]).join(', ') || 'nothing visible'}`);
  if (typeof d.mode === 'string') parts.push(`mode: ${d.mode}`);
  if (typeof d.role === 'string') parts.push(`role: ${d.role}`);
  if (typeof d.from === 'string' && typeof d.to === 'string') parts.push(`${d.from} → ${d.to}`);
  if (typeof d.count === 'number') parts.push(`${d.count} events`);
  if (typeof d.released === 'number') parts.push(`${d.released} released`);
  if (typeof d.discarded === 'number' && d.discarded > 0) parts.push(`${d.discarded} discarded`);
  if (typeof d.reason === 'string') parts.push(`reason: ${d.reason}`);
  if (Array.isArray(d.scopes)) parts.push(`scopes: ${(d.scopes as string[]).join(', ')}`);
  if (typeof d.keyId === 'string') parts.push(`key ${d.keyId}`);
  if (typeof d.urlHost === 'string') parts.push(d.urlHost);
  return parts.join(' · ');
}
