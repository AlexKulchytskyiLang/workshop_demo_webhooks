import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { SEEDED_ENDPOINT_ID, SIMULATOR, produceEvents, signInReady } from './helpers';

// M5 in a real browser: payload versions (compare before switching), the Playground's version picker, the two receiving systems' own UIs
// (live inbox, verified files, identity checks, the chaos switch) and the simulator console, with the accessibility checks on each.

const CONTOSO_UI = 'http://localhost:5174';
const FABRIKAM_UI = 'http://localhost:5175';
const SIMULATOR_UI = 'http://localhost:5400';

async function accessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.help} -> ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
}

async function bothColourSchemes(page: Page) {
  for (const scheme of ['dark', 'light'] as const) {
    await page.emulateMedia({ colorScheme: scheme });
    await accessible(page);
  }
}

test('the version dialog shows what an upgrade changes on a real sample before anything is switched', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/events`);
  await expect(page.getByRole('heading', { level: 1, name: 'Contoso ERP' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Payload version' })).toContainText('2026-11-01');

  await page.getByRole('button', { name: 'Compare versions…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Compare payload versions' });
  await dialog.getByLabel('Compare with').selectOption('2027-02-01');
  const diff = dialog.getByTestId('version-diff');
  await expect(diff).toContainText('total.amount');
  await expect(diff).toContainText('customer.display_name');
  await expect(dialog).toContainText('Preview');
  await dialog.getByLabel('Compare with').selectOption('2026-06-01');
  await expect(dialog.getByTestId('version-diff')).toContainText('customer_name');
  await expect(dialog).toContainText('ends on 2027-06-01');
  await expect(dialog.getByRole('button', { name: 'Switch to 2026-06-01' })).toBeEnabled(); // offered, but nothing is switched by looking

  for (const theme of ['dark', 'light']) {
    await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
    await accessible(page);
  }
  await dialog.getByRole('button', { name: 'Close' }).last().click();
  await expect(page.getByRole('region', { name: 'Payload version' })).toContainText('2026-11-01'); // still pinned where it was
});

test('the playground tries the preview version and the request carries it', async ({ page }) => {
  await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Playground' }).click();
  await page.getByRole('button', { name: 'invoice.paid' }).click();
  await page.getByLabel('Payload version').selectOption('2027-02-01');
  await expect(page.getByLabel('Event data (JSON)')).toContainText('"total"');
  await page.getByRole('button', { name: 'Send test event' }).click();
  const result = page.getByTestId('playground-result');
  await expect(result).toContainText('Passed');
  await expect(result).toContainText('2027-02-01'); // the request body carries the version it was built for
});

test('Contoso’s receiving system shows its live inbox, verified files and identity checks', async ({ page, request }) => {
  test.setTimeout(240_000);
  await request.post(`${CONTOSO_UI.replace('5174', '5201')}/api/reset`);
  await produceEvents(request, 2);
  expect((await request.post(`${SIMULATOR}/simulator/exports`, { data: { tenant: 'contoso', sizeMegabytes: 50 } })).status()).toBe(202);
  expect((await request.post(`${SIMULATOR}/simulator/kyc`, { data: { tenant: 'contoso', count: 2 } })).status()).toBe(202);

  await page.goto(CONTOSO_UI);
  await expect(page.getByRole('heading', { level: 1, name: 'Contoso ERP' })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Inbox' }).getByRole('row').nth(1)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByLabel('What this system holds')).toContainText('Received');
  await bothColourSchemes(page);

  await page.getByRole('tab', { name: 'Invoices & renewals' }).click();
  await expect(page.getByRole('table', { name: 'Invoices' })).toContainText('paid', { timeout: 30_000 });

  await page.getByRole('tab', { name: 'Files & identity checks' }).click();
  await expect(page.getByRole('table', { name: 'Files' })).toContainText('verified', { timeout: 150_000 });
  await expect(page.getByRole('table', { name: 'Files' })).toContainText('50.0 MB');
  await expect(page.getByRole('table', { name: 'Identity checks' })).toContainText('approved', { timeout: 60_000 });
  await bothColourSchemes(page);

  await page.getByRole('tab', { name: 'Inbox' }).click();
  await page.getByRole('row', { name: /Open export.completed/ }).click();
  await expect(page.getByRole('region', { name: 'Event detail' })).toBeVisible();
  await expect(page.getByLabel('Body as received')).toContainText('"download"');
  const body = (await page.getByLabel('Body as received').textContent()) ?? '';
  expect(body).not.toContain('"file"'); // the platform's internal reference to where the file lives never reaches the receiver: only an expiring link does
  expect(body).toContain('expires_at');
});

test('the chaos switch makes the receiver sick and the page says so', async ({ page, request }) => {
  await page.goto(CONTOSO_UI);
  await page.getByRole('tab', { name: 'Break things & trust' }).click();
  // the switch is the receiver's own state (set through its API, then shown), so the radio flips after the round trip: click, then wait for it
  await page.getByRole('radio', { name: /Server error \(500\)/ }).click();
  await expect(page.getByRole('radio', { name: /Server error \(500\)/ })).toBeChecked();
  await expect(page.getByRole('status').filter({ hasText: 'Broken on purpose: fail500' })).toBeVisible();
  await bothColourSchemes(page);
  await page.getByRole('radio', { name: /Healthy/ }).click();
  await expect(page.getByRole('radio', { name: /Healthy/ })).toBeChecked();
  await expect(page.getByText('Broken on purpose')).toHaveCount(0);
  expect((await request.get('http://localhost:5201/api/stats')).ok()).toBe(true);
});

test('the second tenant has its own receiving system on its own port', async ({ page }) => {
  await page.goto(FABRIKAM_UI);
  await expect(page.getByRole('heading', { level: 1, name: 'Fabrikam ERP' })).toBeVisible();
  await expect(page.getByText('http://localhost:5202')).toBeVisible();
  await bothColourSchemes(page);
});

test('the simulator console starts business activity and says what it did', async ({ page }) => {
  await page.goto(SIMULATOR_UI);
  await expect(page.getByRole('heading', { level: 1, name: 'InvoiceHub simulator' })).toBeVisible();
  await expect(page.getByLabel('Business activity for')).toContainText('Contoso Ltd');
  await page.getByRole('spinbutton', { name: 'Renewals' }).fill('2');
  await page.getByRole('button', { name: 'Renew subscriptions' }).click();
  await expect(page.getByRole('status')).toContainText('2 subscriptions renewed');
  await page.getByRole('button', { name: 'Stop all traffic' }).click();
  await expect(page.getByRole('status')).toContainText('All traffic stopped');
  await bothColourSchemes(page);
});
