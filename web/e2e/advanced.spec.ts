import AxeBuilder from '@axe-core/playwright';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { RECEIVER, SEEDED_ENDPOINT_ID, produceEvents, signInReady, stepUp, tab, trustInReceiver } from './helpers';

// M4 in a real browser against the real stack: delivery logs and the drawer, the Playground, the Replay center (resume guide, dry run, typed confirmation),
// API keys (step-up, shown once), the audit log, team management, alerts with the bell, and the accessibility of all of it in both themes.

const MAIL = 'http://localhost:8025';
const unique = () => Date.now().toString(36);

async function accessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.help} -> ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
}

async function bothThemes(page: Page, check: () => Promise<void>) {
  for (const theme of ['dark', 'light']) {
    await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
    await check();
  }
}

/** The nav link of a main screen (the side navigation, not a link inside the page). */
const nav = (page: Page, name: string) => page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name });

test('the delivery log filters, opens a drawer with the request as sent and copies it as cURL', async ({ page, request, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await produceEvents(request, 3);
  await signInReady(page, 'Contoso Ltd', 'support@contoso.test', 'Sofiia');

  await nav(page, 'Delivery logs').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Delivery logs' })).toBeVisible();
  await expect(page.getByTestId('log-row').first()).toBeVisible();
  const delivered = page.getByRole('button', { name: /^Delivered/ });
  await expect(delivered).toContainText(/\d/);

  // filters: status chip, event type, search by event name
  await delivered.click();
  await expect(delivered).toHaveAttribute('aria-pressed', 'true');
  await page.getByLabel('Event type').selectOption('invoice.paid');
  await expect(page.getByTestId('log-row').first()).toContainText('invoice.paid');
  await page.getByLabel('Search').fill('invoice.paid');
  await expect(page.getByTestId('log-row').first()).toContainText('invoice.paid');
  await page.getByLabel('Search').fill('nothing-matches-this');
  await expect(page.getByText('Nothing matches these filters')).toBeVisible();
  await page.getByLabel('Search').fill('');

  // the drawer
  await page.getByTestId('log-row').first().getByRole('button').click();
  const drawer = page.getByRole('dialog');
  await expect(drawer.getByRole('tab', { name: 'Request' })).toHaveAttribute('aria-selected', 'true');
  await expect(drawer).toContainText('webhook-signature');
  await expect(drawer).toContainText('"type": "invoice.paid"');
  await drawer.getByRole('button', { name: 'Copy as cURL' }).click();
  await expect(page.getByTestId('toast')).toContainText('cURL command copied');
  const curl = await page.evaluate(() => navigator.clipboard.readText());
  expect(curl).toContain('curl -X POST');
  expect(curl).not.toContain('webhook-signature');

  await drawer.getByRole('tab', { name: 'Response' }).click();
  await expect(drawer).toContainText('HTTP 200');
  await drawer.getByRole('tab', { name: 'Attempts' }).click();
  await expect(drawer.getByRole('table', { name: 'Attempts' })).toContainText('Success');
  await drawer.getByRole('tab', { name: 'Event' }).click();
  await expect(drawer).toContainText('Webhook id');
  await bothThemes(page, () => accessible(page));

  // support may replay one event; the same webhook-id goes out again
  await drawer.getByRole('button', { name: 'Replay this event' }).click();
  await expect(page.getByTestId('toast')).toContainText('Replay queued');
  await page.keyboard.press('Escape');
  await expect(drawer).toBeHidden();
});

test('a viewer sees deliveries but not payloads, and cannot replay', async ({ page, request }) => {
  await produceEvents(request, 1);
  await signInReady(page, 'Contoso Ltd', 'viewer@contoso.test', 'Viktor');
  await nav(page, 'Delivery logs').click();
  await page.getByTestId('log-row').first().getByRole('button').click();
  const drawer = page.getByRole('dialog');
  await expect(drawer).toContainText('Your role cannot view payloads');
  await expect(drawer.getByRole('button', { name: 'Replay this event' })).toBeDisabled();
});

test('the playground sends edited data, runs a negative test and explains a rejected request', async ({ page, request }) => {
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await trustInReceiver(request, []); // the seeded secret is what the receiver knows by default
  await nav(page, 'Playground').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Playground' })).toBeVisible();

  await page.getByRole('button', { name: 'invoice.paid' }).click();
  const editor = page.getByLabel('Event data (JSON)');
  await expect(editor).toContainText('amount');
  await editor.fill('{ "invoice_id": "inv_e2e_42", "amount": 12.5, "currency": "EUR" }');
  await page.getByLabel('Send to').selectOption({ label: 'Contoso ERP · production' });
  await page.getByRole('button', { name: 'Send test event' }).click();
  const result = page.getByTestId('playground-result');
  await expect(result).toContainText('Passed');
  await expect(result).toContainText('inv_e2e_42');

  // broken JSON is caught before anything is sent
  await editor.fill('{ "amount": ');
  await expect(page.getByRole('alert').filter({ hasText: 'not valid JSON' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Send test event' })).toBeDisabled();
  await editor.fill('[1,2]');
  await expect(page.getByRole('alert').filter({ hasText: 'must be a JSON object' })).toBeVisible();
  await page.getByRole('button', { name: 'Reset to sample' }).click();

  // a negative test passes when the receiver REFUSES the bad request
  await page.getByRole('radio', { name: /Tampered signature/ }).check();
  await page.getByRole('button', { name: 'Send test event' }).click();
  await expect(result).toContainText('Negative test passed');
  await expect(result).toContainText('rejected the tampered signature');
  await bothThemes(page, () => accessible(page));
});

test('pausing holds events, resume needs a passing test, then the held events are delivered at a steady pace', async ({ page, request }) => {
  const name = `E2E replay ${unique()}`;
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');

  // a fresh endpoint through the real create screen, verified through the real dialog
  await page.getByRole('link', { name: 'Add endpoint' }).click();
  await page.getByRole('radio', { name: 'ERP / accounting' }).check();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Endpoint URL (HTTPS POST)').fill(`${RECEIVER}/hooks/e2e-replay-${unique()}`);
  await page.getByRole('button', { name: 'Create endpoint' }).click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
  await tab(page, 'Security').click();
  await stepUp(page, async () => {
    await page.getByRole('button', { name: 'Reveal' }).click();
    await page.getByRole('dialog', { name: 'Reveal the signing secret' }).getByRole('button', { name: 'Sign in again and reveal' }).click();
  });
  const secret = (await page.getByTestId('revealed-secret').textContent())!.trim();
  await page.getByRole('button', { name: 'Hide and close' }).click();
  await trustInReceiver(request, [secret]);
  await page.getByRole('button', { name: 'Verify URL' }).first().click();
  const verify = page.getByRole('dialog', { name: 'Verify the endpoint' });
  await verify.getByRole('button', { name: 'Send verification request' }).click();
  await expect(verify).toContainText('Verified.');
  await verify.getByRole('button', { name: 'Done' }).click();

  // pause, produce events: they are held, not lost
  await page.getByRole('button', { name: 'Pause' }).click();
  await expect(page.getByTestId('toast')).toContainText('Paused');
  await produceEvents(request, 2);
  await nav(page, 'Replay center').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Replay center' })).toBeVisible();
  const card = page.getByTestId('held-card').filter({ hasText: name });
  await expect(card).toContainText(/Held events\s*\d+/);
  await bothThemes(page, () => accessible(page));

  // the resume guide: a passing test is required. The verification above counts (it is a test that passed a moment ago), so the guide says so;
  // "no passing test, no resume" is checked on the API in PortalAdvancedApiTests (an endpoint without a recent test is refused with test_required)
  await card.getByRole('button', { name: 'Resume…' }).click();
  const guide = page.getByRole('dialog', { name: `Resume “${name}”` });
  await expect(guide.getByText('Test passed')).toBeVisible();
  await guide.getByRole('button', { name: 'Test again' }).click();
  await expect(guide.getByText('Test passed')).toBeVisible();
  await guide.getByRole('radio', { name: /Deliver all/ }).check();
  await guide.getByRole('button', { name: 'Resume endpoint' }).click();
  await expect(page.getByTestId('toast')).toContainText(/Resumed/);

  // the job is visible with its progress and finishes
  await page.getByRole('tab', { name: /Jobs/ }).click();
  const job = page.getByTestId('job-card').filter({ hasText: name }).first();
  await expect(job).toContainText('Resume');
  await expect(job).toContainText(/Completed/, { timeout: 60_000 });

  // the log shows them as delivered
  await page.goto(`/logs?range=1h`);
  await expect(page.getByTestId('log-row').filter({ hasText: name }).first()).toContainText('Delivered', { timeout: 30_000 });
});

test('a replay job asks for a dry run and a typed confirmation that names the number of events', async ({ page, request }) => {
  await produceEvents(request, 2);
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await nav(page, 'Replay center').click();
  await page.getByRole('button', { name: 'New replay job' }).click();
  const dialog = page.getByRole('dialog', { name: 'New replay job' });
  await dialog.getByLabel('Endpoint').selectOption({ label: 'Contoso ERP' });
  await dialog.getByRole('checkbox', { name: 'Dead events' }).uncheck();
  await dialog.getByRole('checkbox', { name: 'Events already delivered' }).check();
  await dialog.getByLabel('Event type (optional)').selectOption('invoice.paid');
  await dialog.getByRole('button', { name: /Dry run/ }).click();
  const dry = dialog.getByTestId('dry-run');
  await expect(dry).toContainText('Dry run: nothing was sent');
  const phrase = (await dry.locator('b.mono').textContent())!.trim();
  expect(phrase).toMatch(/^REPLAY \d+$/);

  const start = dialog.getByRole('button', { name: /^Replay \d+ events/ });
  await expect(start).toBeDisabled();
  await dialog.getByLabel(/Type .* to confirm/).fill('REPLAY 1');
  await expect(start).toBeDisabled(); // the wrong number does not unlock it
  await dialog.getByLabel(/Type .* to confirm/).fill(phrase);
  await expect(start).toBeEnabled();
  await bothThemes(page, () => accessible(page));
  await start.click();

  await expect(page.getByRole('tab', { name: /Jobs/ })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('job-card').first()).toContainText('Replay');
});

test('API keys are created after a fresh sign-in, shown once, listed without the key and revoked by typing their name', async ({ page }) => {
  const name = `Nightly job ${unique()}`;
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await nav(page, 'API keys & IPs').click();
  await expect(page.getByRole('heading', { level: 1, name: 'API keys & IPs' })).toBeVisible();
  await expect(page.getByRole('list', { name: 'Outbound addresses' })).toBeVisible();

  await page.getByRole('button', { name: 'Create API key' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create an API key' });
  await expect(dialog.getByRole('button', { name: 'Create key' })).toBeDisabled();
  await dialog.getByLabel('Name').fill(name);
  await dialog.getByRole('checkbox', { name: /events:read/ }).check();
  await stepUp(page, async () => { await dialog.getByRole('button', { name: 'Create key' }).click(); });

  const shown = page.getByRole('dialog', { name: 'Your new API key' });
  const key = (await shown.getByTestId('new-key').textContent())!.trim();
  expect(key).toMatch(/^ihk_[0-9a-f]{32}_[0-9a-f]{32}_/);
  await shown.getByRole('button', { name: 'I have copied it' }).click();

  const row = page.getByRole('row').filter({ hasText: name });
  await expect(row).toContainText('events:read');
  await expect(row).toContainText('never used');
  await expect(page.getByText(key)).toHaveCount(0); // never shown again
  await bothThemes(page, () => accessible(page));

  await row.getByRole('button', { name: `Revoke ${name}` }).click();
  const confirm = page.getByRole('dialog', { name: 'Revoke this key?' });
  await expect(confirm.getByRole('button', { name: 'Revoke key' })).toBeDisabled();
  await confirm.getByLabel(/Type .* to confirm/).fill(name);
  await confirm.getByRole('button', { name: 'Revoke key' }).click();
  await expect(page.getByRole('row').filter({ hasText: name })).toContainText('Revoked');
});

test('the audit log shows who changed what, for owners and admins only', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'admin@contoso.test', 'Taras');
  await nav(page, 'Audit log').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Audit log' })).toBeVisible();
  await expect(page.getByTestId('audit-row').first()).toBeVisible();
  await page.getByLabel('Area').selectOption('apikey.*');
  await expect(page.getByTestId('audit-row').first()).toContainText(/API key/);
  await page.getByLabel('Area').selectOption('');
  await page.getByTestId('audit-row').first().locator('summary').click();
  await expect(page.getByTestId('audit-row').first().locator('pre')).toContainText('"action"');
  await bothThemes(page, () => accessible(page));
});

test('a developer is told the audit log and team pages are not for them', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await nav(page, 'Audit log').click();
  await expect(page.getByText('Your role cannot read the audit log')).toBeVisible();
  await nav(page, 'Team & roles').click();
  await expect(page.getByText('Your role cannot manage the team')).toBeVisible();
});

test('an admin adds a team member, changes their role and disables them; the guard rails hold', async ({ page }) => {
  const email = `e2e-${unique()}@contoso.test`;
  await signInReady(page, 'Contoso Ltd', 'admin@contoso.test', 'Taras');
  await nav(page, 'Team & roles').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Team & roles' })).toBeVisible();

  // an admin cannot touch the owner, and cannot change their own role
  const ownerRow = page.getByRole('row').filter({ hasText: 'owner@contoso.test' });
  await expect(ownerRow.getByLabel(/Role of/)).toBeDisabled();
  await expect(page.getByRole('row').filter({ hasText: 'admin@contoso.test' }).getByLabel(/Role of/)).toBeDisabled();

  await page.getByRole('button', { name: 'Add team member' }).click();
  const dialog = page.getByRole('dialog', { name: 'Add a team member' });
  await dialog.getByLabel('First name').fill('Nina');
  await dialog.getByLabel('Last name').fill('Tester');
  await dialog.getByLabel('Work email').fill(email);
  await dialog.getByLabel('Role').selectOption('support');
  await dialog.getByRole('button', { name: 'Add member' }).click();
  const password = page.getByRole('dialog', { name: 'Nina Tester was added' });
  expect((await password.getByTestId('temp-password').textContent())!.trim()).toHaveLength(16);
  await password.getByRole('button', { name: 'Done' }).click();

  const row = page.getByRole('row').filter({ hasText: email });
  await row.getByLabel('Role of Nina Tester').selectOption('developer');
  await expect(page.getByTestId('toast')).toContainText('Role changed');
  await row.getByRole('button', { name: 'Disable Nina Tester' }).click();
  await expect(row).toContainText('Disabled');
  await expect(page.getByTestId('toast')).toContainText('disabled and signed out');
  await bothThemes(page, () => accessible(page));
});

test('a failing endpoint raises the bell with a pre-warning, and the alert rules can be changed', async ({ page, request }) => {
  test.setTimeout(240_000);
  await signInReady(page, 'Contoso Ltd', 'owner@contoso.test', 'Olena');

  // the rules page: defaults, then a change by an owner
  await nav(page, 'Alerts').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Alerts' })).toBeVisible();
  const rule = page.getByRole('region', { name: 'Endpoint is failing' });
  await expect(rule).toContainText('Platform default');
  await rule.getByRole('checkbox', { name: 'By email' }).check();
  await rule.getByRole('button', { name: 'Save Endpoint is failing' }).click();
  await expect(page.getByTestId('toast')).toContainText('Saved');
  await expect(rule).toContainText(/Changed by/);
  await rule.getByRole('checkbox', { name: 'By email' }).uncheck();
  await rule.getByRole('button', { name: 'Save Endpoint is failing' }).click();
  await bothThemes(page, () => accessible(page));

  // make the seeded endpoint's thresholds small, so a short burst of failures crosses the warning lines (25% of 8 = 2, the breaker opens at 4)
  const token = await page.evaluate(() => {
    const key = Object.keys(sessionStorage).find((k) => k.startsWith('oidc.user:'))!;
    return (JSON.parse(sessionStorage.getItem(key)!) as { access_token: string }).access_token;
  });
  const api = 'http://localhost:5100';
  const headers = { Authorization: `Bearer ${token}` };
  const delivery = `${api}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}/delivery`;
  expect((await request.put(delivery, { headers, data: { circuitFailures: 4, autoDisable: { failures: 8, withinHours: 24, days: 5 } } })).ok()).toBe(true);

  try {
    expect((await request.put(`${RECEIVER}/api/chaos`, { data: { mode: 'fail500', delaySeconds: 15 } })).ok()).toBe(true);
    const sim = await request.post('http://localhost:5400/simulator/invoices', { data: { tenant: 'contoso', count: 4, perSecond: 10, payRatio: 1, overdueRatio: 0, failedPaymentRatio: 0 } });
    expect(sim.status()).toBe(202);

    // the bell: an unread count, the alert in words, and a link to where the problem can be seen
    await expect.poll(async () => {
      await page.reload();
      await page.getByRole('button', { name: /^Notifications, \d+ unread$/ }).waitFor(); // the shell has rendered: only then is a count of zero a real zero
      return page.getByRole('button', { name: /^Notifications, [1-9]\d* unread$/ }).count();
    }, { timeout: 90_000, intervals: [3_000] }).toBe(1);
    await page.getByRole('button', { name: /^Notifications, \d+ unread$/ }).click();
    const panel = page.getByRole('region', { name: 'Notifications' });
    await expect(panel).toContainText('Contoso ERP');
    await expect(panel).toContainText(/% of the way to being switched off/);
    await bothThemes(page, () => accessible(page));
    await panel.getByRole('link', { name: 'Open the delivery log' }).first().click();
    await expect(page).toHaveURL(new RegExp(`/endpoints/${SEEDED_ENDPOINT_ID}/logs`));
    await expect(page.getByTestId('log-row').first()).toBeVisible();
  } finally {
    // leave the demo healthy for whatever runs next
    await request.put(`${RECEIVER}/api/chaos`, { data: { mode: 'ok', delaySeconds: 15 } });
    await request.put(delivery, { headers, data: { circuitFailures: 5, autoDisable: { failures: 100, withinHours: 24, days: 5 } } });
    await request.post('http://localhost:5400/simulator/stop-all');
  }
  // The burst may have switched the endpoint off (that is what the thresholds are for, and it depends on timing). Leave the demo healthy for whatever runs next,
  // the way an owner would: a passing test, then resume (what was held is discarded), then real traffic that succeeds.
  const endpoint = async () => (await (await request.get(`${api}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}`, { headers })).json()).endpoint;
  if ((await endpoint()).status !== 'active') {
    expect((await request.post(`${api}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}/ping`, { headers })).ok()).toBe(true);
    expect((await request.post(`${api}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}/resume-guided`, { headers, data: { mode: 'discard' } })).ok()).toBe(true);
  }
  await produceEvents(request, 1);
  await expect.poll(async () => (await endpoint()).health, { timeout: 150_000, intervals: [5_000] }).toBe('healthy');
  await page.getByRole('button', { name: /^Notifications, \d+ unread$/ }).click();
  await page.getByRole('button', { name: 'Mark all as read' }).click();
  await expect(page.getByRole('button', { name: 'Notifications, 0 unread' })).toBeVisible();
});

test('the endpoint workspace has its own log tab, filtered to that endpoint', async ({ page, request }) => {
  await produceEvents(request, 1);
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/logs`);
  await expect(page.getByRole('heading', { level: 1, name: 'Contoso ERP' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Endpoint sections' }).getByRole('link', { name: 'Logs' })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('log-row').first()).toBeVisible();
  await expect(page.getByLabel('Endpoint', { exact: true })).toHaveCount(0); // already scoped: no endpoint filter
  await bothThemes(page, () => accessible(page));
});
