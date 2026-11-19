import type { Catalog, CatalogCategory, CatalogEvent } from '../api/webhooks';

export type TriState = 'none' | 'part' | 'all';

export function categoryState(category: CatalogCategory, selected: ReadonlySet<string>): TriState {
  const n = category.events.filter((e) => selected.has(e.type)).length;
  return n === 0 ? 'none' : n === category.events.length ? 'all' : 'part';
}

/** A category checkbox selects everything in it, or clears everything if it was already complete. */
export function toggleCategory(category: CatalogCategory, selected: ReadonlySet<string>): Set<string> {
  const next = new Set(selected);
  const complete = categoryState(category, selected) === 'all';
  for (const e of category.events) {
    if (complete) next.delete(e.type);
    else next.add(e.type);
  }
  return next;
}

export function toggleEvent(type: string, selected: ReadonlySet<string>): Set<string> {
  const next = new Set(selected);
  if (next.has(type)) next.delete(type);
  else next.add(type);
  return next;
}

export const bundleActive = (names: readonly string[], selected: ReadonlySet<string>) => names.length > 0 && names.every((n) => selected.has(n));

export function toggleBundle(names: readonly string[], selected: ReadonlySet<string>): Set<string> {
  const next = new Set(selected);
  const active = bundleActive(names, selected);
  for (const n of names) {
    if (active) next.delete(n);
    else next.add(n);
  }
  return next;
}

export function matchesSearch(event: CatalogEvent, query: string): boolean {
  const q = query.trim().toLowerCase();
  return q === '' || event.type.toLowerCase().includes(q) || event.description.toLowerCase().includes(q);
}

export const allEventTypes = (catalog: Catalog): string[] => catalog.categories.flatMap((c) => c.events.map((e) => e.type));

/** Selected events that carry personal data: they can only be sent as thin payloads (ids only). */
export function personalDataEvents(catalog: Catalog, selected: ReadonlySet<string>): string[] {
  return catalog.categories.flatMap((c) => c.events).filter((e) => e.containsPii && selected.has(e.type)).map((e) => e.type);
}

/** Why the "Snapshot" payload style is not available, or null when it is. */
export function snapshotBlockedReason(catalog: Catalog, selected: ReadonlySet<string>): string | null {
  const pii = personalDataEvents(catalog, selected);
  if (pii.length === 0) return null;
  return pii.length === 1
    ? `${pii[0]} carries personal data, so it is always sent as a thin payload (ids only).`
    : `${pii[0]} and ${pii.length - 1} more carry personal data, so they are always sent as thin payloads (ids only).`;
}

/** Drops anything that is not in the catalogue (a stale template or saved selection must never reach the API). */
export function onlyKnown(catalog: Catalog, names: readonly string[]): string[] {
  const known = new Set(allEventTypes(catalog));
  return names.filter((n) => known.has(n));
}
