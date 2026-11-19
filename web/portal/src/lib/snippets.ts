/**
 * Reference code a receiving team can paste into their service. The Node snippets are executed by the portal's own tests against real signatures,
 * so they are known to work; the C# snippet mirrors the platform's own verifier (also covered by its tests).
 * The signed content is always  {webhook-id}.{webhook-timestamp}.{raw body}  and the clock tolerance is 5 minutes.
 */
export type Language = 'csharp' | 'node' | 'python' | 'curl';

export const LANGUAGES: { id: Language; label: string }[] = [
  { id: 'csharp', label: 'C#' },
  { id: 'node', label: 'Node' },
  { id: 'python', label: 'Python' },
  { id: 'curl', label: 'cURL' },
];

export function hmacSnippet(language: Language, secretPlaceholder = 'whsec_YOUR_SIGNING_SECRET'): string {
  switch (language) {
    case 'csharp':
      return `// 1. read the RAW body bytes (never re-serialise the JSON: the signature covers the exact bytes)
string body = await new StreamReader(Request.Body).ReadToEndAsync();
string id = Request.Headers["webhook-id"], timestamp = Request.Headers["webhook-timestamp"], header = Request.Headers["webhook-signature"];

// 2. reject old or future timestamps: that is replay protection
if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) > 300) return Results.Unauthorized();

// 3. expected signature = HMAC-SHA256 over "{id}.{timestamp}.{body}" with the base64 part of the secret
var key = Convert.FromBase64String("${secretPlaceholder}"["whsec_".Length..]);
var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

// 4. accept if ANY signature in the header matches (there are several during a key rotation), compared in constant time
bool valid = header.Split(' ').Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
if (!valid) return Results.Unauthorized();

// 5. de-duplicate on webhook-id (the same id is sent on every retry), answer 2xx at once, process afterwards
return Results.Ok();`;

    case 'node':
      return `const crypto = require('node:crypto');

/** rawBody: the exact bytes received (a Buffer or string), never JSON.stringify(parsedBody). headers: lower-case header names. */
function verify(rawBody, headers, secret = '${secretPlaceholder}') {
  const id = headers['webhook-id'];
  const timestamp = headers['webhook-timestamp'];
  const received = (headers['webhook-signature'] || '').split(' ');

  // reject old or future timestamps (replay protection)
  if (!id || !timestamp || Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false;

  const key = Buffer.from(secret.replace(/^whsec_/, ''), 'base64');
  const expected = 'v1,' + crypto.createHmac('sha256', key).update(\`\${id}.\${timestamp}.\${rawBody}\`).digest('base64');

  // accept if ANY signature matches (several are sent during a key rotation), compared in constant time
  return received.some((s) => s.length === expected.length && crypto.timingSafeEqual(Buffer.from(s), Buffer.from(expected)));
}
// then: de-duplicate on webhook-id, answer 2xx at once, process afterwards`;

    case 'python':
      return `import base64, hashlib, hmac, time

def verify(raw_body: bytes, headers: dict, secret: str = "${secretPlaceholder}") -> bool:
    """raw_body: the exact bytes received (never json.dumps of a parsed body). headers: lower-case header names."""
    msg_id = headers.get("webhook-id")
    timestamp = headers.get("webhook-timestamp")
    received = headers.get("webhook-signature", "").split(" ")

    # reject old or future timestamps (replay protection)
    if not msg_id or not timestamp or abs(time.time() - int(timestamp)) > 300:
        return False

    key = base64.b64decode(secret.removeprefix("whsec_"))
    signed = f"{msg_id}.{timestamp}.".encode() + raw_body
    expected = "v1," + base64.b64encode(hmac.new(key, signed, hashlib.sha256).digest()).decode()

    # accept if ANY signature matches (several are sent during a key rotation), compared in constant time
    return any(hmac.compare_digest(s, expected) for s in received)
# then: de-duplicate on webhook-id, answer 2xx at once, process afterwards`;

    case 'curl':
      return `# Send yourself a signed request to try your endpoint locally (the same signature we compute).
SECRET="${secretPlaceholder}"
ID="evt_test_manual_1"; TS=$(date +%s)
BODY='{"id":"evt_test_manual_1","type":"invoice.paid","test":true,"data":{"invoice_id":"inv_1"}}'
KEY=$(printf '%s' "\${SECRET#whsec_}" | base64 -d | xxd -p -c 256)
SIG=$(printf '%s' "$ID.$TS.$BODY" | openssl dgst -sha256 -mac HMAC -macopt hexkey:$KEY -binary | base64)

curl -i -X POST https://your-server.example/hooks/invoicehub \\
  -H "content-type: application/json" \\
  -H "webhook-id: $ID" -H "webhook-timestamp: $TS" -H "webhook-signature: v1,$SIG" \\
  --data "$BODY"`;
  }
}

