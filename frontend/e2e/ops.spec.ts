import { expect, test } from '@playwright/test';
import { U, key, lateTodayUtc, staff } from './support';

const signIn = async (page: import('@playwright/test').Page, role: string, landing = /\/app$/) => {
  await page.goto('/sign-in');
  await page.locator('label.role', { hasText: role }).click();
  await page.getByRole('button', { name: 'Enter workspace' }).click();
  await expect(page).toHaveURL(landing);
};

const stamp = () => Date.now().toString(36);

test('a guest checks themselves in at the lobby kiosk with their confirmation number and last name', async ({ page }) => {
  const dana = await staff('dana');
  const morgan = await staff('morgan');
  const last = `Lobby${stamp()}`;
  const g = (await (await dana.post('/guests', { data: { legalFirstName: 'Noor', legalLastName: last, contactsVerifiedInPerson: true } })).json()).guest;
  const appt = await morgan.post('/appointments', { headers: { 'Idempotency-Key': key() },
    data: { guestId: g.guestId, guestAlias: g.displayAlias, serviceId: U(403), startUtc: lateTodayUtc() } });
  expect(appt.status(), await appt.text()).toBe(201);
  const a = await appt.json();

  await signIn(page, 'Lobby kiosk', /\/kiosk$/);
  await page.getByLabel('Confirmation number').fill(a.confirmationNumber);
  await page.getByLabel('Last name').fill('Wrong');
  await page.getByRole('button', { name: 'Find my booking' }).click();
  await expect(page.getByRole('alert')).toContainText('front desk');
  await page.getByLabel('Last name').fill(last);
  await page.getByRole('button', { name: 'Find my booking' }).click();
  await expect(page.getByText('Facial 45')).toBeVisible();
  await page.getByRole('button', { name: 'Check me in' }).click();
  await expect(page.getByTestId('kiosk-done')).toContainText("You're checked in");
});

test('a template drafted by the administrator goes live only when the manager approves it', async ({ page, browser }) => {
  const code = `e2e-${stamp()}`;
  await signIn(page, 'Platform admin');
  await page.goto('/app/messaging');
  await page.getByLabel('Code').fill(code);
  await page.getByLabel('Channel').selectOption('Sms');
  await page.getByLabel('Template body').fill('See you soon at {{propertyName}}.');
  await page.getByRole('button', { name: 'Save draft' }).click();
  const row = page.getByTestId('templates').locator('tr', { hasText: code });
  await expect(row).toContainText('Draft');
  await expect(row.getByRole('button', { name: 'Approve' })).toBeDisabled();

  const manager = await browser.newPage();
  await signIn(manager, 'Spa manager');
  await manager.goto('/app/messaging');
  const theirs = manager.getByTestId('templates').locator('tr', { hasText: code });
  await theirs.getByRole('button', { name: 'Approve' }).click();
  await expect(theirs).toContainText('Active');
});

test('the manager runs the day\'s operations report, sees it verified, and exports it', async ({ page }) => {
  await signIn(page, 'Spa manager');
  await page.goto('/app/reports');
  await page.getByTestId('run-operations.daily').click();
  const result = page.getByTestId('report-result');
  await expect(result).toContainText('Verified');
  await expect(result).toContainText('SHA-256');
  const download = page.waitForEvent('download');
  await result.getByRole('button', { name: 'Export CSV' }).click();
  expect((await download).suggestedFilename()).toMatch(/^operations\.daily-.*\.csv$/);
});
