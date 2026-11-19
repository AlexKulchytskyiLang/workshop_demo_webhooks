export interface DiffLine {
  path: string;
  kind: 'added' | 'removed' | 'changed';
  before?: unknown;
  after?: unknown;
}

function leaves(value: unknown, prefix: string, out: Map<string, unknown>): Map<string, unknown> {
  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) leaves(child, prefix === '' ? key : `${prefix}.${key}`, out);
  } else {
    out.set(prefix, value);
  }
  return out;
}

/**
 * What differs between two shapes of the same event, field by field (nested objects are flattened to dotted paths). A field that moved shows up as
 * removed at the old path and added at the new one, which is exactly what a receiver's parsing code has to deal with.
 */
export function diffJson(before: unknown, after: unknown): DiffLine[] {
  const a = leaves(before, '', new Map());
  const b = leaves(after, '', new Map());
  const lines: DiffLine[] = [];
  for (const [path, value] of a) {
    if (!b.has(path)) lines.push({ path, kind: 'removed', before: value });
    else if (JSON.stringify(b.get(path)) !== JSON.stringify(value)) lines.push({ path, kind: 'changed', before: value, after: b.get(path) });
  }
  for (const [path, value] of b) if (!a.has(path)) lines.push({ path, kind: 'added', after: value });
  return lines.sort((x, y) => x.path.localeCompare(y.path));
}
