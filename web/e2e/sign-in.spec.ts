import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { DEMO_PASSWORD, chooseTenantAndContinue, expectSignedIn, openAccess, signInAs, signInOnKeycloak } from './helpers';

// M1 exit criteria, in a real browser against the real stack.

test('the owner of Contoso picks the tenant, signs in on Keycloak and lands in the portal', async ({ page }) => {
  await signInAs(page, 'Contoso Ltd', 'owner@contoso.test');

  await expectSignedIn(page, 'Olena');
  await expect(page.locator('.topbar')).toContainText('Contoso Ltd');
  await expect(page.locator('.topbar')).toContainText('owner');
  await openAccess(page, 'Olena');
  await expect(page.getByText('Reveal and rotate secrets')).toBeVisible();

  // the session survives a reload (kept for the tab, in session storage)
  await page.reload();
  await expect(page.getByRole('heading', { name: /Welcome, Olena/ })).toBeVisible();

  // signing out returns to the first screen and a new visit needs credentials again
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/select-tenant$/);
  await page.goto('/');
  await expect(page).toHaveURL(/\/select-tenant$/);
});

test('picking Fabrikam and signing in with a Contoso account is refused with a clear message', async ({ page }) => {
  await signInAs(page, 'Fabrikam Inc', 'owner@contoso.test');

  await expect(page.getByRole('heading', { name: /not your organisation/i })).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Contoso Ltd');
  await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toHaveCount(0);

  await page.getByRole('button', { name: /sign out and choose again/i }).click();
  await expect(page).toHaveURL(/\/select-tenant$/);
});

