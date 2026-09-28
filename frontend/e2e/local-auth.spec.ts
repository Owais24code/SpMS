import { expect, test, type Page } from '@playwright/test';
import { staff } from './support';

/**
 * Local email + password accounts (the API's Auth:Local, authMode 'local').
 * The dev server is built for demo sign-in, so each test swaps the runtime
 * config for authMode 'local' — exactly what a deployment's config.js does.
 */
const API = process.env['SPMS_API_URL'] ?? 'http://127.0.0.1:5199';

const localMode = async (page: Page): Promise<void> => {
  await page.route('**/assets/config.js', (route) => route.fulfill({
    contentType: 'application/javascript',
    body: `window.__SPMS_CONFIG__ = { apiBaseUrl: '${API}', useRealApi: true, authMode: 'local' };`,
  }));
};

const uniq = (): string => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

/** Proposed by Morgan, approved by Ada: SEC-014 needs two administrators. */
const grantRole = async (staffId: string, roleCode: string): Promise<void> => {
  const morgan = await staff('morgan');
  const proposed = await morgan.post(`/staff/${staffId}/roles`, { data: { roleCode, tenantWide: true } });
  expect(proposed.status()).toBe(201);
  const p = await proposed.json();
  const ada = await staff('ada');
  expect((await ada.post(`/role-assignments/${p.assignmentId}/approve`, { headers: { 'If-Match': p.eTag } })).status()).toBe(200);
};

test('someone signs up, an administrator approves them and gives a role, and they sign in with their password', async ({ page }) => {
  const email = `signup-${uniq()}@aarfid.dev`;
  const password = `Sign-up-${uniq()}-pw`;
  await localMode(page);
  await page.goto('/register');
  await page.locator('#reg-name').fill('Nadia Newcomer');
  await page.locator('#reg-email').fill(email);
  await page.locator('#reg-password').fill(password);
  await page.locator('#reg-repeat').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/sign-in\?registered=1/);
  await expect(page.getByText('An administrator will approve your account')).toBeVisible();

  // Before approval the right password says so; nothing else is revealed.
  await page.locator('#email').fill(email);
  await page.locator('#password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('waiting for an administrator');

  const morgan = await staff('morgan');
  const pending = (await (await morgan.get('/staff/sign-ins')).json()).find((x: { email: string }) => x.email === email);
  const row = (await (await morgan.get('/staff')).json()).find((x: { staffId: string }) => x.staffId === pending.staffId);
  expect((await morgan.post(`/staff/${pending.staffId}/approve-sign-up`, { headers: { 'If-Match': row.eTag } })).status()).toBe(200);
  await grantRole(pending.staffId, 'front_desk');

  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page).toHaveURL(/\/app$/);
  await expect(page.getByText('Nadia Newcomer').first()).toBeVisible();
});

test('an administrator-issued temporary password must be replaced at first sign-in', async ({ page }) => {
  const email = `temp-${uniq()}@aarfid.dev`;
  const morgan = await staff('morgan');
  const created = await (await morgan.post('/staff', { data: { preferredName: 'Tomas Temp', bookable: false } })).json();
  const given = await morgan.post(`/staff/${created.staffId}/local-sign-in`, { headers: { 'If-Match': created.eTag }, data: { email } });
  expect(given.status()).toBe(200);
  const { temporaryPassword } = await given.json();
  await grantRole(created.staffId, 'scheduler');

  await localMode(page);
  await page.goto('/sign-in');
  await page.locator('#email').fill(email);
  await page.locator('#password').fill(temporaryPassword);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByText('You signed in with a temporary password')).toBeVisible();
  const mine = `Tomas-own-${uniq()}`;
  await page.locator('#new-password').fill(mine);
  await page.locator('#repeat-password').fill(mine);
  await page.getByRole('button', { name: 'Set password and continue' }).click();
  await expect(page).toHaveURL(/\/app$/);

  // The temporary password no longer works; the new one does.
  const again = await (await import('@playwright/test')).request.newContext({ baseURL: API });
  expect((await again.post('/auth/login', { data: { email, password: temporaryPassword } })).status()).toBe(401);
  expect((await again.post('/auth/login', { data: { email, password: mine } })).status()).toBe(200);
});
