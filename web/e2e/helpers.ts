import { expect, type APIRequestContext, type Page } from '@playwright/test';

export const DEMO_PASSWORD = 'Passw0rd!2026';

/** Where the demo's other applications listen (fixed ports of the AppHost). */
export const RECEIVER = 'http://localhost:5201';
export const SIMULATOR = 'http://localhost:5400';

/** The endpoint every demo tenant "Contoso" starts with. */
export const SEEDED_ENDPOINT_ID = '0198a000-0000-7000-8000-0000000001e1';

/** Step one on the portal: choose the organisation, then continue to the identity provider. */
export async function chooseTenantAndContinue(page: Page, tenantName: string) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/select-tenant$/);
  await page.getByRole('radio', { name: new RegExp(tenantName) }).check();
  await page.getByRole('button', { name: /continue to sign in/i }).click();
}

/** Step two on Keycloak's own page: this is real credential entry, not a mock. */
export async function signInOnKeycloak(page: Page, email: string, password: string) {
  await page.waitForURL(/\/realms\/invoicehub\/protocol\/openid-connect\/auth/);
  await page.locator('#username').fill(email);
  await page.locator('#password').fill(password);
  await page.locator('#kc-login').click();
}

export async function signInAs(page: Page, tenantName: string, email: string, password = DEMO_PASSWORD) {
  await chooseTenantAndContinue(page, tenantName);
  await signInOnKeycloak(page, email, password);
}

/**
 * The step-up sign-in: an action that needs a fresh sign-in opens a small window on Keycloak, where the person enters their password again.
 * `trigger` is the click that starts it; this fills the password in the pop-up and waits until the pop-up has closed itself.
 */
export async function stepUp(page: Page, trigger: () => Promise<void>, password = DEMO_PASSWORD) {
  const popupPromise = page.waitForEvent('popup');
  await trigger();
  const popup = await popupPromise;
  await popup.waitForLoadState('domcontentloaded');
  await popup.locator('#password').fill(password);
  await popup.locator('#kc-login').click();
  await expect.poll(() => popup.isClosed(), { timeout: 20_000, message: 'the sign-in window should close itself' }).toBe(true);
}

/** What a tenant does with the secret: pastes it into their own application's configuration (here, the demo receiver). */
export async function trustInReceiver(request: APIRequestContext, secrets: string[], jwksUrl: string | null = null) {
  const response = await request.put(`${RECEIVER}/api/trust`, { data: { secrets, jwksUrl } });
  expect(response.ok()).toBe(true);
}

/** Business activity for the demo tenant, then waits until the receiver has taken it all in. */
export async function produceEvents(request: APIRequestContext, invoices = 3) {
  const before = await receivedCount(request);
  const response = await request.post(`${SIMULATOR}/simulator/invoices`, { data: { tenant: 'contoso', count: invoices, perSecond: 10, payRatio: 1, overdueRatio: 0, failedPaymentRatio: 0 } });
  expect(response.status()).toBe(202);
  await expect.poll(async () => (await receivedCount(request)) - before, { timeout: 60_000, message: 'events should reach the receiver' }).toBeGreaterThanOrEqual(invoices * 3);
}

async function receivedCount(request: APIRequestContext): Promise<number> {
  const stats = await (await request.get(`${RECEIVER}/api/stats`)).json();
  return stats.counts.received as number;
}

/** The landing page after sign-in is the Webhooks overview; the topbar names the person. */
export async function expectSignedIn(page: Page, firstName: string) {
  await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toBeVisible();
  await expect(page.locator('.topbar')).toContainText(firstName);
}

/** The "Your access" page: who you are and what your role allows. */
export async function openAccess(page: Page, firstName: string) {
  await page.getByRole('link', { name: /your access/i }).click();
  await expect(page.getByRole('heading', { name: new RegExp(`Welcome, ${firstName}`) })).toBeVisible();
}

/** Signs in and waits until the portal has really loaded (navigating away before that would drop the session). */
export async function signInReady(page: Page, tenantName: string, email: string, firstName: string) {
  await signInAs(page, tenantName, email);
  await expectSignedIn(page, firstName);
}

/** The tabs of an endpoint workspace (scoped, because the go-live checklist links to them too). */
export const tab = (page: Page, name: string) => page.getByRole('navigation', { name: 'Endpoint sections' }).getByRole('link', { name });

/** Deleting an endpoint is an administrator's right (the developer in the journey may not): a second person, a second browser. */
export async function deleteAsAdmin(browser: import('@playwright/test').Browser, name: string) {
  const context = await browser.newContext();
  const admin = await context.newPage();
  try {
    await signInReady(admin, 'Contoso Ltd', 'admin@contoso.test', 'Taras');
    const row = admin.getByRole('row').filter({ hasText: name });
    await row.getByRole('button', { name: `Delete ${name}` }).click();
    const confirm = admin.getByRole('dialog', { name: `Delete “${name}”` });
    await expect(confirm.getByRole('button', { name: 'Delete endpoint' })).toBeDisabled();
    await confirm.getByLabel(/Type .* to confirm/).fill(name);
    await confirm.getByRole('button', { name: 'Delete endpoint' }).click();
    await expect(admin.getByRole('row').filter({ hasText: name })).toHaveCount(0);
  } finally {
    await context.close();
  }
}
