import { expect, test } from '@playwright/test';

const signIn = async (page: import('@playwright/test').Page, role: string) => {
  await page.goto('/sign-in');
  await page.locator('label.role', { hasText: role }).click();
  await page.getByRole('button', { name: 'Enter workspace' }).click();
  await expect(page).toHaveURL(/\/app$/);
};

test('the inventory manager receives stock and it shows in the balance', async ({ page }) => {
  await signIn(page, 'Inventory manager');
  await page.goto('/app/inventory');
  const row = page.getByTestId('balances').locator('tr', { hasText: 'Massage oil' }).filter({ hasText: 'Main store' });
  await expect(row).toBeVisible();
  const before = Number((await row.locator('td.numeric').innerText()).replace(/,/g, ''));
  await page.locator('#mv-action').selectOption('Receipt');
  await page.locator('#mv-item').selectOption({ label: 'Massage oil 500 ml · OIL-500' });
  await page.locator('#mv-loc').selectOption({ label: 'Main store' });
  await page.locator('#mv-state').selectOption('Saleable');
  await page.locator('#mv-qty').fill('6');
  await page.getByTestId('post-movement').click();
  await expect(page.getByText('Posted', { exact: true })).toBeVisible();
  await expect(row.locator('td.numeric')).toHaveText(String(before + 6));
});

test('HR records a license and only its last four digits are ever shown', async ({ page }) => {
  const number = `NY-${Date.now().toString().slice(-8)}`;
  await signIn(page, 'HR & compliance');
  await page.goto('/app/staff');
  await page.getByTestId('team').getByRole('button', { name: 'Marco' }).click();
  await page.getByRole('tab', { name: 'Credentials' }).click();
  await page.getByLabel('Type', { exact: true }).fill('LMT');
  await page.getByLabel('Number', { exact: true }).fill(number);
  await page.getByRole('button', { name: 'Add credential' }).click();
  const list = page.getByTestId('credentials');
  await expect(list).toContainText(`•••• ${number.slice(-4)}`);
  await expect(page.locator('body')).not.toContainText(number);
  await list.locator('li', { hasText: number.slice(-4) }).getByRole('button', { name: 'Verify' }).click();
  await expect(list.locator('li', { hasText: number.slice(-4) })).toContainText('Verified');
});

test('a policy change is proposed by the administrator and approved by the approver, never by its author', async ({ page, browser }) => {
  const reason = `e2e cancellation ${Date.now().toString(36)}`;
  await signIn(page, 'Platform admin');
  await page.goto('/app/setup');
  await page.getByRole('tab', { name: 'Policies' }).click();
  await page.locator('#pol-key').selectOption('policy.cancellation');
  await page.getByLabel('Policy value').fill('{"noticeHours":24,"feePercent":50}');
  await page.getByLabel('Reason for the change').fill(reason);
  await page.getByRole('button', { name: 'Propose change' }).click();
  const mine = page.getByTestId('settings').locator('tr', { hasText: reason });
  await expect(mine).toContainText('Proposed');
  await expect(mine.getByRole('button', { name: 'Approve' })).toBeDisabled();

  const approver = await browser.newPage();
  await signIn(approver, 'Finance');
  await approver.goto('/app/setup');
  await approver.getByRole('tab', { name: 'Policies' }).click();
  const row = approver.getByTestId('settings').locator('tr', { hasText: reason });
  await row.getByRole('button', { name: 'Approve' }).click();
  await expect(row).toContainText('Active');
});
