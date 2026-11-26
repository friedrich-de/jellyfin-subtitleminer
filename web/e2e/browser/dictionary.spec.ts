import type { Page } from '@playwright/test';

import { guessRoles } from '../../src/anki/pageSettings';
import { LAPIS_FIELDS } from '../../src/testing/ankiCollection';
import { forgetAnki, saveSettings } from '../support/ankiSettings';
import {
  forgetDictionaries,
  marksDictionary,
  marksDictionaryPackedOnWindows,
  metaDictionary,
  uploadDictionary,
  wordsDictionary,
} from '../support/dictionaries';
import { expect, type Login, signIn, test } from '../support/fixtures';
import { COLOURS } from '../support/media';
import { colourOf, isColour } from '../support/probe';
import { signInToJellyfin } from '../support/webClient';
import { tapCharacter } from '../support/lines';
import { aSound, jpodOf, setWordAudio, startAudioSource } from '../support/wordAudio';
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
  await setWordAudio(request, admin, '');
});

/** Where a character of a line is on the screen, its middle. */
async function pointOf(page: Page, character: string): Promise<{ x: number; y: number }> {
  return firstLine(page)
    .locator('.said')
    .evaluate((element, wanted) => {
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
        const at = (node as Text).data.indexOf(wanted);
        if (at === -1) continue;
        const range = document.createRange();
        range.setStart(node, at);
        range.setEnd(node, at + 1);
        const box = range.getBoundingClientRect();
        return { x: box.left + box.width / 2, y: box.top + box.height / 2 };
      }
      throw new Error(`No ${wanted} in the line`);
    }, character);
}

/**
 * Selects a part of a definition in the popup: with the mouse on a desktop, as a long press
 * leaves it on a phone.
 */
