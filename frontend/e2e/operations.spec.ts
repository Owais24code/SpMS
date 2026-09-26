import { expect, test } from '@playwright/test';
import { SVC, U, farFutureUtc, key, lateTodayUtc, staff } from './support';

const signIn = async (page: import('@playwright/test').Page, role: string) => {
  await page.goto('/sign-in');
  await page.locator('label.role', { hasText: role }).click();
  await page.getByRole('button', { name: 'Enter workspace' }).click();
  await expect(page).toHaveURL(/\/app$/);
};

test('the desk checks a guest in against the server, and the row turns Checked in', async ({ page }) => {
  const morgan = await staff('morgan');
  const alias = `E2E ${Date.now() % 100000}`;
  const guest = (await (await (await staff('dana')).post('/guests', {
    data: { legalFirstName: `Arr${Date.now().toString(36)}`, legalLastName: 'Ival' } })).json()).guest;
  const made = await morgan.post('/appointments', {
    headers: { 'Idempotency-Key': key() },
    data: { guestId: guest.guestId, guestAlias: alias, serviceId: SVC.facial, startUtc: lateTodayUtc() },
  });
  expect(made.status(), await made.text()).toBe(201);
  const a = await made.json();

  await signIn(page, 'Front desk');
  await page.goto('/app/check-in');
  const row = page.locator('tr', { hasText: a.guestAlias }).filter({ hasText: 'Facial 45' }).last();
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Check in' }).click();
  await expect(row.getByText('Checked in')).toBeVisible();

  const after = await (await morgan.get(`/appointments/${a.appointmentId}`)).json();
  expect(after.status).toBe('CheckedIn');
});

test('housekeeping passes a turnover, and the room is ready again', async ({ page }) => {
  const morgan = await staff('morgan');
  const made = await morgan.post('/appointments', {
    headers: { 'Idempotency-Key': key() },
    data: { guestId: U(2039), guestAlias: 'Turnover guest', serviceId: SVC.swedish, startUtc: farFutureUtc(), roomId: U(1040) },
  });
  expect(made.status(), await made.text()).toBe(201);
  let a = await made.json();
  for (const to of ['CheckedIn', 'Ready', 'InService', 'Completed']) {
    const r = await morgan.post(`/appointments/${a.appointmentId}/transitions`, { data: { to }, headers: { 'If-Match': a.eTag } });
    expect(r.status(), await r.text()).toBe(200);
    a = await r.json();
  }

  await signIn(page, 'Housekeeping');
  await page.goto('/app/turnover');
  const hana = await staff('hana');
  const open = (await (await hana.get('/turnaround')).json()).items as { appointmentId: string; roomName: string }[];
  const before = open.length;
  const mine = open.find((t) => t.appointmentId === a.appointmentId)!;
  expect(mine).toBeTruthy();
  const row = page.locator('tr', { hasText: 'Turnover' }).filter({ hasText: mine.roomName }).first();
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Pass' }).click();
  await expect(page.getByText(`${mine.roomName} is ready`, { exact: true })).toBeVisible();
  const afterCount = (await (await hana.get('/turnaround')).json()).items.length;
  expect(afterCount).toBe(before - 1);
});

test('the desk offers a waitlisted guest a slot', async ({ page }) => {
  const dana = await staff('dana');
  const start = new Date(Date.now() + 3600_000);
  const added = await dana.post('/waitlist', {
    data: { guestId: U(705), serviceId: SVC.swedish, earliestUtc: start.toISOString(), latestUtc: new Date(start.getTime() + 4 * 3600_000).toISOString() },
  });
  expect(added.status(), await added.text()).toBe(201);
  const w = await added.json();

  await signIn(page, 'Front desk');
  await page.goto('/app/waitlist');
  const row = page.locator('tr', { hasText: w.guestAlias }).last();
  await row.getByRole('button', { name: 'Offer 30 min' }).click();
  await expect(page.getByText(`Offered to ${w.guestAlias}`)).toBeVisible();

  const after = await (await dana.get('/waitlist?status=Offered')).json();
  expect(after.items.some((x: { waitlistId: string }) => x.waitlistId === w.waitlistId)).toBe(true);
});
