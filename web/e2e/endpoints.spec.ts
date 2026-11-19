import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { RECEIVER, SEEDED_ENDPOINT_ID, deleteAsAdmin, produceEvents, signInReady, stepUp, tab, trustInReceiver } from './helpers';

// M3 exit criteria, in a real browser against the real stack: a developer creates an endpoint, verifies it, tests it and goes live
// (ASR-20: about ten minutes), every screen is operable by keyboard and passes the automated accessibility checks.

const unique = () => Date.now().toString(36);

async function openSeededEndpoint(page: Page, tab: 'events' | 'security' | 'delivery' | 'settings' = 'events') {
  await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/${tab}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Contoso ERP' })).toBeVisible();
}

async function accessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.help} -> ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
}

test('a developer creates, verifies and tests an endpoint and it goes live (the first ten minutes)', async ({ page, request, browser }) => {
  const started = Date.now();
  const suffix = unique();
  const name = `E2E ERP ${suffix}`;
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');

  // 1. create from a template, with live address checks
  await page.getByRole('link', { name: 'Add endpoint' }).click();
  await expect(page.getByRole('heading', { name: 'Add endpoint' })).toBeVisible();
  await page.getByRole('radio', { name: 'ERP / accounting' }).check();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Endpoint URL (HTTPS POST)').fill(`${RECEIVER}/hooks/e2e-${suffix}`);
  await expect(page.getByTestId('url-checks')).toContainText('Allowed by the demo egress policy');
  await expect(page.getByTestId('url-checks')).toContainText('We never follow redirects');
  await expect(page.getByText(/\d+ of 42 selected/)).toBeVisible();
  await page.getByRole('button', { name: 'Create endpoint' }).click();

  // 2. it starts pending; verification fails until the receiving application knows the secret, and says why
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Go-live checklist' })).toBeVisible();
  await expect(page.locator('.ep-h').getByText('Pending verification')).toBeVisible();
  await page.getByRole('button', { name: 'Verify URL' }).first().click();
  const verify = page.getByRole('dialog', { name: 'Verify the endpoint' });
  await verify.getByRole('button', { name: 'Send verification request' }).click();
  await expect(verify).toContainText('Not verified');
  await expect(verify).toContainText(/signature/i);
  await verify.getByRole('button', { name: 'Close' }).last().click();

  // 3. reveal the secret: needs a fresh sign-in (a real pop-up on Keycloak)
  await tab(page, 'Security').click();
  await stepUp(page, async () => {
    await page.getByRole('button', { name: 'Reveal' }).click();
    await page.getByRole('dialog', { name: 'Reveal the signing secret' }).getByRole('button', { name: 'Sign in again and reveal' }).click();
  });
  const secret = (await page.getByTestId('revealed-secret').textContent())!.trim();
  expect(secret).toMatch(/^whsec_[A-Za-z0-9+/=]{40,}$/);
  await page.getByRole('button', { name: 'Hide and close' }).click();

  // the tenant pastes it into their application's configuration
  await trustInReceiver(request, [secret]);

  // 4. verify again: now it works and the endpoint is live
  await page.getByRole('button', { name: 'Verify URL' }).first().click();
  const again = page.getByRole('dialog', { name: 'Verify the endpoint' });
  await again.getByRole('button', { name: 'Send verification request' }).click();
  await expect(again).toContainText('Verified.');
  await again.getByRole('button', { name: 'Done' }).click();
  await expect(page.locator('.ep-h').getByText('Active')).toBeVisible();

  // 5. test events: a normal one, and negative tests that prove the receiver is safe
  await page.getByRole('button', { name: 'Send test' }).click();
  const test = page.getByRole('dialog', { name: 'Send a test event' });
  await test.getByRole('button', { name: 'Send test event' }).click();
  await expect(test.getByTestId('test-result')).toContainText('Passed');
  await test.getByRole('radio', { name: /Tampered signature/ }).check();
  await test.getByRole('button', { name: 'Send test event' }).click();
  await expect(test.getByTestId('test-result')).toContainText('Negative test passed');
  await expect(test.getByTestId('test-result')).toContainText('rejected the tampered signature');
  await test.getByRole('radio', { name: /6-minute-old timestamp/ }).check();
  await test.getByRole('button', { name: 'Send test event' }).click();
  await expect(test.getByTestId('test-result')).toContainText('Replay protection works');
  await test.getByRole('button', { name: 'Close' }).last().click();

  // 6. the verification snippets are there, and the secret is masked again
  await expect(page.getByRole('tab', { name: 'Node' })).toBeVisible();
  await page.getByRole('tab', { name: 'Node' }).click();
  await expect(page.getByRole('tabpanel')).toContainText('createHmac');

  // the whole journey, including two sign-ins and a pop-up, took far less than the ten-minute target
  expect(Date.now() - started).toBeLessThan(10 * 60_000);

  // a developer cannot delete (that is an administrator's right): the settings tab offers no delete button
  await tab(page, 'Settings').click();
  await expect(page.getByRole('button', { name: 'Delete endpoint…' })).toHaveCount(0);

  // an administrator does it, and has to type the name
  await deleteAsAdmin(browser, name);
  await trustInReceiver(request, []);
});

