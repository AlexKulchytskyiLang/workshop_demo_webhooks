/** "5 s", "2 min", "1.8 h", "3 d": a duration a person can read at a glance. */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, totalSeconds);
  if (s < 60) return `${Math.round(s)} s`;
  if (s < 3600) return `${trim(s / 60)} min`;
  if (s < 86_400) return `${trim(s / 3600)} h`;
  return `${trim(s / 86_400)} d`;
}

function trim(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

/** "3 min ago", "2 h ago", "yesterday"; the exact time belongs in a title attribute next to it. */
export function timeAgo(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return 'never';
  const seconds = Math.round((now.getTime() - new Date(iso).getTime()) / 1000);
  if (seconds < 0) return 'in the future';
  if (seconds < 45) return 'just now';
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86_400) return `${Math.round(seconds / 3600)} h ago`;
  return `${Math.round(seconds / 86_400)} d ago`;
}

/** "in 21 h", "in 3 min": how far away a moment in the future is. */
export function timeUntil(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return '';
  const seconds = Math.round((new Date(iso).getTime() - now.getTime()) / 1000);
  if (seconds <= 0) return 'now';
  if (seconds < 60) return 'in under a minute';
  if (seconds < 3600) return `in ${Math.round(seconds / 60)} min`;
  if (seconds < 86_400) return `in ${Math.round(seconds / 3600)} h`;
  return `in ${Math.round(seconds / 86_400)} d`;
}

export function formatUtc(iso: string | null | undefined): string {
  return iso ? new Date(iso).toISOString().replace('T', ' ').replace(/\.\d+Z$/, ' UTC') : '';
}

export function formatNumber(value: number): string {
  return value.toLocaleString('en-US');
}

/** Shortens a long address for a table cell without hiding what matters (scheme and host). */
export function shortUrl(url: string, max = 44): string {
  return url.length <= max ? url : `${url.slice(0, max - 1)}…`;
}

export const HEALTH_LABEL: Record<string, string> = {
  healthy: 'Active',
  degraded: 'Degraded',
  circuit_open: 'Circuit open',
  paused: 'Paused',
  auto_disabled: 'Auto-disabled',
  disabled: 'Disabled',
  pending_verification: 'Pending verification',
};

/** Colour class of a status pill: colour is never the only signal, the label is always shown too. */
export const HEALTH_COLOUR: Record<string, string> = {
  healthy: 'green',
  degraded: 'amber',
  circuit_open: 'amber',
  paused: 'gray',
  auto_disabled: 'red',
  disabled: 'red',
  pending_verification: 'blue',
};

export const ENVIRONMENT_LABEL: Record<string, string> = { production: 'Production', staging: 'Staging', development: 'Development' };
