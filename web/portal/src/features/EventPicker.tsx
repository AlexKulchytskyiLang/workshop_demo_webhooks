import { useEffect, useId, useRef, useState } from 'react';
import type { Catalog, CatalogCategory } from '../api/webhooks';
import { bundleActive, categoryState, matchesSearch, toggleBundle, toggleCategory, toggleEvent } from '../lib/events';
import { Icon } from '../ui/Icon';

interface Props {
  catalog: Catalog;
  selected: ReadonlySet<string>;
  onChange: (next: Set<string>) => void;
  /** How many filter conditions each selected event has (shown as a tag). */
  filterCounts?: Record<string, number>;
  /** Whether to offer the bundle shortcuts (Finance basics ...). */
  bundles?: boolean;
  initiallyOpen?: string[];
  disabled?: boolean;
}

const BUNDLE_LABEL: Record<string, string> = { finance: 'Finance basics', subs: 'Subscriptions', edi: 'Vendor & EDI', alerts: 'Platform alerts' };

/**
 * The 42 events in 7 categories as an accordion of real checkboxes: a category box selects or clears the whole category, bundles select
 * a sensible starter set, and the search matches names and descriptions. Everything is reachable and operable with the keyboard.
 */
export function EventPicker({ catalog, selected, onChange, filterCounts, bundles = true, initiallyOpen = [], disabled }: Props) {
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState<Set<string>>(new Set(initiallyOpen));
  const searchId = useId();
  const total = catalog.categories.reduce((n, c) => n + c.events.length, 0);
  const searching = query.trim() !== '';

  const toggleOpen = (name: string) => setOpen((o) => {
    const next = new Set(o);
    if (next.has(name)) next.delete(name);
    else next.add(name);
    return next;
  });

  return (
    <div className="picker">
      <div className="row wrap">
        <label className="sr-only" htmlFor={searchId}>Search event types</label>
        <input id={searchId} type="search" className="input" style={{ maxWidth: 320 }} placeholder={`Search ${total} event types…`} value={query} onChange={(e) => setQuery(e.target.value)} />
        {bundles && (
          <>
            <span className="muted sm">Bundles:</span>
            {Object.entries(catalog.bundles).map(([name, events]) => (
              <button
                key={name}
                type="button"
                className={`chip act${bundleActive(events, selected) ? ' on' : ''}`}
                aria-pressed={bundleActive(events, selected)}
                disabled={disabled}
                onClick={() => onChange(toggleBundle(events, selected))}
              >
                {BUNDLE_LABEL[name] ?? name}
              </button>
            ))}
          </>
        )}
        <span className="muted sm right" aria-live="polite">{selected.size} of {total} selected</span>
      </div>

      {catalog.categories.map((category) => {
        const events = category.events.filter((e) => matchesSearch(e, query));
        if (events.length === 0) return null;
        const isOpen = open.has(category.name) || searching;
        return (
          <Category
            key={category.name}
            category={category}
            events={events}
            isOpen={isOpen}
            selected={selected}
            filterCounts={filterCounts}
            disabled={disabled}
            onToggleOpen={() => toggleOpen(category.name)}
            onChange={onChange}
          />
        );
      })}
      {searching && catalog.categories.every((c) => c.events.every((e) => !matchesSearch(e, query))) && <p className="muted">No event matches “{query}”.</p>}
    </div>
  );
}

function Category({ category, events, isOpen, selected, filterCounts, disabled, onToggleOpen, onChange }: {
  category: CatalogCategory;
  events: CatalogCategory['events'];
  isOpen: boolean;
  selected: ReadonlySet<string>;
  filterCounts?: Record<string, number>;
  disabled?: boolean;
  onToggleOpen: () => void;
  onChange: (next: Set<string>) => void;
}) {
  const state = categoryState(category, selected);
  const boxRef = useRef<HTMLInputElement>(null);
  const bodyId = useId();
  useEffect(() => {
    if (boxRef.current) boxRef.current.indeterminate = state === 'part';
  }, [state]);
  const n = category.events.filter((e) => selected.has(e.type)).length;

  return (
    <section className={`acc${isOpen ? ' open' : ''}`}>
      <div className="h">
        <input ref={boxRef} type="checkbox" checked={state === 'all'} disabled={disabled} aria-label={`Select all ${category.name} events`} onChange={() => onChange(toggleCategory(category, selected))} />
        <button type="button" className="chevbtn" aria-expanded={isOpen} aria-controls={bodyId} onClick={onToggleOpen}>
          <Icon name="right" />
          <b>{category.name}</b>
        </button>
        {category.pii && <span className="tag pii">Thin only · personal data</span>}
        <span className="cnt">{n} of {category.events.length}</span>
      </div>
      <div className="b" id={bodyId}>
        {isOpen && events.map((e) => {
          const checked = selected.has(e.type);
          const filters = filterCounts?.[e.type] ?? 0;
          return (
            <label key={e.type} className={`ev${checked ? ' on' : ''}`}>
              <input type="checkbox" checked={checked} disabled={disabled} onChange={() => onChange(toggleEvent(e.type, selected))} />
              <span className="nm">{e.type}</span>
              <span className="ds">{e.description}</span>
              <span className="tags">
                {e.payload === 'thin' ? <span className="tag thin">Thin only</span> : e.payload === 'claim' ? <span className="tag claim">Claim-check</span> : <span className="tag fat">Snapshot</span>}
                {checked && filters > 0 && <span className="tag filters">{filters} filter{filters === 1 ? '' : 's'}</span>}
              </span>
            </label>
          );
        })}
      </div>
    </section>
  );
}
