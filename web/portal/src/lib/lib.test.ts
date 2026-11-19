import { describe, expect, it } from 'vitest';
import type { Catalog } from '../api/webhooks';
import { allEventTypes, bundleActive, categoryState, matchesSearch, onlyKnown, personalDataEvents, snapshotBlockedReason, toggleBundle, toggleCategory, toggleEvent } from './events';
import { countConditions, emptyFilter, fieldSuggestions, fromDoc, MAX_CONDITIONS, parseScalar, rowError, stateErrors, toDoc } from './filters';
import { formatDuration, shortUrl, timeAgo } from './format';

const catalog: Catalog = {
  categories: [
    { name: 'Invoices', pii: false, events: [
      { type: 'invoice.generated', description: 'A new invoice was issued', payload: 'fat', containsPii: false, referenceKeys: [] },
      { type: 'invoice.paid', description: 'Invoice fully paid', payload: 'fat', containsPii: false, referenceKeys: [] },
    ] },
    { name: 'Compliance', pii: true, events: [
      { type: 'kyc.completed', description: 'Identity verification finished', payload: 'thin', containsPii: true, referenceKeys: ['verification_id'] },
      { type: 'kyc.review_required', description: 'Needs a manual decision', payload: 'thin', containsPii: true, referenceKeys: ['verification_id'] },
    ] },
  ],
  bundles: { finance: ['invoice.generated', 'invoice.paid'] },
  templates: {},
};

describe('event picker rules', () => {
  const [invoices, compliance] = catalog.categories;

  it('shows a category as none, part or all selected', () => {
    expect(categoryState(invoices!, new Set())).toBe('none');
    expect(categoryState(invoices!, new Set(['invoice.paid']))).toBe('part');
    expect(categoryState(invoices!, new Set(['invoice.paid', 'invoice.generated']))).toBe('all');
  });

  it('a category checkbox completes a partial selection and clears a complete one', () => {
    const completed = toggleCategory(invoices!, new Set(['invoice.paid']));
    expect([...completed].sort()).toEqual(['invoice.generated', 'invoice.paid']);
    expect(toggleCategory(invoices!, completed).size).toBe(0);
  });

  it('never changes the set it was given', () => {
    const before = new Set(['invoice.paid']);
    toggleEvent('invoice.generated', before);
    toggleCategory(invoices!, before);
    expect([...before]).toEqual(['invoice.paid']);
  });

  it('bundles add all their events, and remove them again when already complete', () => {
    const names = catalog.bundles.finance!;
    expect(bundleActive(names, new Set())).toBe(false);
    const on = toggleBundle(names, new Set(['kyc.completed']));
    expect(bundleActive(names, on)).toBe(true);
    expect(on.has('kyc.completed')).toBe(true); // other choices stay
    expect(toggleBundle(names, on).has('invoice.paid')).toBe(false);
  });

  it('searches names and descriptions without caring about case', () => {
    expect(matchesSearch(invoices!.events[0]!, 'GENERATED')).toBe(true);
    expect(matchesSearch(invoices!.events[0]!, 'issued')).toBe(true);
    expect(matchesSearch(invoices!.events[0]!, 'kyc')).toBe(false);
    expect(matchesSearch(invoices!.events[0]!, '  ')).toBe(true);
  });

  it('explains why the snapshot style is unavailable when personal data events are chosen', () => {
    expect(snapshotBlockedReason(catalog, new Set(['invoice.paid']))).toBeNull();
    expect(personalDataEvents(catalog, new Set(['kyc.completed', 'invoice.paid']))).toEqual(['kyc.completed']);
    expect(snapshotBlockedReason(catalog, new Set(['kyc.completed']))).toContain('kyc.completed carries personal data');
    expect(snapshotBlockedReason(catalog, new Set(['kyc.completed', 'kyc.review_required']))).toContain('and 1 more');
    expect(compliance!.pii).toBe(true);
  });

  it('keeps only events the catalogue knows', () => {
    expect(allEventTypes(catalog)).toHaveLength(4);
    expect(onlyKnown(catalog, ['invoice.paid', 'nope.nothing'])).toEqual(['invoice.paid']);
  });
});

