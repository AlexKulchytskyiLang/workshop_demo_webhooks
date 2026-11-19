import path from 'node:path';
import { expect, test } from '@playwright/test';
import { expectSignedIn, signInAs } from './helpers';

// Not a test: captures the M1 screens for the review document. Run with SHOTS=1 (see tests/run-e2e.ps1 -Shots).
test.skip(!process.env.SHOTS, 'screenshots are only taken on request');

const out = (name: string) => path.resolve(__dirname, '..', '..', 'docs', 'screenshots', `${name}.png`);

for (const theme of ['dark', 'light'] as const) {
  test.describe(`screens, ${theme}`, () => {
    test.use({ viewport: { width: 1360, height: 820 } });
    test.beforeEach(async ({ page }) => {
      await page.addInitScript((t) => localStorage.setItem('ih.theme', t), theme);
    });

    test('tenant picker', async ({ page }) => {
      await page.goto('/select-tenant');
      await page.getByRole('radio', { name: /Contoso/ }).check();
      await page.locator('details.demo-hint summary').click();
      await page.screenshot({ path: out(`m1-01-tenant-picker-${theme}`) });
    });

    test('registration with errors', async ({ page }) => {
      await page.goto('/register');
      await page.getByLabel('Organisation name').fill('Northwind Traders');
      await page.getByRole('button', { name: /create organisation/i }).click();
      await page.screenshot({ path: out(`m1-02-register-errors-${theme}`), fullPage: true });
    });

    test('keycloak sign-in page', async ({ page }) => {
      await page.goto('/select-tenant');
      await page.getByRole('radio', { name: /Contoso/ }).check();
      await page.getByRole('button', { name: /continue to sign in/i }).click();
      await page.waitForURL(/openid-connect\/auth/);
      await page.screenshot({ path: out(`m1-03-keycloak-login-${theme}`) });
    });

    test('signed-in overview', async ({ page }) => {
      await signInAs(page, 'Contoso Ltd', 'developer@contoso.test');
      await expectSignedIn(page, 'Dmytro');
      await page.screenshot({ path: out(`m1-04-overview-${theme}`) });
    });

    test('wrong organisation', async ({ page }) => {
      await signInAs(page, 'Fabrikam Inc', 'owner@contoso.test');
      await expect(page.getByRole('heading', { name: /not your organisation/i })).toBeVisible();
      await page.screenshot({ path: out(`m1-05-wrong-organisation-${theme}`) });
    });
  });
}
