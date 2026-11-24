import type { Page } from '@playwright/test';

import type { Account } from './server';

/** Signs in on Jellyfin's own web client, the way a user does. */
export async function signInToJellyfin(page: Page, account: Account): Promise<void> {
  await page.goto('/web/');
  await page.locator('#txtManualName').fill(account.name);
  await page.locator('#txtManualPassword').fill(account.password);
  await page.getByRole('button', { name: 'Sign In', exact: true }).click();
  await page.waitForURL(/#\/home/);
}
