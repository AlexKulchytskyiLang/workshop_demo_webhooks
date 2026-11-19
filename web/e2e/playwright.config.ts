import { defineConfig } from '@playwright/test';

// Runs against the real, already started platform (dotnet run --project src/AppHost/InvoiceHub.AppHost).
// Uses the installed Chrome so no browser download is needed.
export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.PORTAL_URL ?? 'http://localhost:5173',
    channel: 'chrome',
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  outputDir: './test-results',
});