/** Verification with our published public keys (ES256). Nothing secret is needed on the receiving side. */
export function es256Snippet(language: Exclude<Language, 'curl'>, jwksUrl: string): string {
  switch (language) {
    case 'csharp':
      return `// Base64Url(s): decode base64url (replace '-' with '+' and '_' with '/', add '=' padding, then Convert.FromBase64String)
// Fetch and cache the public keys: ${jwksUrl}  (refresh when a signature does not verify: the key may have been rotated)
using var jwks = JsonDocument.Parse(await http.GetStringAsync("${jwksUrl}"));
var keys = jwks.RootElement.GetProperty("keys").EnumerateArray().Select(k => ECDsa.Create(new ECParameters
{
    Curve = ECCurve.NamedCurves.nistP256,
    Q = new ECPoint { X = Base64Url(k.GetProperty("x").GetString()!), Y = Base64Url(k.GetProperty("y").GetString()!) },
})).ToList();

// same signed content as HMAC: "{webhook-id}.{webhook-timestamp}.{raw body}"; signatures that start with "v1a," are ES256 (raw 64-byte r||s, base64)
var content = Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}");
bool valid = header.Split(' ').Where(s => s.StartsWith("v1a,"))
    .Select(s => Convert.FromBase64String(s[4..]))
    .Any(sig => keys.Any(k => k.VerifyData(content, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
// also check the 5-minute timestamp tolerance and de-duplicate on webhook-id, exactly as with HMAC`;

    case 'node':
      return `const crypto = require('node:crypto');

/** jwks: the parsed JSON from ${jwksUrl}. Cache it and refresh when a signature does not verify (keys rotate). */
function verifyEs256(rawBody, headers, jwks) {
  const id = headers['webhook-id'];
  const timestamp = headers['webhook-timestamp'];
  if (!id || !timestamp || Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false; // replay protection

  const content = Buffer.from(\`\${id}.\${timestamp}.\${rawBody}\`);
  const signatures = (headers['webhook-signature'] || '').split(' ').filter((s) => s.startsWith('v1a,')).map((s) => Buffer.from(s.slice(4), 'base64'));
  return jwks.keys.some((jwk) => {
    const key = crypto.createPublicKey({ key: jwk, format: 'jwk' });
    return signatures.some((sig) => crypto.verify('sha256', content, { key, dsaEncoding: 'ieee-p1363' }, sig));
  });
}`;

    case 'python':
      return `import base64, json, time, urllib.request
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, utils

JWKS_URL = "${jwksUrl}"  # cache it, and refresh when a signature does not verify (keys rotate)

def b64url(value: str) -> int:
    return int.from_bytes(base64.urlsafe_b64decode(value + "=" * (-len(value) % 4)), "big")

def verify_es256(raw_body: bytes, headers: dict) -> bool:
    msg_id, timestamp = headers.get("webhook-id"), headers.get("webhook-timestamp")
    if not msg_id or not timestamp or abs(time.time() - int(timestamp)) > 300:  # replay protection
        return False
    content = f"{msg_id}.{timestamp}.".encode() + raw_body
    signatures = [base64.b64decode(s[4:]) for s in headers.get("webhook-signature", "").split(" ") if s.startswith("v1a,")]
    for jwk in json.load(urllib.request.urlopen(JWKS_URL))["keys"]:
        key = ec.EllipticCurvePublicNumbers(b64url(jwk["x"]), b64url(jwk["y"]), ec.SECP256R1()).public_key()
        for sig in signatures:
            r, s = int.from_bytes(sig[:32], "big"), int.from_bytes(sig[32:], "big")
            try:
                key.verify(utils.encode_dss_signature(r, s), content, ec.ECDSA(hashes.SHA256()))
                return True
            except Exception:
                continue
    return False`;
  }
}