describe('filter builder', () => {
  it('turns an empty builder into "no filter" so every event of the type is delivered', () => {
    expect(toDoc(emptyFilter())).toBeNull();
    expect(toDoc({ groups: [[]] })).toBeNull();
  });

  it('types the values the way the engine compares them', () => {
    expect(parseScalar('5000')).toBe(5000);
    expect(parseScalar('-2.5')).toBe(-2.5);
    expect(parseScalar(' EUR ')).toBe('EUR');
    expect(parseScalar('true')).toBe(true);
    expect(parseScalar('007x')).toBe('007x');

    const doc = toDoc({ groups: [[
      { path: 'data.amount', op: 'gte', value: '5000' },
      { path: 'data.currency', op: 'in', value: 'USD, EUR' },
      { path: 'data.customer.name', op: 'contains', value: ' Fabrikam ' },
      { path: 'data.paid_at', op: 'exists', value: '' },
    ]] });
    expect(doc).toEqual({ groups: [[
      { path: 'data.amount', op: 'gte', value: 5000 },
      { path: 'data.currency', op: 'in', value: ['USD', 'EUR'] },
      { path: 'data.customer.name', op: 'contains', value: 'Fabrikam' },
      { path: 'data.paid_at', op: 'exists', value: true },
    ]] });
  });

  it('round-trips what the API returns back into the form', () => {
    const state = fromDoc({ groups: [[{ path: 'data.currency', op: 'in', value: ['USD', 'EUR'] }, { path: 'data.amount', op: 'gt', value: 100 }]] });
    expect(state.groups[0]).toEqual([{ path: 'data.currency', op: 'in', value: 'USD, EUR' }, { path: 'data.amount', op: 'gt', value: '100' }]);
    expect(toDoc(state)).toEqual({ groups: [[{ path: 'data.currency', op: 'in', value: ['USD', 'EUR'] }, { path: 'data.amount', op: 'gt', value: 100 }]] });
    expect(fromDoc(null)).toEqual({ groups: [] });
  });

  it('explains each invalid row before anything is saved', () => {
    expect(rowError({ path: '', op: 'eq', value: '1' })).toContain('Choose a field');
    expect(rowError({ path: 'data.x; drop', op: 'eq', value: '1' })).toContain('Choose a field');
    expect(rowError({ path: 'data.amount', op: 'gt', value: 'lots' })).toContain('number');
    expect(rowError({ path: 'data.amount', op: 'eq', value: '' })).toContain('Enter a value');
    expect(rowError({ path: 'data.currency', op: 'in', value: ' , ' })).toContain('at least one');
    expect(rowError({ path: 'data.paid_at', op: 'exists', value: '' })).toBeNull();
    expect(rowError({ path: 'data.amount', op: 'gte', value: '10' })).toBeNull();
  });

  it('numbers the problems and limits the size like the server does', () => {
    const rows = Array.from({ length: MAX_CONDITIONS + 1 }, () => ({ path: 'data.a', op: 'eq', value: '1' }));
    const errors = stateErrors({ groups: [rows] });
    expect(errors[0]).toContain(`At most ${MAX_CONDITIONS}`);
    expect(countConditions({ groups: [rows, rows] })).toBe(22);

    const two = stateErrors({ groups: [[{ path: 'data.a', op: 'eq', value: '1' }], [{ path: 'data.b', op: 'gt', value: 'x' }]] });
    expect(two).toEqual(['Condition 2: This comparison needs a number.']);
  });

  it('only offers fields that make sense for the event family, plus the generic ones', () => {
    expect(fieldSuggestions('invoice.paid')).toContain('data.amount');
    expect(fieldSuggestions('payment.failed')).toContain('data.failure_reason');
    expect(fieldSuggestions('kyc.completed')).toContain('aggregate_id');
    expect(fieldSuggestions('kyc.completed')).not.toContain('data.amount');
  });
});

describe('formatting', () => {
  it('formats durations like the prototype did', () => {
    expect(formatDuration(5)).toBe('5 s');
    expect(formatDuration(30)).toBe('30 s');
    expect(formatDuration(120)).toBe('2 min');
    expect(formatDuration(6480)).toBe('1.8 h');
    expect(formatDuration(259_200)).toBe('3 d');
    expect(formatDuration(-4)).toBe('0 s');
  });

  it('says how long ago something happened', () => {
    const now = new Date('2026-11-12T12:00:00Z');
    expect(timeAgo(null, now)).toBe('never');
    expect(timeAgo('2026-11-12T11:59:40Z', now)).toBe('just now');
    expect(timeAgo('2026-11-12T11:30:00Z', now)).toBe('30 min ago');
    expect(timeAgo('2026-11-12T08:00:00Z', now)).toBe('4 h ago');
    expect(timeAgo('2026-11-09T12:00:00Z', now)).toBe('3 d ago');
  });

  it('shortens long addresses', () => {
    expect(shortUrl('https://a.example/x', 44)).toBe('https://a.example/x');
    expect(shortUrl(`https://a.example/${'x'.repeat(60)}`, 20)).toHaveLength(20);
  });
});
