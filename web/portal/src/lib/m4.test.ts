import { describe, expect, it } from 'vitest';
import type { AuditLine } from '../api/advanced';
import { AUDIT_AREAS, auditLabel, auditSummary } from './audit';
import { toCurl } from './curl';

describe('toCurl', () => {
  const request = {
    method: 'POST',
    url: 'https://erp.example.com/hooks/invoicehub',
    headers: { 'webhook-id': 'evt_1', 'webhook-timestamp': '1700000000', 'webhook-signature': 'v1,abcdefghijkl...', 'webhook-key-id': 'k_1', 'X-Api-Key': '••••••••', 'X-Region': 'eu', 'User-Agent': 'InvoiceHub-Webhooks/1.0' },
    body: '{\n  "id": "evt_1",\n  "data": { "note": "it\'s fine" }\n}',
  };

  it('reproduces the request without the signature and without any secret', () => {
    const command = toCurl(request);
    expect(command).toContain("curl -X POST 'https://erp.example.com/hooks/invoicehub'");
    expect(command).toContain("-H 'webhook-id: evt_1'");
    expect(command).toContain("-H 'X-Region: eu'");
    expect(command).not.toContain('webhook-signature');
    expect(command).not.toContain('webhook-key-id');
    expect(command).not.toContain('User-Agent');
    expect(command).toContain("-H 'X-Api-Key: <your-secret>'"); // a masked value becomes a placeholder, never a bullet string
    expect(command).not.toContain('••••');
  });

  it('puts the body on one line and survives a single quote inside it', () => {
    const command = toCurl(request);
    expect(command).toContain(`-d '{"id":"evt_1","data":{"note":"it'\''s fine"}}'`);
  });

  it('says why the signature is missing', () => {
    expect(toCurl(request).split('\n')[0]).toMatch(/signature header is not copied/i);
  });

  it('copes with no headers and no body', () => {
    expect(toCurl({ method: 'POST', url: 'https://x.test', headers: null, body: null })).not.toContain('-d ');
  });
});

describe('audit wording', () => {
  const line = (action: string, details: Record<string, unknown> | null): AuditLine => ({
    id: 1, at: '2026-11-13T10:00:00Z', actor: 'a@b.test', actorRole: 'owner', action, targetType: 'endpoint', targetId: 'x', targetName: 'ERP', ip: null, traceId: null, details,
  });

  it('knows every action the server writes and falls back to the raw name for a newer one', () => {
    expect(auditLabel('secret.revealed')).toBe('Revealed the signing secret');
    expect(auditLabel('something.new')).toBe('something.new');
  });

  it('summarises details in words and never invents a value', () => {
    expect(auditSummary(line('endpoint.updated', { changed: ['name', 'url'], urlHost: 'erp.example.com' }))).toBe('changed: name, url · erp.example.com');
    expect(auditSummary(line('team.role_changed', { from: 'support', to: 'developer' }))).toBe('support → developer');
    expect(auditSummary(line('endpoint.resumed', { mode: 'discard', released: 0, discarded: 2 }))).toBe('mode: discard · 0 released · 2 discarded');
    expect(auditSummary(line('x', null))).toBe('');
  });

  it('offers a filter for each area, starting with everything', () => {
    expect(AUDIT_AREAS[0]).toEqual({ value: '', label: 'Everything' });
    expect(AUDIT_AREAS.map((a) => a.value).filter(Boolean).every((v) => v.endsWith('.*'))).toBe(true);
  });
});

describe('diffJson', () => {
  it('shows a field that moved as removed at the old path and added at the new one', async () => {
    const { diffJson } = await import('./jsonDiff');
    const lines = diffJson({ amount: 10, currency: 'USD', customer: { name: 'A' } }, { total: { amount: 10, currency: 'USD' }, customer: { display_name: 'A' } });
    expect(lines.map((l) => `${l.kind} ${l.path}`)).toEqual([
      'removed amount', 'removed currency', 'removed customer.name', 'added customer.display_name', 'added total.amount', 'added total.currency',
    ].sort((a, b) => a.split(' ')[1]!.localeCompare(b.split(' ')[1]!)));
  });

  it('reports changed values and nothing when the shapes are equal', async () => {
    const { diffJson } = await import('./jsonDiff');
    expect(diffJson({ a: 1, b: { c: 'x' } }, { a: 1, b: { c: 'x' } })).toEqual([]);
    expect(diffJson({ a: 1 }, { a: 2 })).toEqual([{ path: 'a', kind: 'changed', before: 1, after: 2 }]);
  });

  it('treats arrays as values, not as structure', async () => {
    const { diffJson } = await import('./jsonDiff');
    expect(diffJson({ tags: [1, 2] }, { tags: [1, 3] })).toEqual([{ path: 'tags', kind: 'changed', before: [1, 2], after: [1, 3] }]);
  });
});
