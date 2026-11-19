const MASK = '••••••••';

/** Headers that only make sense for the original request: a copy cannot reuse a signature, and the key list belongs with it. */
const SKIP = new Set(['webhook-signature', 'webhook-key-id', 'user-agent', 'content-length', 'host']);

const quote = (value: string) => `'${value.replaceAll("'", `'\''`)}'`;

/**
 * The request as a cURL command a person can paste into a terminal to reproduce what we sent. Secrets stay masked (the log never has them),
 * and the signature is left out on purpose: a copied signature would be rejected as stale anyway, so the command says where to sign instead.
 */
export function toCurl(request: { method: string; url: string; headers: Record<string, string> | null; body: string | null }): string {
  const lines = [`curl -X ${request.method} ${quote(request.url)}`, `  -H ${quote('content-type: application/json')}`];
  for (const [name, value] of Object.entries(request.headers ?? {})) {
    if (SKIP.has(name.toLowerCase())) continue;
    lines.push(`  -H ${quote(`${name}: ${value === MASK ? '<your-secret>' : value}`)}`);
  }
  if (request.body) lines.push(`  -d ${quote(compact(request.body))}`);
  const command = lines.join(' \\n');
  return `# The signature header is not copied: sign your own test request, or use "Send test" in the Playground.\n${command}`;
}

function compact(body: string): string {
  try {
    return JSON.stringify(JSON.parse(body));
  } catch {
    return body;
  }
}
