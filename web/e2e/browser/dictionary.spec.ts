import type { Locator, Page } from '@playwright/test';

import { guessRoles } from '../../src/anki/pageSettings';
import { LAPIS_FIELDS } from '../../src/testing/ankiCollection';
import { forgetAnki, saveSettings } from '../support/ankiSettings';
import {
  forgetDictionaries,
  metaDictionary,
  uploadDictionary,
  wordsDictionary,
} from '../support/dictionaries';
import { expect, type Login, signIn, test } from '../support/fixtures';
import { COLOURS } from '../support/media';
import { colourOf, isColour } from '../support/probe';
import { signInToJellyfin } from '../support/webClient';
import { yomitanZip } from '../support/yomitanZip';

let admin: Login;

test.beforeEach(async ({ request, jellyfin }) => {
  admin = await signIn(request, jellyfin.admin);
  await uploadDictionary(request, admin, wordsDictionary());
  await uploadDictionary(request, admin, metaDictionary());
});

test.afterEach(async ({ request, jellyfin }) => {
  await forgetDictionaries(request, admin);
  await forgetAnki(request, jellyfin);
});

/** Taps a character of a line's said text, as a finger or a mouse would. */
async function tapCharacter(page: Page, line: Locator, character: string): Promise<void> {
  const said = line.locator('.said');
  const point = await said.evaluate((element, wanted) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const at = (node as Text).data.indexOf(wanted);
      if (at === -1) continue;
      const range = document.createRange();
      range.setStart(node, at);
      range.setEnd(node, at + 1);
      const box = range.getBoundingClientRect();
      const outer = element.getBoundingClientRect();
      return {
        x: box.left + box.width * 0.6 - outer.left,
        y: box.top + box.height / 2 - outer.top,
      };
    }
    throw new Error(`No ${wanted} in the line`);
  }, character);
  const touch = await page.evaluate(() => navigator.maxTouchPoints > 0);
  if (touch) await said.tap({ position: point });
  else await said.click({ position: point });
}

const popup = (page: Page) => page.getByTestId('word-popup');
const firstLine = (page: Page) => page.locator('.line[data-line="0"]');

