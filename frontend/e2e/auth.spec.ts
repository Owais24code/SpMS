import { expect, test } from '@playwright/test';
import { ids, key, staff } from './support';

test.describe('staff sign-in', () => {
  test('a dev login signs in with the scopes the API resolved', async ({ page }) => {
    await page.goto('/sign-in');
    await page.locator('label.role', { hasText: 'Front desk' }).click();
    await page.getByRole('button', { name: 'Enter workspace' }).click();
    await expect(page).toHaveURL(/\/app$/);
    await expect(page.locator('.account__name')).toHaveText('Dana');
    await expect(page.locator('.account__role')).toHaveText('Front desk');
    // Front desk has no spa.reconcile: the screen is not in the navigation.
    await expect(page.getByRole('link', { name: 'Reconciliation' })).toHaveCount(0);
  });

  test('a reload keeps the operator signed in', async ({ page }) => {
    await page.goto('/sign-in');
    await page.locator('label.role', { hasText: 'Finance' }).click();
    await page.getByRole('button', { name: 'Enter workspace' }).click();
    await expect(page.locator('.account__role')).toHaveText('Finance');
    await page.reload();
    await expect(page).toHaveURL(/\/app$/);
    await expect(page.locator('.account__role')).toHaveText('Finance');
  });

  test('a manager with two properties can switch between them', async ({ page }) => {
    await page.goto('/sign-in');
    await page.locator('label.role', { hasText: 'Spa manager' }).click();
    await page.getByRole('button', { name: 'Enter workspace' }).click();
    await page.locator('.account__btn').click();
    await expect(page.locator('.menu__head')).toHaveText('Riverside Spa');

    const next = page.waitForRequest((r) => r.url().includes('/appointments') && r.headers()['x-spa-property'] === ids.harbour);
    await page.getByRole('menuitemradio', { name: 'Harbour Spa' }).click();
    await next;
    await page.locator('.account__btn').click();
    await expect(page.locator('.menu__head')).toHaveText('Harbour Spa');
  });

  test('the workspace sends the dev login, never asserted scopes', async ({ page }) => {
    await page.goto('/sign-in');
    await page.locator('label.role', { hasText: 'Scheduler' }).click();
    const board = page.waitForRequest((r) => r.url().includes('/appointments'));
    await page.getByRole('button', { name: 'Enter workspace' }).click();
    const h = (await board).headers();
    expect(h['x-spa-login']).toBe('riley');
    expect(h['x-spa-scopes']).toBeUndefined();
  });
});

test.describe('guest magic link', () => {
  test('a link signs the guest in once; a second use is refused', async ({ page }) => {
    const dana = await staff('dana');
    const email = `ava+${Date.now()}@example.com`;
    const cp = await dana.post(`/guests/${ids.guestAva}/contact-points`, {
      headers: { 'Idempotency-Key': key() },
      data: { contactType: 'Email', value: email, isPrimary: false, verifiedInPerson: true },
    });
    expect(cp.status(), await cp.text()).toBe(201);
    const { contactPointId } = await cp.json();

    const link = await dana.post(`/guests/${ids.guestAva}/magic-links`, {
      headers: { 'Idempotency-Key': key() },
      data: { contactPointId, purpose: 'SignIn' },
    });
    expect(link.status(), await link.text()).toBe(202);
    const { devToken } = await link.json();
    expect(devToken).toBeTruthy();

    await page.goto(`/g/${devToken}`);
    await expect(page).toHaveURL(/\/guest$/);
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Hello');
    await expect(page.locator('.list')).toContainText('Email');

    // Single use: the same link again is spent.
    await page.goto(`/g/${devToken}`);
    await expect(page.getByRole('heading', { name: 'This link has expired' })).toBeVisible();
  });

  test('asking for a link answers the same for an unknown address', async ({ page }) => {
    await page.goto('/guest/sign-in');
    await page.getByLabel('Email').fill('nobody-here@example.com');
    await page.getByRole('button', { name: 'Send link' }).click();
    await expect(page.getByRole('status')).toContainText('If that address is on file');
  });
});
