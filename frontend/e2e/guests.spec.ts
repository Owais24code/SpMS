import { expect, test } from '@playwright/test';
import { SVC, U, key, lateTodayUtc, staff } from './support';

const signIn = async (page: import('@playwright/test').Page, role: string) => {
  await page.goto('/sign-in');
  await page.locator('label.role', { hasText: role }).click();
  await page.getByRole('button', { name: 'Enter workspace' }).click();
  await expect(page).toHaveURL(/\/app$/);
};

const stamp = () => Date.now().toString(36);

test('the desk creates a guest, finds them by email, and keeps an operational preference', async ({ page }) => {
  const email = `e2e.${stamp()}@example.com`;
  await signIn(page, 'Front desk');
  await page.goto('/app/guests');
  await page.getByRole('button', { name: 'New guest' }).click();
  await page.getByLabel('First name', { exact: true }).fill('Olive');
  await page.getByLabel('Last name', { exact: true }).fill('Quinn');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Create guest' }).click();
  await expect(page.getByText('Guest created')).toBeVisible();

  await page.getByLabel('Search guests').fill(email.toUpperCase());
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  const hit = page.locator('.hit', { hasText: 'Olive Q.' });
  await expect(hit).toBeVisible();
  await expect(hit).toContainText('e***@example.com');
  await hit.click();
  await page.locator('#pref-pressure').selectOption('Firm');
  await page.getByRole('button', { name: 'Save profile' }).click();
  await expect(page.getByText('Profile saved')).toBeVisible();
});

test('the desk books a guest into an open slot through the API', async ({ page }) => {
  const dana = await staff('dana');
  const made = await dana.post('/guests', { data: { legalFirstName: 'Pia', legalLastName: `Book${stamp()}`, contactsVerifiedInPerson: true } });
  expect(made.status(), await made.text()).toBe(201);
  const g = (await made.json()).guest;

  await signIn(page, 'Front desk');
  await page.goto('/app/booking');
  await page.getByLabel('Find guest').fill(g.publicQueueId);
  await page.getByRole('button', { name: 'Find' }).click();
  await page.getByRole('button', { name: new RegExp(g.publicQueueId) }).click();
  await page.locator('#b-svc').selectOption({ label: 'Facial 45' });
  const tomorrow = new Date(Date.now() + 86400_000).toISOString().slice(0, 10);
  await page.locator('#b-day').fill(tomorrow);
  await page.locator('.tslot').first().click();
  await page.getByRole('button', { name: 'Book', exact: true }).click();
  await expect(page.getByText(/Booked · AAR\d{9}/)).toBeVisible();
});

test('a guest completes intake from their link, and the assigned provider reads the summary and acknowledges it', async ({ page, browser }) => {
  const dana = await staff('dana');
  const morgan = await staff('morgan');
  const email = `intake.${stamp()}@example.com`;
  const g = (await (await dana.post('/guests', { data: { legalFirstName: `Rue${stamp()}`, legalLastName: 'Stone', email, contactsVerifiedInPerson: true } })).json()).guest;
  const appt = await morgan.post('/appointments', {
    headers: { 'Idempotency-Key': key() },
    data: { guestId: g.guestId, serviceId: U(401), startUtc: lateTodayUtc(), providerId: U(601), reason: 'e2e: Lena covers the late slot' },
  });
  expect(appt.status(), await appt.text()).toBe(201);
  const link = await (await dana.post(`/guests/${g.guestId}/magic-links`, { data: { contactPointId: g.contacts[0].contactPointId, purpose: 'CompleteIntake' } })).json();

  await page.goto(`/g/${link.devToken}`);
  await expect(page).toHaveURL(/\/guest$/);
  const form = page.locator('section.intake');
  await expect(form).toContainText('Deep tissue 90');
  await form.getByLabel(/pregnant/).selectOption('false');
  await form.getByLabel(/heart condition/).selectOption('false');
  await form.getByLabel(/Allergies/).fill('almond oil');
  await form.getByLabel(/Emergency contact/).fill('Sol 555-0101');
  await form.getByRole('button', { name: 'Submit' }).click();
  await expect(form.getByRole('alert')).toContainText('I confirm these answers are accurate');
  await form.getByLabel(/I confirm/).selectOption('true');
  await form.getByRole('button', { name: 'Submit' }).click();
  await expect(form.getByText('Thank you')).toBeVisible();

  const tablet = await browser.newPage();
  await signIn(tablet, 'Provider');
  await tablet.goto('/app/treatments');
  await tablet.locator('.hit', { hasText: 'Deep tissue 90' }).filter({ hasText: g.displayAlias }).first().click();
  await expect(tablet.getByText('almond oil', { exact: true })).toBeVisible();
  await tablet.getByRole('button', { name: 'I have read this' }).click();
  await expect(tablet.getByText(/The form is locked/)).toBeVisible();
  await tablet.getByLabel('New note').fill('Light pressure; avoided almond oil.');
  await tablet.getByRole('button', { name: 'Save note' }).click();
  await expect(tablet.getByText('Light pressure; avoided almond oil.')).toBeVisible();
});