async function selectInPopup(page: Page, text: string, mouse: boolean): Promise<void> {
  const ends = await popup(page)
    .locator('.dictionary-popup')
    .evaluate(
      (host, [wanted, drag]) => {
        const root = host.shadowRoot!;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
          const at = (node as Text).data.indexOf(wanted!);
          if (at === -1) continue;
          const range = document.createRange();
          range.setStart(node, at);
          range.setEnd(node, at + wanted!.length);
          if (drag === 'no') {
            document.getSelection()!.removeAllRanges();
            document.getSelection()!.addRange(range);
            return null;
          }
          const box = range.getBoundingClientRect();
          return { y: box.top + box.height / 2, from: box.left + 1, to: box.right - 1 };
        }
        throw new Error(`No ${wanted} in the popup`);
      },
      [text, mouse ? 'yes' : 'no'],
    );
  if (ends === null) return;
  await page.mouse.move(ends.from, ends.y);
  await page.mouse.down();
  await page.mouse.move(ends.to, ends.y, { steps: 5 });
  await page.mouse.up();
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

  test('shows the pictures of a dictionary added after the page opened', async ({
    page,
    request,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'Dictionary desk', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();
    await tapCharacter(page, firstLine(page), '話');
    await expect(popup(page).getByText('talk; story')).toBeVisible();
    await page.keyboard.press('Escape');

    // An administrator imports one more while the page is open, as an import that ends after the
    // settings were closed: the page's list does not have it yet.
    await uploadDictionary(request, admin, marksDictionary());
    const pictures: string[] = [];
    page.on('response', (response) => {
      if (response.url().includes('/Media?path=gaiji')) pictures.push(String(response.status()));
    });
    await tapCharacter(page, firstLine(page), '昨');
    await expect(popup(page).getByText('(ナリ) the day before today')).toBeVisible();

    // Its marks, an SVG under a Japanese name and a PNG, are drawn, and none failed.
    const marks = popup(page).locator('.dictionary-popup [data-image-load-state]');
    await expect(marks).toHaveCount(2);
    await expect
      .poll(() =>
        marks.evaluateAll((links) =>
          links.map((link) => link.getAttribute('data-image-load-state')),
        ),
      )
      .toEqual(['loaded', 'loaded']);
    await expect(popup(page)).not.toContainText('Image failed to load');
    const sizes = await popup(page)
      .locator('.dictionary-popup img')
      .evaluateAll((images) => images.map((image) => (image as HTMLImageElement).naturalWidth));
    expect(sizes).toEqual([16, 1]);
    expect(pictures).toEqual(['200', '200']);
  });

  test('shows the pictures a dictionary’s zip names in Shift-JIS', async ({
    page,
    request,
    jellyfin,
    player,
  }) => {
    // As a real Daijirin comes: its marks are named in Shift-JIS, with the names in UTF-8 beside
    // them, where Yomitan reads them.
    await uploadDictionary(request, admin, marksDictionaryPackedOnWindows());
    const tv = player(jellyfin.viewer, 'Dictionary desk', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();
    const pictures: string[] = [];
    page.on('response', (response) => {
      if (response.url().includes('/Media?path=gaiji')) pictures.push(String(response.status()));
    });
    await tapCharacter(page, firstLine(page), '昨');
    await expect(popup(page).getByText('(ナリ) the day before today')).toBeVisible();

    const marks = popup(page).locator('.dictionary-popup [data-image-load-state]');
    await expect(marks).toHaveCount(2);
    await expect
      .poll(() =>
        marks.evaluateAll((links) =>
          links.map((link) => link.getAttribute('data-image-load-state')),
        ),
      )
      .toEqual(['loaded', 'loaded']);
    await expect(popup(page)).not.toContainText('Image failed to load');
    expect(pictures).toEqual(['200', '200']);
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

  test('is looked up while Shift is held over it, as Yomitan scans', async ({
    page,
    jellyfin,
    player,
  }, info) => {
    test.skip(info.project.name === 'phone', 'A phone has no Shift key and no mouse.');
    const tv = player(jellyfin.viewer, 'Dictionary scan', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(firstLine(page)).toBeVisible();

    // Moving over the words without Shift looks nothing up.
    const talk = await pointOf(page, '話');
    await page.mouse.move(talk.x, talk.y);
    await expect(popup(page)).toBeHidden();

    // Shift pressed with the mouse resting on a word.
    await page.keyboard.down('Shift');
    await expect(popup(page).getByText('talk; story')).toBeVisible();
    await expect(firstLine(page).locator('.word')).toHaveText('話');

    // The mouse moved with Shift held.
    const yesterday = await pointOf(page, '昨');
    await page.mouse.move(yesterday.x, yesterday.y, { steps: 4 });
    await expect(popup(page).getByText('yesterday')).toBeVisible();
    await expect(firstLine(page).locator('.word')).toHaveText('昨日');
    await page.keyboard.up('Shift');

    // Without Shift, moving on leaves the popup as it is, and the line is not chosen.
    await page.mouse.move(talk.x, talk.y, { steps: 4 });
    await expect(popup(page).getByText('yesterday')).toBeVisible();
    await expect(page.getByRole('checkbox', { name: 'Line at 00:01' })).not.toBeChecked();
  });

  test('becomes a card with its sound and only the part of its definition selected', async ({
    page,
    request,
    jellyfin,
    player,
    anki,
  }, info) => {
    const sound = await aSound(request, admin, jellyfin.media.episode1.id);
    const source = await startAudioSource({ 話: sound });
    const server = await anki();
    try {
      await setWordAudio(request, admin, source.address);
      await saveSettings(request, admin, {
        Anki: { Where: 'server', ServerAddress: server.url, ServerKey: '' },
        Page: {
          anki: { deck: 'Mining', noteType: 'Lapis', fields: guessRoles(LAPIS_FIELDS), tags: [] },
        },
      });
      const tv = player(jellyfin.admin, 'Dictionary audio', 'mpv');
      await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
      await signInToJellyfin(page, jellyfin.admin);
      await page.goto('/SubtitleMiner/');
      await expect(firstLine(page)).toBeVisible();

      await tapCharacter(page, firstLine(page), '話');
      await expect(popup(page).getByText('talk; story')).toBeVisible();

      // The play button plays the word's sound, fetched by the server.
      const play = popup(page).getByRole('button', { name: 'Play pronunciation' }).first();
      await expect(play).toBeVisible();
      const heard = page.waitForResponse((response) => response.url().includes('/WordAudio?'));
      await play.click();
      expect((await heard).status()).toBe(200);
      await expect(page.getByTestId('word-status')).toHaveCount(0);

      // Held, it lists the word's sounds, as Yomitan's does; the one chosen plays and is kept.
      const box = (await play.boundingBox())!;
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.waitForTimeout(700);
      await page.mouse.up();
      const sounds = popup(page).locator('.gsm-hoshidicts-audio-menu-item');
      await expect(sounds).toHaveText(['gone', 'nhk16', 'jpod']);
      const chosen = page.waitForResponse((response) => response.url().includes('source=jpod'));
      await sounds.filter({ hasText: 'jpod' }).click();
      expect((await chosen).status()).toBe(200);
      await expect(sounds).toHaveCount(0);

      await selectInPopup(page, 'story', info.project.name !== 'phone');
      await popup(page).locator('.gsm-hoshidicts-mine-button').first().click();
      await expect(page.getByTestId('word-status')).toHaveText('Made a new card, "話".');

      const note = [...server.collection.notes.values()].at(-1)!;
      expect(note.fields.SelectionText).toBe('story');
      expect(note.fields.MainDefinition).toContain('talk; story');
      const name = /^\[sound:([^\]]+)\]$/.exec(note.fields.ExpressionAudio ?? '')?.[1];
      expect(name).toMatch(/^subtitle-miner_word_[0-9a-f]{8}\.mp3$/);
      // The card has the sound chosen.
      expect(Buffer.compare(server.media(name!), jpodOf(sound))).toBe(0);

      // A word the source has no sound for still gets its card, without one.
      await page.keyboard.press('Escape');
      await tapCharacter(page, firstLine(page), '昨');
      await expect(popup(page).getByText('yesterday')).toBeVisible();
      await popup(page).locator('.gsm-hoshidicts-mine-button').first().click();
      await expect(page.getByTestId('word-status')).toHaveText(
        'Made a new card, "昨日", without the word\'s sound: the audio source has none for it.',
      );
      const quiet = [...server.collection.notes.values()].at(-1)!;
      expect(quiet.fields).toMatchObject({
        Expression: '昨日',
        ExpressionAudio: '',
        SelectionText: '',
      });
    } finally {
      await source.stop();
    }
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

  test('take several files picked at once, and say how each of them ended', async ({
    page,
    jellyfin,
  }) => {
    await signInToJellyfin(page, jellyfin.admin);
    await page.goto('/SubtitleMiner/');
    await page.getByTestId('open-settings').click();
    const section = page.getByTestId('dictionaries');
    await expect(section.getByTestId('dictionary-E2E Words')).toBeVisible();

    await page.getByTestId('dictionary-file').setInputFiles([
      {
        name: 'one.zip',
        mimeType: 'application/zip',
        buffer: yomitanZip(wordsDictionary('One More')),
      },
      { name: 'other.zip', mimeType: 'application/zip', buffer: Buffer.from('not a zip') },
      {
        name: 'two.zip',
        mimeType: 'application/zip',
        buffer: yomitanZip(metaDictionary('Two More')),
      },
    ]);

    await expect(page.getByTestId('dictionary-message')).toHaveText(
      '2 dictionaries are ready: One More, Two More.\n' +
        'other.zip: That file is not a zip. A Yomitan dictionary is a zip with an index.json in it.',
      { useInnerText: true },
    );
    // In the order they were picked, behind those that were there.
    await expect(section.locator('.dictionary .title')).toHaveText([
      'E2E Words',
      'E2E Frequencies',
      'One More',
      'Two More',
    ]);
    await expect(page.getByTestId('dictionary-file')).toBeVisible();
  });

  test('take an audio source for words, which the page never shows again', async ({
    page,
    jellyfin,
  }) => {
    const source = await startAudioSource({ 食べる: Buffer.from('ID3') });
    try {
      await signInToJellyfin(page, jellyfin.admin);
      await page.goto('/SubtitleMiner/');
      await page.getByTestId('open-settings').click();
      await expect(page.getByTestId('word-audio-state')).toContainText('Words have no sound.');

      await page.getByTestId('word-audio-address').fill(source.address);
      await page.getByTestId('word-audio-save').click();
      await expect(page.getByTestId('word-audio-message')).toHaveText(
        'Saved. It works: it has a sound for 食べる.',
      );
      await expect(page.getByTestId('word-audio-state')).toContainText(
        `come from ${new URL(source.url).hostname}`,
      );
      await expect(page.getByTestId('word-audio-address')).toHaveValue('');
      expect(await page.content()).not.toContain('apiKey');

      await page.getByTestId('word-audio-remove').click();
      await expect(page.getByTestId('word-audio-message')).toHaveText('Words have no sound now.');
    } finally {
      await source.stop();
    }
  });
});