test('the identity provider page says which organisation you are signing in to, and links back', async ({ page }) => {
  // the custom Keycloak login theme reads the organisation the portal remembered (display only, grants nothing)
  await chooseTenantAndContinue(page, 'Fabrikam Inc');
  await page.waitForURL(/\/realms\/invoicehub\/protocol\/openid-connect\/auth/);

  const banner = page.locator('.ih-tenant-banner');
  await expect(banner).toContainText('Signing in to Fabrikam Inc');
  await expect(banner.getByRole('link', { name: 'Choose again' })).toHaveAttribute('href', /localhost:5173\/select-tenant$/);

  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.help} -> ${v.nodes.map((n) => n.target.join(' ') + ' ' + (n.any[0]?.message ?? '')).join(' | ')}`)).toEqual([]);

  await banner.getByRole('link', { name: 'Choose again' }).click();
  await expect(page).toHaveURL(/\/select-tenant$/);
});

test('a wrong password stays on the identity provider page and gives no access', async ({ page }) => {
  await chooseTenantAndContinue(page, 'Contoso Ltd');
  await signInOnKeycloak(page, 'owner@contoso.test', 'definitely-wrong-password');
  await expect(page.locator('#input-error, .pf-m-error, [id*=error]').first()).toBeVisible();
  await expect(page).toHaveURL(/localhost:8085/);
});

test('a viewer of Fabrikam sees only read permissions', async ({ page }) => {
  await signInAs(page, 'Fabrikam Inc', 'viewer@fabrikam.test');
  await expectSignedIn(page, 'Oksana');
  await openAccess(page, 'Oksana');
  const allowed = page.locator('ul.perm-list li.yes');
  await expect(allowed).toHaveCount(1);
  await expect(allowed).toContainText('View endpoints and metrics');
});

test('a new organisation registers itself and its owner signs in', async ({ page }) => {
  const suffix = Date.now().toString(36);
  const name = `Northwind ${suffix}`;
  const email = `nancy@${suffix}.test`;

  await page.goto('/select-tenant');
  await page.getByRole('link', { name: /register your organisation/i }).click();

  await page.getByLabel('Organisation name').fill(name);
  await expect(page.getByLabel('Organisation address')).toHaveValue(`northwind-${suffix}`);
  await page.getByLabel('First name').fill('Nancy');
  await page.getByLabel('Last name').fill('Davolio');
  await page.getByLabel('Work email').fill(email);
  await page.getByLabel('Password', { exact: true }).fill('Str0ng!Passw0rd');
  await page.getByLabel('Repeat the password').fill('Str0ng!Passw0rd');
  await page.getByRole('button', { name: /create organisation/i }).click();

  await expect(page.getByRole('heading', { name: /your organisation is ready/i })).toBeVisible();
  await page.getByRole('button', { name: new RegExp(`sign in to ${name}`, 'i') }).click();
  await signInOnKeycloak(page, email, 'Str0ng!Passw0rd');

  await expectSignedIn(page, 'Nancy');
  await expect(page.locator('.topbar')).toContainText(name);
  await expect(page.locator('.topbar')).toContainText('owner');
});

test('registration reports mistakes next to the field and focuses the first one', async ({ page }) => {
  await page.goto('/register');
  await page.getByRole('button', { name: /create organisation/i }).click();
  await expect(page.getByLabel('Organisation name')).toBeFocused();
  await expect(page.locator('#d-password')).toContainText('at least 12 characters');
  await expect(page.locator('#d-slug')).toContainText('lowercase');
});

test('registering a taken address is reported on the address field', async ({ page }) => {
  await page.goto('/register');
  await page.getByLabel('Organisation name').fill('Another Contoso');
  await page.getByLabel('Organisation address').fill('contoso');
  await page.getByLabel('First name').fill('A');
  await page.getByLabel('Last name').fill('B');
  await page.getByLabel('Work email').fill(`someone${Date.now()}@elsewhere.test`);
  await page.getByLabel('Password', { exact: true }).fill('Str0ng!Passw0rd');
  await page.getByLabel('Repeat the password').fill('Str0ng!Passw0rd');
  await page.getByRole('button', { name: /create organisation/i }).click();
  await expect(page.locator('#d-slug')).toContainText('already taken');
});

for (const theme of ['dark', 'light'] as const) {
  test.describe(`accessibility, ${theme} theme (WCAG 2.1 AA, automated checks)`, () => {
    const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

    test.beforeEach(async ({ page }) => {
      await page.addInitScript((t) => localStorage.setItem('ih.theme', t), theme);
    });

    test('tenant picker', async ({ page }) => {
      await page.goto('/select-tenant');
      await expect(page.getByRole('radio').first()).toBeVisible();
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      expect((await new AxeBuilder({ page }).withTags(tags).analyze()).violations).toEqual([]);
    });

    test('tenant picker with a choice made and the demo accounts open', async ({ page }) => {
      await page.goto('/select-tenant');
      await page.getByRole('radio', { name: /Contoso/ }).check();
      await page.locator('details.demo-hint summary').click();
      expect((await new AxeBuilder({ page }).withTags(tags).analyze()).violations).toEqual([]);
    });

    test('registration form with errors shown', async ({ page }) => {
      await page.goto('/register');
      await page.getByRole('button', { name: /create organisation/i }).click();
      expect((await new AxeBuilder({ page }).withTags(tags).analyze()).violations).toEqual([]);
    });

    test('signed-in overview', async ({ page }) => {
      await signInAs(page, 'Contoso Ltd', 'developer@contoso.test', DEMO_PASSWORD);
      await expectSignedIn(page, 'Dmytro');
      expect((await new AxeBuilder({ page }).withTags(tags).analyze()).violations).toEqual([]);
    });

    test('wrong-organisation stop screen', async ({ page }) => {
      await signInAs(page, 'Fabrikam Inc', 'owner@contoso.test');
      await expect(page.getByRole('heading', { name: /not your organisation/i })).toBeVisible();
      expect((await new AxeBuilder({ page }).withTags(tags).analyze()).violations).toEqual([]);
    });
  });
}

test('the theme switch flips the theme and remembers the choice', async ({ page }) => {
  await page.addInitScript(() => { if (!localStorage.getItem('ih.theme')) localStorage.setItem('ih.theme', 'dark'); });
  await page.goto('/select-tenant');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.getByRole('button', { name: /switch to light theme/i }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});

test('the whole sign-in page works with the keyboard only', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ih.theme', 'dark'));
  await page.goto('/select-tenant');
  await expect(page.getByRole('radio').first()).toBeVisible();
  await page.keyboard.press('Tab'); // skip link
  await expect(page.getByRole('link', { name: /skip to content/i })).toBeFocused();
  await page.getByRole('radio', { name: /Contoso/ }).focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('radio', { name: /Contoso/ })).toBeChecked();
  await expect(page.getByRole('button', { name: /continue to sign in/i })).toBeEnabled();
});

test('step-up: prompt=login with max_age=0 forces a fresh password entry and a newer auth_time (used later to reveal secrets)', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ih.theme', 'dark'));
  await signInAs(page, 'Contoso Ltd', 'developer@contoso.test');
  await expectSignedIn(page, 'Dmytro');

  // read the auth_time claim of the access token the portal holds
  const authTime = () => page.evaluate(() => {
    const key = Object.keys(sessionStorage).find((k) => k.startsWith('oidc.user:'))!;
    const token = JSON.parse(sessionStorage.getItem(key)!).access_token as string;
    return JSON.parse(atob(token.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/'))).auth_time as number;
  });
  const first = await authTime();
  expect(first).toBeGreaterThan(0);

  await page.waitForTimeout(1500); // make sure a second passes so the new auth_time is distinguishable

  // ask the identity provider to re-authenticate: an existing SSO session must NOT be enough
  // (the redirect destroys the page context, so do not wait for this call to return)
  void page.evaluate(async () => {
    const { userManager } = await import('/src/auth/oidc.ts');
    await userManager.signinRedirect({ prompt: 'login', max_age: 0 });
  }).catch(() => undefined);
  // Keycloak asks for the password again even though the SSO session is still valid: the account is pre-filled and read-only
  await page.waitForURL(/openid-connect\/auth/);
  await expect(page.getByRole('textbox').first()).toHaveValue('developer@contoso.test');
  await page.locator('#password').fill(DEMO_PASSWORD);
  await page.locator('#kc-login').click();
  await expectSignedIn(page, 'Dmytro');

  expect(await authTime()).toBeGreaterThan(first);
});
