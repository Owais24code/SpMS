import { expect, test } from '@playwright/test';
import { U, key, riversideAt, staff } from './support';

const signIn = async (page: import('@playwright/test').Page, role: string) => {
  await page.goto('/sign-in');
  await page.locator('label.role', { hasText: role }).click();
  await page.getByRole('button', { name: 'Enter workspace' }).click();
  await expect(page).toHaveURL(/\/app$/);
};

test('the header search finds a booking by confirmation number and opens it', async ({ page }) => {
  await signIn(page, 'Front desk');
  await page.getByLabel('Search the workspace').fill('AAR000000002');
  const results = page.getByTestId('search-results');
  await expect(results).toContainText('AAR000000002');
  await expect(results).toContainText('Facial 45');
  await page.getByLabel('Search the workspace').fill('Hot stone');
  await expect(results).toContainText('Hot stone 60');
});

test('a booking made at another desk appears on the open board without a reload', async ({ page }) => {
  const dana = await staff('dana');
  const morgan = await staff('morgan');
  const g = (await (await dana.post('/guests', { data: { legalFirstName: 'Remy', legalLastName: 'Vale', contactsVerifiedInPerson: true } })).json()).guest;

  await signIn(page, 'Scheduler');
  await page.goto('/app/schedule');
  await expect(page.getByTestId('board-live')).toBeVisible();
  const slots = page.locator('.slot__label');
  await expect(slots.first()).toBeVisible();
  const before = await slots.count();
  const made = await morgan.post('/appointments', { headers: { 'Idempotency-Key': key() },
    data: { guestId: g.guestId, guestAlias: g.displayAlias, serviceId: U(403), startUtc: riversideAt(19, Math.floor(Math.random() * 30)) } });
  expect(made.status(), await made.text()).toBe(201);
  // No reload: the stream's change event makes the board fetch again.
  await expect(slots).toHaveCount(before + 1, { timeout: 10_000 });
});