test.describe('a word tapped in a line', () => {
  test('is looked up on the server and shown with its picture, frequency and pitch', async ({
    page,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'Dictionary desk', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();

    const picture = page.waitForResponse((response) =>
      response.url().includes('/Media?path=img%2Fbowl.png'),
    );
    await tapCharacter(page, firstLine(page), '話');
    await expect(popup(page)).toBeVisible();
    await expect(popup(page).getByText('talk; story')).toBeVisible();
    // The word is picked out in its line, and choosing the line is left to its box.
    await expect(firstLine(page).locator('.word')).toHaveText('話');
    await expect(page.getByRole('checkbox', { name: 'Line at 00:01' })).not.toBeChecked();

    // The picture of the definition came with the login, and the popup shows it.
    expect((await picture).status()).toBe(200);
    const image = popup(page).locator('img').first();
    await expect(image).toBeVisible();
    await expect
      .poll(() => image.evaluate((element) => (element as HTMLImageElement).naturalWidth))
      .toBe(1);

    // The popup's own stylesheets and the dictionary's are adopted, as the page's policy needs.
    const sheets = await popup(page)
      .locator('.dictionary-popup')
      .evaluate((host) => host.shadowRoot?.adoptedStyleSheets.length ?? 0);
    expect(sheets).toBeGreaterThanOrEqual(3);

    await tapCharacter(page, firstLine(page), '昨');
    await expect(popup(page).getByText('yesterday')).toBeVisible();
    await expect(firstLine(page).locator('.word')).toHaveText('昨日');

    await page.keyboard.press('Escape');
    await expect(popup(page)).toBeHidden();
    await expect(firstLine(page).locator('.word')).toHaveCount(0);

    // Without Anki the popup says how to make cards.
    await tapCharacter(page, firstLine(page), '話');
    await expect(popup(page)).toContainText('Choose where your cards go in the settings');
    // A tap beside the words closes it, and a tap on the line's box still chooses the line.
    await page.getByTestId('title').click();
    await expect(popup(page)).toBeHidden();
    await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
    await expect(page.getByRole('checkbox', { name: 'Line at 00:01' })).toBeChecked();
  });

  test('becomes a new card with its reading, meaning, frequency, pitch, picture and sound', async ({
    page,
    request,
    jellyfin,
    player,
    anki,
  }) => {
    const server = await anki();
    await saveSettings(request, admin, {
      Anki: { Where: 'server', ServerAddress: server.url, ServerKey: '' },
      Page: {
        anki: {
          deck: 'Mining',
          noteType: 'Lapis',
          fields: guessRoles(LAPIS_FIELDS),
          tags: ['e2e'],
        },
      },
    });

    const tv = player(jellyfin.admin, 'Dictionary couch', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.admin);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();

    await tapCharacter(page, firstLine(page), '昨');
    await expect(popup(page).getByText('yesterday')).toBeVisible();
    const add = popup(page).locator('.gsm-hoshidicts-mine-button').first();
    await expect(add).toHaveAttribute('data-card-state', 'add');
    await add.click();
    await expect(page.getByTestId('word-status')).toHaveText('Made a new card, "昨日".');
    await expect(add).toHaveAttribute('data-card-state', 'in-anki');

    const note = [...server.collection.notes.values()].at(-1)!;
    expect(note).toMatchObject({ deck: 'Mining', modelName: 'Lapis', tags: ['e2e'] });
    expect(note.fields).toMatchObject({
      Expression: '昨日',
      ExpressionReading: 'きのう',
      ExpressionFurigana: '昨日[きのう]',
      MainDefinition: '<div class="yomitan-glossary" style="text-align: left">yesterday</div>',
      Sentence: '<b>昨日</b>の話、覚えてる？',
      Frequency: '77',
      PitchPosition: '2, 0',
      MiscInfo: `${jellyfin.media.show} · S1 E1 · 00:01`,
    });
    const pictureName = /<img src="([^"]+)">/.exec(note.fields.Picture ?? '')?.[1];
    expect(isColour(await colourOf(server.media(pictureName!)), COLOURS[0]!.rgb)).toBe(true);
    expect(note.fields.SentenceAudio).toMatch(/^\[sound:[^\]]+\]$/);

    // Looked up again, Anki says the deck has it.
    await page.keyboard.press('Escape');
    await tapCharacter(page, firstLine(page), '昨');
    await expect(popup(page).locator('.gsm-hoshidicts-mine-button').first()).toHaveAttribute(
      'data-card-state',
      'in-anki',
    );
    expect(server.seen.some((call) => call.action === 'canAddNotesWithErrorDetail')).toBe(true);
    expect(server.collection.notes.size).toBe(1);
  });

  test('keeps Anki’s answer with its own word when another is looked up meanwhile', async ({
    page,
    request,
    jellyfin,
    player,
    anki,
  }) => {
    const server = await anki();
    await saveSettings(request, admin, {
      Anki: { Where: 'server', ServerAddress: server.url, ServerKey: '' },
      Page: {
        anki: { deck: 'Mining', noteType: 'Lapis', fields: guessRoles(LAPIS_FIELDS), tags: [] },
      },
    });
    const tv = player(jellyfin.admin, 'Dictionary sofa', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.admin);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();

    // Anki takes its time with the card of 昨日, and 話 is looked up meanwhile.
    const release = server.hold('addNote');
    await tapCharacter(page, firstLine(page), '昨');
    const button = popup(page).locator('.gsm-hoshidicts-mine-button').first();
    await expect(button).toHaveAttribute('data-card-state', 'add');
    await button.click();
    await expect(button).toHaveAttribute('data-card-state', 'adding');
    await expect.poll(() => server.seen.some((call) => call.action === 'addNote')).toBe(true);
    await tapCharacter(page, firstLine(page), '話');
    await expect(popup(page).getByText('talk; story')).toBeVisible();
    await expect(button).toHaveAttribute('data-card-state', 'add');

    release();
    await expect(page.getByTestId('word-status')).toHaveText('Made a new card, "昨日".');
    // 話 is not the word Anki got.
    await expect(button).toHaveAttribute('data-card-state', 'add');
    await page.keyboard.press('Escape');
    await tapCharacter(page, firstLine(page), '昨');
    await expect(button).toHaveAttribute('data-card-state', 'in-anki');
  });

  test('is left to Yomitan when the user turns looking up off', async ({
    page,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'Dictionary phone', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();

    await page.getByTestId('open-settings').click();
    await expect(page.getByTestId('look-up')).toBeChecked();
    await expect(page.getByTestId('dictionaries')).toContainText('In E2E Words, E2E Frequencies');
    // A user who is not an administrator cannot change the server's dictionaries.
    await expect(page.getByTestId('dictionary-address')).toHaveCount(0);
    await page.getByTestId('look-up').uncheck();
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    await tapCharacter(page, firstLine(page), '話');
    await expect(page.getByRole('checkbox', { name: 'Line at 00:01' })).toBeChecked();
    await expect(popup(page)).toHaveCount(0);
  });
});

test.describe('the dictionaries in the settings', () => {
  test('are added, turned off and removed by an administrator', async ({ page, jellyfin }) => {
    await signInToJellyfin(page, jellyfin.admin);
    await page.goto('/SubtitleMiner/');
    await page.getByTestId('open-settings').click();
    const section = page.getByTestId('dictionaries');
    await expect(section.getByTestId('dictionary-E2E Words')).toContainText('4 words');

    await page.getByTestId('dictionary-file').setInputFiles({
      name: 'more.zip',
      mimeType: 'application/zip',
      buffer: yomitanZip(wordsDictionary('More Words')),
    });
    await expect(page.getByTestId('dictionary-message')).toHaveText('More Words is ready.');
    await expect(section.getByTestId('dictionary-More Words')).toBeVisible();

    await page.getByRole('switch', { name: 'Use E2E Words' }).click();
    await expect(page.getByRole('switch', { name: 'Use E2E Words' })).toHaveAttribute(
      'aria-checked',
      'false',
    );
    await expect(section).toContainText('In E2E Frequencies, More Words');

    await page.getByRole('button', { name: 'Remove More Words' }).click();
    await page.getByTestId('confirm-remove').click();
    await expect(section.getByTestId('dictionary-More Words')).toHaveCount(0);

    // An address where nothing answers says so.
    await page.getByTestId('dictionary-address').fill('http://127.0.0.1:9/none.zip');
    await page.getByTestId('dictionary-download').click();
    await expect(page.getByTestId('dictionary-message')).toContainText(
      'The address could not be reached:',
    );
  });
});
