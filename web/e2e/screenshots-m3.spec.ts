import path from 'node:path';
import { expect, test } from '@playwright/test';
import { RECEIVER, SEEDED_ENDPOINT_ID, produceEvents, signInReady, trustInReceiver } from './helpers';

// Not a test: captures the M3 screens for the review document. Run with SHOTS=1 (see tests/run-e2e.ps1 -Shots -Grep "M3 screens").
test.skip(!process.env.SHOTS, 'screenshots are only taken on request');

const out = (name: string) => path.resolve(__dirname, '..', '..', 'docs', 'screenshots', `${name}.png`);

for (const theme of ['dark', 'light'] as const) {
  test.describe(`M3 screens, ${theme}`, () => {
    test.use({ viewport: { width: 1360, height: 900 } });
    test.beforeEach(async ({ page }) => {
      await page.addInitScript((t) => localStorage.setItem('ih.theme', t), theme);
    });

    test('overview and endpoint workspace', async ({ page, request }) => {
      await produceEvents(request, 4);
      await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
      await page.waitForLoadState('networkidle');
      await page.screenshot({ path: out(`m3-01-overview-${theme}`), fullPage: true });

      await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/events`);
      await expect(page.getByRole('heading', { level: 1, name: 'Contoso ERP' })).toBeVisible();
      await page.getByLabel('Event to filter').selectOption('invoice.paid');
      await page.getByRole('button', { name: 'Add condition' }).click();
      await page.getByLabel('Field of condition 1').fill('data.amount');
      await page.getByLabel('Operator of condition 1').selectOption('gte');
      await page.getByLabel('Value of condition 1').fill('2000');
      await expect(page.getByLabel('Compiled filter expression')).toContainText('data.amount >= 2000');
      await expect(page.getByTestId('filter-summary')).toBeVisible();
      await expect(page.locator('[data-stale="false"]')).toBeVisible(); // the result on screen is the one for what is typed
      await page.screenshot({ path: out(`m3-03-events-filters-${theme}`), fullPage: true });

      await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/security`);
      await expect(page.getByRole('heading', { name: '3 · Message signing' })).toBeVisible();
      await page.screenshot({ path: out(`m3-04-security-${theme}`), fullPage: true });

      await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/delivery`);
      await expect(page.getByTestId('schedule')).toBeVisible();
      await page.getByRole('radio', { name: /Fast recovery/ }).check();
      await expect(page.getByTestId('schedule-summary')).toContainText('8 attempts');
      await page.screenshot({ path: out(`m3-05-delivery-${theme}`), fullPage: true });
    });

    test('create and test dialog', async ({ page, request }) => {
      await signInReady(page, 'Contoso Ltd', 'developer@contoso.test', 'Dmytro');
      await page.getByRole('link', { name: 'Add endpoint' }).click();
      await page.getByRole('radio', { name: 'ERP / accounting' }).check();
      await page.getByLabel('Name', { exact: true }).fill('ERP – Staging');
      await page.getByLabel('Endpoint URL (HTTPS POST)').fill(`${RECEIVER}/hooks/staging`);
      await expect(page.getByTestId('url-checks')).toContainText('We never follow redirects');
      await page.screenshot({ path: out(`m3-02-create-${theme}`), fullPage: true });

      await trustInReceiver(request, []);
      await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/events`);
      await page.getByRole('button', { name: 'Send test' }).click();
      const dialog = page.getByRole('dialog', { name: 'Send a test event' });
      await dialog.getByRole('radio', { name: /Tampered signature/ }).check();
      await dialog.getByRole('button', { name: 'Send test event' }).click();
      await expect(dialog.getByTestId('test-result')).toContainText('Negative test passed');
      await page.screenshot({ path: out(`m3-06-test-dialog-${theme}`) });
    });
  });
}
