import path from 'node:path';

import { expect, test } from '../support/fixtures';
import { signInToJellyfin } from '../support/webClient';

/**
 * Pictures of the page for people to look at, not a test. Runs only when asked:
 *
 *   SUBTITLE_MINER_E2E_SCREENS=<folder> dev/e2e.sh screens
 */
const folder = process.env.SUBTITLE_MINER_E2E_SCREENS;

test.skip(folder === undefined || folder === '', 'Set SUBTITLE_MINER_E2E_SCREENS to a folder.');

test('the main screen', async ({ page, jellyfin, player }, info) => {
  const tv = player(jellyfin.viewer, 'Living room', 'mpv');
  await tv.play(jellyfin.media.episode1.id, { positionMs: 4_500, paused: true });
  await signInToJellyfin(page, jellyfin.viewer);
  await page.goto('/SubtitleMiner/');
  await page.getByTestId('pick-second').selectOption('s1');
  await expect(page.locator('.line .second').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-lines.png`) });

  await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
  await page.getByRole('checkbox', { name: 'Line at 00:04' }).click();
  await expect(page.getByTestId('preview-picture').locator('img')).toBeVisible();
  await expect(page.getByTestId('play-sound')).toBeEnabled();
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-selected.png`) });

  await page.getByTestId('account').click();
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-account.png`) });
  await page.keyboard.press('Escape');

  await page.getByTestId('open-settings').click();
  await page.getByTestId('where-device').check();
  await page.screenshot({
    path: path.join(folder!, `${info.project.name}-settings.png`),
    fullPage: true,
  });
  await page.getByTestId('settings-back').click();

  await tv.stop();
  await page.reload();
  await expect(page.getByTestId('title')).toBeVisible();
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-watched.png`) });
});
