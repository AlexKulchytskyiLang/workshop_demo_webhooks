import path from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { SEEDED_ENDPOINT_ID, SIMULATOR, produceEvents, signInReady } from './helpers';

// Not a test: captures the M4 and M5 screens for the review document. Run with SHOTS=1 (tests/run-e2e.ps1 -Shots -Grep "M4 screens").
test.skip(!process.env.SHOTS, 'screenshots are only taken on request');

const out = (name: string) => path.resolve(__dirname, '..', '..', 'docs', 'screenshots', `${name}.png`);
const API = 'http://localhost:5100';
const nav = (page: Page, name: string) => page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name });

async function token(page: Page): Promise<string> {
  return page.evaluate(() => {
    const key = Object.keys(sessionStorage).find((k) => k.startsWith('oidc.user:'))!;
    return (JSON.parse(sessionStorage.getItem(key)!) as { access_token: string }).access_token;
  });
}

async function releaseSeeded(page: Page, request: APIRequestContext) {
  const headers = { Authorization: `Bearer ${await token(page)}` };
  await request.post(`${API}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}/ping`, { headers });
  await request.post(`${API}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}/resume-guided`, { headers, data: { mode: 'deliver_all' } });
}

for (const theme of ['dark', 'light'] as const) {
  test.describe(`M4 screens, ${theme}`, () => {
    test.use({ viewport: { width: 1360, height: 900 } });
    test.beforeEach(async ({ page }) => {
      await page.addInitScript((t) => localStorage.setItem('ih.theme', t), theme);
    });

    test('logs, playground and the replay center', async ({ page, request }) => {
      test.setTimeout(240_000);
      await produceEvents(request, 4);
      await signInReady(page, 'Contoso Ltd', 'owner@contoso.test', 'Olena');

      await nav(page, 'Delivery logs').click();
      await expect(page.getByTestId('log-row').first()).toBeVisible();
      await page.screenshot({ path: out(`m4-01-logs-${theme}`), fullPage: true });
      await page.getByTestId('log-row').first().getByRole('button').click();
      await expect(page.getByRole('dialog').getByRole('tab', { name: 'Request' })).toBeVisible();
      await page.screenshot({ path: out(`m4-02-drawer-${theme}`) });
      await page.keyboard.press('Escape');

      await nav(page, 'Playground').click();
      await page.getByRole('button', { name: 'invoice.paid' }).click();
      await page.getByRole('radio', { name: /Tampered signature/ }).check();
      await page.getByRole('button', { name: 'Send test event' }).click();
      await expect(page.getByTestId('playground-result')).toContainText('Negative test passed');
      await page.screenshot({ path: out(`m4-03-playground-${theme}`), fullPage: true });

      // hold some events: pause the demo endpoint, produce, then walk through the resume guide
      const headers = { Authorization: `Bearer ${await token(page)}` };
      await request.post(`${API}/api/portal/endpoints/${SEEDED_ENDPOINT_ID}/pause`, { headers });
      try {
        await request.post(`${SIMULATOR}/simulator/invoices`, { data: { tenant: 'contoso', count: 3, perSecond: 10, payRatio: 1, overdueRatio: 0, failedPaymentRatio: 0 } });
        await nav(page, 'Replay center').click();
        const card = page.getByTestId('held-card').filter({ hasText: 'Contoso ERP' });
        await expect(card).toContainText(/Held events\s*\d+/, { timeout: 60_000 });
        await page.screenshot({ path: out(`m4-04-replay-held-${theme}`), fullPage: true });
        await card.getByRole('button', { name: 'Resume…' }).click();
        const guide = page.getByRole('dialog', { name: /Resume/ });
        await guide.getByRole('button', { name: /Send test|Test again/ }).click(); // "Test again" when the dark run's test is still fresh
        await expect(guide.getByText('Test passed')).toBeVisible();
        await page.screenshot({ path: out(`m4-05-resume-guide-${theme}`) });
        await guide.getByRole('button', { name: 'Resume endpoint' }).click();
        await page.getByRole('tab', { name: /Jobs/ }).click();
        await expect(page.getByTestId('job-card').first()).toBeVisible();
        await page.screenshot({ path: out(`m4-06-jobs-${theme}`), fullPage: true });

        await page.getByRole('button', { name: 'New replay job' }).click();
        const dialog = page.getByRole('dialog', { name: 'New replay job' });
        await dialog.getByRole('checkbox', { name: 'Dead events' }).uncheck();
        await dialog.getByRole('checkbox', { name: 'Events already delivered' }).check();
        await dialog.getByRole('button', { name: /Dry run/ }).click();
        await expect(dialog.getByTestId('dry-run')).toBeVisible();
        await page.screenshot({ path: out(`m4-07-replay-dry-run-${theme}`) });
      } finally {
        await releaseSeeded(page, request);
      }
    });

    test('settings screens and the bell', async ({ page }) => {
      await signInReady(page, 'Contoso Ltd', 'owner@contoso.test', 'Olena');
      for (const [link, heading, file] of [
        ['Alerts', 'Alerts', 'm4-08-alerts'], ['API keys & IPs', 'API keys & IPs', 'm4-09-api-keys'], ['Audit log', 'Audit log', 'm4-10-audit'], ['Team & roles', 'Team & roles', 'm4-11-team'],
      ] as const) {
        await nav(page, link).click();
        await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
        await page.waitForLoadState('networkidle');
        await page.screenshot({ path: out(`${file}-${theme}`), fullPage: true });
      }
      await page.getByRole('button', { name: /^Notifications, \d+ unread$/ }).click();
      await page.screenshot({ path: out(`m4-12-bell-${theme}`) });
    });

    test('payload versions and the receiving systems', async ({ page, request }) => {
      test.setTimeout(240_000);
      await signInReady(page, 'Contoso Ltd', 'owner@contoso.test', 'Olena');
      await page.goto(`/endpoints/${SEEDED_ENDPOINT_ID}/events`);
      await expect(page.getByRole('heading', { level: 1, name: 'Contoso ERP' })).toBeVisible();
      await page.getByRole('button', { name: 'Compare versions…' }).click();
      const dialog = page.getByRole('dialog', { name: 'Compare payload versions' });
      await dialog.getByLabel('Compare with').selectOption('2027-02-01');
      await expect(dialog.getByTestId('version-diff')).toContainText('total.amount');
      await page.screenshot({ path: out(`m5-01-versions-${theme}`) });
      await page.keyboard.press('Escape');

      // the big files and the identity checks, seen from the receiving system
      await request.post(`${SIMULATOR}/simulator/exports`, { data: { tenant: 'contoso', sizeMegabytes: 50 } });
      await request.post(`${SIMULATOR}/simulator/kyc`, { data: { tenant: 'contoso', count: 3 } });
      await page.emulateMedia({ colorScheme: theme }); // the receiving systems follow the system colour scheme
      await page.goto('http://localhost:5174');
      await page.getByRole('tab', { name: 'Files & identity checks' }).click();
      await expect(page.getByRole('table', { name: 'Files' })).toContainText('verified', { timeout: 120_000 });
      await expect(page.getByRole('table', { name: 'Identity checks' })).toContainText('approved', { timeout: 60_000 });
      await page.screenshot({ path: out(`m5-02-receiver-files-${theme}`), fullPage: true });
      await page.getByRole('tab', { name: 'Inbox' }).click();
      await expect(page.getByRole('table', { name: 'Inbox' }).getByRole('row').nth(1)).toBeVisible();
      await page.screenshot({ path: out(`m5-03-receiver-inbox-${theme}`), fullPage: true });
      await page.getByRole('tab', { name: 'Break things & trust' }).click();
      await page.screenshot({ path: out(`m5-04-receiver-chaos-${theme}`), fullPage: true });

      await page.goto('http://localhost:5400');
      await expect(page.getByRole('heading', { level: 1, name: 'InvoiceHub simulator' })).toBeVisible();
      await page.screenshot({ path: out(`m5-05-simulator-${theme}`), fullPage: true });
    });
  });
}
