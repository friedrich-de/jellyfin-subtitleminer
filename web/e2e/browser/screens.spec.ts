import type { APIRequestContext } from '@playwright/test';
import path from 'node:path';

import { guessRoles } from '../../src/anki/pageSettings';
import { LAPIS_FIELDS } from '../../src/testing/ankiCollection';
import { forgetAnki, letEveryoneUseServer, saveSettings } from '../support/ankiSettings';
import {
  forgetDictionaries,
  metaDictionary,
  uploadDictionary,
  wordsDictionary,
} from '../support/dictionaries';
import { expect, type Login, signIn, test } from '../support/fixtures';
import { tapCharacter } from '../support/lines';
import { signInToJellyfin } from '../support/webClient';

/**
 * Pictures of the page for people to look at, not a test. Runs only when asked:
 *
 *   SUBTITLE_MINER_E2E_SCREENS=<folder> dev/e2e.sh screens
 */
const folder = process.env.SUBTITLE_MINER_E2E_SCREENS;

test.skip(folder === undefined || folder === '', 'Set SUBTITLE_MINER_E2E_SCREENS to a folder.');

/** Lets the viewer send cards to a pretend AnkiConnect on a server, so the pictures show the card buttons. */
async function ankiOnServer(
  request: APIRequestContext,
  admin: Login,
  viewer: Login,
  address: string,
): Promise<void> {
  await letEveryoneUseServer(request, admin, true);
  await saveSettings(request, viewer, {
    Anki: { Where: 'server', ServerAddress: address, ServerKey: '' },
    Page: {
      anki: { deck: 'Mining', noteType: 'Lapis', fields: guessRoles(LAPIS_FIELDS), tags: [] },
    },
  });
}

test.afterEach(async ({ request, jellyfin }) => {
  await forgetAnki(request, jellyfin);
});

test('the main screen', async ({ page, request, jellyfin, player, anki }, info) => {
  const server = await anki();
  await ankiOnServer(
    request,
    await signIn(request, jellyfin.admin),
    await signIn(request, jellyfin.viewer),
    server.url,
  );
  const tv = player(jellyfin.viewer, 'Living room', 'mpv');
  await tv.play(jellyfin.media.episode1.id, { positionMs: 4_500, paused: true });
  await signInToJellyfin(page, jellyfin.viewer);
  await page.goto('/SubtitleMiner/');
  await page.getByTestId('pick-second').selectOption('s1');
  await expect(page.locator('.line .second').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-lines.png`) });

  // A card Yomitan made a moment ago, for "Add to my newest card" to name.
  server.collection.makeNote({
    deck: 'Mining',
    modelName: 'Lapis',
    fields: { Expression: '約束', Sentence: '<b>約束</b>したからね。' },
  });
  await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
  await page.getByRole('checkbox', { name: 'Line at 00:04' }).click();
  await expect(page.getByTestId('preview-picture').locator('img')).toBeVisible();
  await expect(page.getByTestId('play-sound')).toBeEnabled();
  await expect(page.getByTestId('add-new')).toBeEnabled();
  await expect(page.getByTestId('newest-card')).toContainText('Adds to "約束"');
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-selected.png`) });

  await page.getByTestId('account').click();
  await page.screenshot({ path: path.join(folder!, `${info.project.name}-account.png`) });
  await page.keyboard.press('Escape');

  await page.getByTestId('open-settings').click();
  await expect(page.getByTestId('connected')).toBeVisible();
  // The pretend AnkiConnect's address, as one on a real server would read.
  await page.getByTestId('server-address').fill('https://anki.example.org');
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

test('the dictionary popup', async ({ page, request, jellyfin, player, anki }, info) => {
  const admin = await signIn(request, jellyfin.admin);
  const server = await anki();
  await ankiOnServer(request, admin, await signIn(request, jellyfin.viewer), server.url);
  await uploadDictionary(request, admin, wordsDictionary());
  await uploadDictionary(request, admin, metaDictionary());
  try {
    const tv = player(jellyfin.viewer, 'Living room', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    const line = page.locator('.line[data-line="0"]');
    await expect(line).toBeVisible();
    await tapCharacter(page, line, '昨');
    const popup = page.getByTestId('word-popup');
    await expect(popup.getByText('yesterday')).toBeVisible();
    await expect(popup.locator('.gsm-hoshidicts-mine-button').first()).toHaveAttribute(
      'data-card-state',
      'add',
    );
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(folder!, `${info.project.name}-popup.png`) });
  } finally {
    await forgetDictionaries(request, admin);
  }
});