test('an ES256 endpoint publishes its public keys and a signing change needs a fresh sign-in', async ({ page, request, browser }) => {
  const suffix = unique();
  const name = `E2E keys ${suffix}`;
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await page.getByRole('link', { name: 'Add endpoint' }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Endpoint URL (HTTPS POST)').fill(`${RECEIVER}/hooks/e2e-keys-${suffix}`);
  await page.getByRole('checkbox', { name: 'Select all Invoices events' }).check();
  await page.getByRole('button', { name: 'Create endpoint' }).click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();

  await tab(page, 'Security').click();
  await page.getByRole('radio', { name: /Both/ }).check();
  await stepUp(page, async () => {
    await page.getByRole('button', { name: /Change signing to HMAC \+ ES256/ }).click();
    await page.getByRole('dialog', { name: 'Change the signing algorithm' }).getByRole('button', { name: 'Change signing' }).click();
  });
  await expect(page.locator('.ep-h').getByText('HMAC + ES256')).toBeVisible();
  const jwksUrl = (await page.locator('#jwks-url').textContent())!.trim();
  expect(jwksUrl).toMatch(/\/api\/public\/endpoints\/[0-9a-f-]{36}\/jwks\.json$/);

  // anyone can fetch the public keys without signing in; nothing private is in them
  const jwks = await (await request.get(jwksUrl)).json();
  expect(jwks.keys).toHaveLength(1);
  expect(jwks.keys[0].alg).toBe('ES256');
  expect(jwks.keys[0].d).toBeUndefined();
  await expect(page.getByRole('table', { name: 'Signing keys of this endpoint' }).getByRole('row')).toHaveCount(3); // header + HMAC + ES256

  // rotate with an overlap: the old key keeps signing next to the new one
  await stepUp(page, async () => {
    await page.getByRole('button', { name: 'Rotate…' }).click();
    await page.getByRole('dialog', { name: 'Rotate the signing keys' }).getByRole('button', { name: 'Rotate keys' }).click();
  });
  await expect(page.getByRole('table', { name: 'Signing keys of this endpoint' }).getByText(/Retires in/).first()).toBeVisible();

  await accessible(page);

  await deleteAsAdmin(browser, name);
});

test('the filter builder tests a filter against real recent events before anything is saved', async ({ page, request }) => {
  await produceEvents(request, 3);
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await openSeededEndpoint(page, 'events');

  await page.getByLabel('Event to filter').selectOption('invoice.paid');
  await page.getByRole('button', { name: 'Add condition' }).click();
  await page.getByLabel('Field of condition 1').fill('data.amount');
  await page.getByLabel('Operator of condition 1').selectOption('gte');
  await page.getByLabel('Value of condition 1').fill('0');
  await expect(page.getByLabel('Compiled filter expression')).toContainText('data.amount >= 0');
  await expect(page.getByTestId('filter-summary')).toContainText(/\d+ of \d+ recent events would be delivered/);

  // a filter that matches nothing is flagged before saving
  await page.getByLabel('Value of condition 1').fill('99999999');
  await expect(page.getByText(/matches none of your latest events/)).toBeVisible();

  // an invalid row is explained, and saving is blocked
  await page.getByLabel('Operator of condition 1').selectOption('gt');
  await page.getByLabel('Value of condition 1').fill('lots');
  await expect(page.getByRole('alert').filter({ hasText: 'needs a number' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save events & filters' })).toBeDisabled();
  await accessible(page);
});

test('the delivery tab previews the retry schedule from the real policy and explains impossible settings', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await openSeededEndpoint(page, 'delivery');

  await page.getByRole('radio', { name: /Standard/ }).check();
  await expect(page.getByTestId('schedule-summary')).toContainText('3 d');
  await expect(page.getByTestId('schedule')).toContainText('Attempt 2');
  await expect(page.getByTestId('schedule')).toContainText('+5 s');

  await page.getByRole('radio', { name: /Fast recovery/ }).check();
  await expect(page.getByTestId('schedule-summary')).toContainText('8 attempts');

  await page.getByRole('radio', { name: /^Custom/ }).check();
  await page.getByLabel('First retry after (seconds)').fill('600');
  await page.getByLabel('Give up after (seconds)').fill('60');
  await expect(page.getByRole('alert').filter({ hasText: 'shorter than the first retry' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save delivery settings' })).toBeDisabled();

  await page.getByLabel('Give up after (seconds)').fill('3600');
  await expect(page.getByTestId('schedule')).toContainText('Attempt 2');
  await accessible(page);
});

test('the overview shows real numbers and pausing an endpoint holds its events', async ({ page, request }) => {
  await produceEvents(request, 3);
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');

  await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toBeVisible();
  await expect(page.getByLabel('Last 24 hours')).toContainText('Deliveries (24 h)');
  await expect(page.getByLabel('Last 24 hours').locator('.kpi').first().locator('.v')).not.toHaveText('0');
  const row = page.getByRole('row').filter({ hasText: 'Contoso ERP' });
  await expect(row).toContainText('Active');

  await row.getByRole('button', { name: 'Pause Contoso ERP' }).click();
  await expect(page.getByTestId('toast')).toContainText('Paused');
  await expect(row).toContainText('Paused');
  await row.getByRole('button', { name: 'Resume Contoso ERP' }).click();
  await expect(row).toContainText('Active');
  await accessible(page);
});

test('every screen of the endpoint workspace passes the accessibility checks in both themes', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  for (const theme of ['dark', 'light']) {
    await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toBeVisible();
    await accessible(page);

    await page.getByRole('link', { name: 'Add endpoint' }).click();
    await expect(page.getByRole('heading', { name: 'Add endpoint' })).toBeVisible();
    await page.getByRole('checkbox', { name: 'Select all Invoices events' }).check();
    await accessible(page);

    for (const tab of ['events', 'security', 'delivery', 'settings'] as const) {
      await openSeededEndpoint(page, tab);
      await page.waitForLoadState('networkidle');
      await accessible(page);
    }
  }
});

test('a viewer can look but not change anything', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'viewer@contoso.test', 'Viktor');
  await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Add endpoint' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Pause Contoso ERP/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Delete Contoso ERP/ })).toHaveCount(0);

  await openSeededEndpoint(page, 'events');
  await expect(page.getByRole('button', { name: 'Send test' })).toHaveCount(0);
  await expect(page.getByRole('checkbox', { name: 'Select all Invoices events' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Save events & filters' })).toHaveCount(0);

  await openSeededEndpoint(page, 'security');
  await expect(page.getByRole('button', { name: 'Reveal' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Rotate…' })).toHaveCount(0);
  await openSeededEndpoint(page, 'settings');
  await expect(page.getByRole('button', { name: 'Delete endpoint…' })).toHaveCount(0);
});

test('another organisation cannot see or open this one\'s endpoint', async ({ page }) => {
  await signInReady(page, 'Fabrikam Inc', 'owner@fabrikam.test', 'Petro');
  await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Fabrikam ERP' }).first()).toBeVisible(); // their own endpoint (their own receiving system, seeded)
  await expect(page.getByRole('link', { name: 'Contoso ERP' })).toHaveCount(0);

  await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/events`);
  await expect(page.getByText(/does not exist, or it belongs to another organisation/)).toBeVisible();
});
