import { expect, test } from '@playwright/test';
import { U, key, lateTodayUtc, staff } from './support';

const signIn = async (page: import('@playwright/test').Page, role: string) => {
  await page.goto('/sign-in');
  await page.locator('label.role', { hasText: role }).click();
  await page.getByRole('button', { name: 'Enter workspace' }).click();
  await expect(page).toHaveURL(/\/app$/);
};

const stamp = () => Date.now().toString(36);

test('the desk takes a deposit, bills the guest, waits out an unconfirmed card, and finance approves the refund', async ({ page, browser }) => {
  const dana = await staff('dana');
  const morgan = await staff('morgan');
  const g = (await (await dana.post('/guests', { data: { legalFirstName: `Till${stamp()}`, legalLastName: 'Moss', contactsVerifiedInPerson: true } })).json()).guest;
  const appt = await morgan.post('/appointments', {
    headers: { 'Idempotency-Key': key() },
    data: { guestId: g.guestId, guestAlias: g.displayAlias, serviceId: U(404), startUtc: lateTodayUtc() },
  });
  expect(appt.status(), await appt.text()).toBe(201);

  await signIn(page, 'Front desk');
  await page.goto('/app/checkout');
  const row = page.getByTestId('checkout-arrivals').locator('li', { hasText: g.displayAlias }).filter({ hasText: 'Hot stone 60' });
  await expect(row).toContainText('Deposit due');
  await row.getByRole('button', { name: 'Take deposit' }).click();
  await expect(page.getByText('approved').first()).toBeVisible();
  await expect(row).toContainText('Deposit paid');

  await row.getByRole('button', { name: 'Open bill' }).click();
  await expect(page.getByTestId('order-status')).toHaveText('Draft');
  await expect(page.getByTestId('order-total')).toHaveText('$146.98');
  await page.getByLabel('Tip in dollars').fill('10');
  await page.getByRole('button', { name: 'Add tip' }).click();
  await expect(page.getByTestId('order-total')).toHaveText('$156.98');
  await page.getByRole('button', { name: 'Place order' }).click();
  await expect(page.getByTestId('order-status')).toHaveText('PartiallyPaid');
  await expect(page.getByTestId('order-balance')).toHaveText('$89.48');

  await page.getByLabel('Card', { exact: true }).selectOption('tok_timeout');
  await page.getByTestId('pay').click();
  await expect(page.getByTestId('payment-pending')).toBeVisible();
  await expect(page.getByTestId('pay')).toHaveCount(0);
  await page.getByRole('button', { name: 'Ask the provider' }).click();
  await expect(page.getByTestId('order-status')).toHaveText('Paid');
  await expect(page.getByTestId('receipt')).toHaveText(/^RCT/);

  await page.getByRole('button', { name: 'Refund…' }).last().click();
  await page.getByLabel('Refund in dollars').fill('5');
  await page.getByRole('button', { name: 'Request refund' }).click();
  await expect(page.getByText('Refund requested')).toBeVisible();

  const finance = await browser.newPage();
  await signIn(finance, 'Finance');
  await finance.goto('/app/reconciliation');
  await expect(finance.getByTestId('tender-totals')).toContainText('Card');
  const refund = finance.getByTestId('refunds').locator('tr', { hasText: '$5.00' }).first();
  await refund.getByRole('button', { name: 'Approve' }).click();
  await expect(finance.getByText('Refund approved')).toBeVisible();
});
