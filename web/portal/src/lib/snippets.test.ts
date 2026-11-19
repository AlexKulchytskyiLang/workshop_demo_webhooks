// @vitest-environment node
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';
import { es256Snippet, hmacSnippet } from './snippets';

// The reference snippets we show tenants are EXECUTED here against real signatures, so "copy this" is known to work.
const nodeRequire = createRequire(import.meta.url);
const load = <T>(code: string, name: string): T => new Function('require', `${code}\nreturn ${name};`)(nodeRequire) as T;

const sign = (secret: string, id: string, ts: number, body: string) =>
  'v1,' + crypto.createHmac('sha256', Buffer.from(secret.replace(/^whsec_/, ''), 'base64')).update(`${id}.${ts}.${body}`).digest('base64');

describe('the HMAC verification snippet (Node)', () => {
  const secret = 'whsec_' + crypto.randomBytes(32).toString('base64');
  const verify = load<(body: string, headers: Record<string, string>, secret?: string) => boolean>(hmacSnippet('node', secret), 'verify');
  const now = () => Math.floor(Date.now() / 1000);
  const body = '{"type":"invoice.paid","data":{"amount":10}}';

  it('accepts a correctly signed, fresh request', () => {
    const ts = now();
    expect(verify(body, { 'webhook-id': 'evt_1', 'webhook-timestamp': String(ts), 'webhook-signature': sign(secret, 'evt_1', ts, body) })).toBe(true);
  });

  it('rejects a tampered body, a wrong secret and a different id', () => {
    const ts = now();
    const headers = { 'webhook-id': 'evt_1', 'webhook-timestamp': String(ts), 'webhook-signature': sign(secret, 'evt_1', ts, body) };
    expect(verify(body.replace('10', '9999'), headers)).toBe(false);
    expect(verify(body, { ...headers, 'webhook-signature': sign('whsec_' + crypto.randomBytes(32).toString('base64'), 'evt_1', ts, body) })).toBe(false);
    expect(verify(body, { ...headers, 'webhook-id': 'evt_2' })).toBe(false);
  });

  it('rejects a request older than five minutes (replay protection) and one with missing headers', () => {
    const ts = now() - 6 * 60;
    expect(verify(body, { 'webhook-id': 'evt_1', 'webhook-timestamp': String(ts), 'webhook-signature': sign(secret, 'evt_1', ts, body) })).toBe(false);
    expect(verify(body, {})).toBe(false);
  });

  it('accepts when ANY of several signatures matches, as during a key rotation', () => {
    const ts = now();
    const old = 'whsec_' + crypto.randomBytes(32).toString('base64');
    const header = `${sign(old, 'evt_1', ts, body)} ${sign(secret, 'evt_1', ts, body)}`;
    expect(verify(body, { 'webhook-id': 'evt_1', 'webhook-timestamp': String(ts), 'webhook-signature': header })).toBe(true);
  });
});

describe('the ES256 verification snippet (Node)', () => {
  const verify = load<(body: string, headers: Record<string, string>, jwks: { keys: object[] }) => boolean>(es256Snippet('node', 'https://example.test/jwks.json'), 'verifyEs256');
  const body = '{"type":"invoice.paid"}';
  const pair = () => crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const signEs = (privateKey: crypto.KeyObject, id: string, ts: number) =>
    'v1a,' + crypto.sign('sha256', Buffer.from(`${id}.${ts}.${body}`), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64');

  it('verifies with the published public key only', () => {
    const { privateKey, publicKey } = pair();
    const jwks = { keys: [{ ...publicKey.export({ format: 'jwk' }), kid: 'k_1', alg: 'ES256', use: 'sig' }] };
    const ts = Math.floor(Date.now() / 1000);
    const headers = { 'webhook-id': 'evt_1', 'webhook-timestamp': String(ts), 'webhook-signature': signEs(privateKey, 'evt_1', ts) };
    expect(verify(body, headers, jwks)).toBe(true);
    expect(verify(body.replace('paid', 'void'), headers, jwks)).toBe(false);

    const other = pair().publicKey.export({ format: 'jwk' });
    expect(verify(body, headers, { keys: [other] })).toBe(false);
  });

  it('still verifies when the header also carries an HMAC signature (migration)', () => {
    const { privateKey, publicKey } = pair();
    const ts = Math.floor(Date.now() / 1000);
    const header = `v1,AAAA ${signEs(privateKey, 'evt_1', ts)}`;
    expect(verify(body, { 'webhook-id': 'evt_1', 'webhook-timestamp': String(ts), 'webhook-signature': header }, { keys: [publicKey.export({ format: 'jwk' })] })).toBe(true);
  });
});

describe('snippets for the other languages', () => {
  it('carry the same rules: raw body, five minute tolerance, any-signature match, de-duplication', () => {
    for (const language of ['csharp', 'python'] as const) {
      const code = hmacSnippet(language);
      expect(code).toContain('300');
      expect(code.toLowerCase()).toContain('raw');
      expect(code.toLowerCase()).toContain('constant time');
      expect(code).toContain('webhook-id');
    }
    expect(hmacSnippet('curl')).toContain('openssl');
    expect(es256Snippet('python', 'https://x/jwks.json')).toContain('https://x/jwks.json');
  });
});
