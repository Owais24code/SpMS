import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end against the real API.
 *
 * Needs the API running in Development on :5199 with the dev seed (see the
 * README); the dev server is started here. PW_CHROMIUM points at a
 * preinstalled browser when `npx playwright install` is not an option.
 */
const executablePath = process.env['PW_CHROMIUM'] || undefined;

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  fullyParallel: false,
  workers: 1,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env['SPMS_WEB_URL'] ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'], launchOptions: { executablePath } } },
  ],
  webServer: process.env['SPMS_WEB_URL'] ? undefined : {
    command: 'npx ng serve --configuration development --port 4200',
    url: 'http://localhost:4200',
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
