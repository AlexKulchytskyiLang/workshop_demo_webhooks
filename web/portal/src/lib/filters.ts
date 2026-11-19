import type { FilterCondition, FilterDoc } from '../api/webhooks';

/**
 * The filter builder's state and its translation to what the API stores. A filter is an OR of groups, each group an AND of conditions
 * (UX-SPEC D4: "AND with one OR level, up to 10 conditions"). The server evaluates it on the canonical event, so what the builder shows is what runs.
 */
export interface FilterRow {
  path: string;
  op: string;
  value: string;
}
export interface FilterState {
  groups: FilterRow[][];
}

export const MAX_CONDITIONS = 10;

export const OPERATORS: { id: string; label: string; hint: string }[] = [
  { id: 'eq', label: 'is', hint: 'equals' },
  { id: 'ne', label: 'is not', hint: 'does not equal' },
  { id: 'gt', label: '>', hint: 'greater than' },
  { id: 'gte', label: '>=', hint: 'greater than or equal' },
  { id: 'lt', label: '<', hint: 'less than' },
  { id: 'lte', label: '<=', hint: 'less than or equal' },
  { id: 'in', label: 'is one of', hint: 'comma separated' },
  { id: 'nin', label: 'is none of', hint: 'comma separated' },
  { id: 'contains', label: 'contains', hint: 'text' },
  { id: 'startsWith', label: 'starts with', hint: 'text' },
  { id: 'exists', label: 'exists', hint: 'the field is present' },
];

const NUMERIC_OPS = new Set(['gt', 'gte', 'lt', 'lte']);
const LIST_OPS = new Set(['in', 'nin']);
const NUMBER = /^-?\d+(\.\d+)?$/;

export const emptyFilter = (): FilterState => ({ groups: [] });

export function parseScalar(text: string): string | number | boolean {
  const t = text.trim();
  if (NUMBER.test(t)) return Number(t);
  if (t === 'true') return true;
  if (t === 'false') return false;
  return t;
}

function parseValue(op: string, text: string): unknown {
  if (op === 'exists') return text.trim() !== 'false';
  if (LIST_OPS.has(op)) return text.split(',').map((x) => x.trim()).filter((x) => x !== '').map(parseScalar);
  if (op === 'contains' || op === 'startsWith') return text.trim();
  return parseScalar(text);
}

export function rowError(row: FilterRow): string | null {
  if (!/^[A-Za-z0-9_.-]{1,100}$/.test(row.path)) return 'Choose a field such as data.amount (letters, digits, dots).';
  if (row.op === 'exists') return null;
  if (row.value.trim() === '') return 'Enter a value.';
  if (NUMERIC_OPS.has(row.op) && !NUMBER.test(row.value.trim())) return 'This comparison needs a number.';
  if (LIST_OPS.has(row.op) && (parseValue(row.op, row.value) as unknown[]).length === 0) return 'Enter at least one value, separated by commas.';
  return null;
}

export const countConditions = (state: FilterState): number => state.groups.reduce((n, g) => n + g.length, 0);

export function stateErrors(state: FilterState): string[] {
  const errors: string[] = [];
  if (countConditions(state) > MAX_CONDITIONS) errors.push(`At most ${MAX_CONDITIONS} conditions are allowed.`);
  state.groups.forEach((group, g) => group.forEach((row, r) => {
    const e = rowError(row);
    if (e) errors.push(`Condition ${state.groups.slice(0, g).reduce((n, x) => n + x.length, 0) + r + 1}: ${e}`);
  }));
  return errors;
}

/** The document the API stores. No conditions means "deliver every event of this type" and is stored as nothing at all. */
export function toDoc(state: FilterState): FilterDoc | null {
  const groups = state.groups
    .map((group) => group.filter((row) => rowError(row) === null).map((row): FilterCondition => (
      row.op === 'exists' ? { path: row.path, op: 'exists', value: parseValue('exists', row.value) } : { path: row.path, op: row.op, value: parseValue(row.op, row.value) }
    )))
    .filter((group) => group.length > 0);
  return groups.length === 0 ? null : { groups };
}

export function fromDoc(doc: FilterDoc | null | undefined): FilterState {
  if (!doc) return emptyFilter();
  return {
    groups: doc.groups.map((group) => group.map((c) => ({
      path: c.path,
      op: c.op,
      value: Array.isArray(c.value) ? c.value.join(', ') : c.value === undefined || c.value === null ? '' : String(c.value),
    }))),
  };
}

/** Fields worth suggesting for an event type. Anything is allowed: the list only saves typing and typos. */
export function fieldSuggestions(eventType: string): string[] {
  const common = ['type', 'aggregate_id', 'sequence'];
  if (eventType.startsWith('invoice.')) return [...common, 'data.amount', 'data.currency', 'data.status', 'data.number', 'data.customer.country', 'data.customer.name'];
  if (eventType.startsWith('payment.')) return [...common, 'data.amount', 'data.currency', 'data.method', 'data.failure_reason'];
  if (eventType.startsWith('subscription.')) return [...common, 'data.plan', 'data.amount', 'data.currency'];
  if (eventType.startsWith('catalog.')) return [...common, 'data.vendor_id', 'data.source', 'data.rejected'];
  return [...common, 'data.status'];
}
