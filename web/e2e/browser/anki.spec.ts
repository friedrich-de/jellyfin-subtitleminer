import type { Page } from '@playwright/test';

import { forgetAnki, letEveryoneUseServer } from '../support/ankiSettings';
import { expect, signIn, test } from '../support/fixtures';
import { COLOURS, FIRST_TRACK_EARLY_HZ } from '../support/media';
import { colourOf, isColour, soundOf } from '../support/probe';
import { signInToJellyfin } from '../support/webClient';

const KEY = 'e2e-anki-key';

/** The names of the files a card's fields point at. */
function pictureIn(field: string | undefined): string {
  const name = /<img src="([^"]+)">/.exec(field ?? '')?.[1];
  if (name === undefined) throw new Error(`No picture in ${field}`);
  return name;
}

function soundIn(field: string | undefined): string {
  const name = /\[sound:([^\]]+)\]/.exec(field ?? '')?.[1];
  if (name === undefined) throw new Error(`No sound in ${field}`);
  return name;
}

async function openSettings(page: Page): Promise<void> {
  await page.getByTestId('open-settings').click();
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();
}

test.afterEach(async ({ request, jellyfin }) => {
  await forgetAnki(request, jellyfin);
});

test.describe('cards', () => {
  test('go to the AnkiConnect on a server, with a key the page never gets', async ({
    page,
    jellyfin,
    player,
    anki,
  }) => {
    const server = await anki({ key: KEY });
    // A card Yomitan made a moment ago, with the word it looked up in bold.
    const id = server.collection.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '約束', Sentence: '<b>約束</b>したからね。' },
    });
    // A deck of the user's own besides: only the card says which deck is for mining.
    server.collection.decks.push('Kanji');
    const settingsSeen: Promise<string>[] = [];
    page.on('response', (response) => {
      if (response.url().includes('/SubtitleMiner/Api/Settings'))
        settingsSeen.push(response.text());
    });

    const tv = player(jellyfin.admin, 'Anki desk', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.admin);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line').first()).toBeVisible();

    // Before Anki is set up, the page says how.
    await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
    await page.getByTestId('set-up-anki').click();
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();

    await page.getByTestId('where-server').check();
    await page.getByTestId('server-address').fill(server.url);
    await page.getByTestId('server-key').fill('not-the-key');
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connect-error')).toHaveText(
      'The AnkiConnect on your server did not take the key. Check it in the settings.',
    );
    await page.getByTestId('server-key').fill(KEY);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connected')).toHaveText('Connected: 3 decks, 2 note types.');
    await expect(page.getByTestId('server-key')).toHaveValue('');

    // A new user's Lapis is chosen for them, with the deck its newest card is in.
    await expect(page.getByTestId('lapis-found')).toContainText('"Lapis" is a Lapis note type');
    await expect(page.getByTestId('note-type')).toHaveValue('Lapis');
    await expect(page.getByTestId('deck')).toHaveValue('Mining');
    await expect(page.getByTestId('role-Sentence')).toHaveValue('sentence');
    await expect(page.getByTestId('role-SentenceAudio')).toHaveValue('sound');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    await page.getByRole('checkbox', { name: 'Line at 00:04' }).click();
    await expect(page.getByTestId('selected-count')).toHaveText('2 lines selected');
    await expect(page.getByTestId('play-sound')).toBeEnabled();
    await expect(page.getByTestId('newest-card')).toHaveText('Adds to "約束", made just now.');
    await page.getByTestId('add-to-newest').click();
    await expect(page.getByTestId('sending')).toContainText('Added to "約束".');

    const note = server.collection.notes.get(id)!;
    expect(note.fields).toMatchObject({
      Expression: '約束',
      Sentence: '昨日の話、覚えてる？<br>もちろん。<br><b>約束</b>したからね。',
      MiscInfo: `${jellyfin.media.show} · S1 E1 · 00:01`,
    });
    // The picture from the middle of the first line, the sound of both with a little more.
    const picture = server.media(pictureIn(note.fields.Picture));
    expect(isColour(await colourOf(picture), COLOURS[0]!.rgb)).toBe(true);
    const sound = await soundOf(server.media(soundIn(note.fields.SentenceAudio)));
    expect(Math.abs(sound.hz - FIRST_TRACK_EARLY_HZ), `hums at ${sound.hz} Hz`).toBeLessThan(12);
    expect(Math.abs(sound.seconds - 5.5), `lasts ${sound.seconds} s`).toBeLessThan(0.2);

    // Every call came from the Jellyfin server with the key; the page never had it.
    expect(server.seen.filter((call) => call.origin !== undefined)).toEqual([]);
    expect(server.seen.filter((call) => call.key === KEY).length).toBeGreaterThan(5);
    const bodies = await Promise.all(settingsSeen);
    expect(bodies.length).toBeGreaterThan(1);
    for (const body of bodies) expect(body).not.toContain(KEY);

    await page.getByTestId('undo').click();
    await expect(page.getByTestId('sending')).toContainText('The card is as it was.');
    expect(note.fields.Sentence).toBe('<b>約束</b>したからね。');
    expect(note.fields.Picture).toBe('');

    // The bar has the server's Anki sync with AnkiWeb now, through the Jellyfin server.
    const release = server.hold('sync');
    await page.getByTestId('sync-anki').click();
    await expect(page.getByTestId('sync-status')).toHaveText('Syncing Anki with AnkiWeb…');
    await expect(page.getByTestId('sync-anki')).toBeDisabled();
    release();
    await expect(page.getByTestId('sync-status')).toHaveText(/^Anki synced with AnkiWeb\./);
    await expect(page.getByTestId('sync-anki')).toBeEnabled();
    expect(server.collection.sync.count).toBe(1);
    expect(server.seen.at(-1)).toMatchObject({ action: 'sync', key: KEY, origin: undefined });
  });

  test('go to the Anki on the device once it lets the page in', async ({
    page,
    jellyfin,
    player,
    anki,
  }) => {
    const device = await anki({ key: KEY });
    const origin = new URL(jellyfin.baseUrl).origin;

    const tv = player(jellyfin.viewer, 'Anki phone', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 10_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line').first()).toBeVisible();
    await openSettings(page);

    await page.getByTestId('where-device').check();
    await expect(page.getByText(`"webCorsOriginList": ["${origin}"]`)).toBeVisible();
    await page.getByTestId('device-address').fill(device.url);
    await page.getByTestId('device-key').fill(KEY);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connect-error')).toHaveText(
      `Anki could not be reached at ${device.url}. Is Anki open, with AnkiConnect letting this page in?`,
    );

    // The user puts the page's address into AnkiConnect's settings.
    device.allow(origin);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connected')).toHaveText('Connected: 2 decks, 2 note types.');
    await expect(page.getByTestId('note-type')).toHaveValue('Lapis');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    // No card made yet: the button is off, and the page says why.
    await page.getByRole('checkbox', { name: 'Line at 00:10' }).click();
    await expect(page.getByTestId('newest-card')).toHaveText(
      'Anki has no "Lapis" card made today. Make one with Yomitan first, or make a new card.',
    );
    await expect(page.getByTestId('add-to-newest')).toBeDisabled();

    // Yomitan makes one meanwhile: the page finds it by itself, and names it.
    const id = device.collection.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '明日', Sentence: 'じゃあ、<b>明日</b>の朝に行こう。' },
    });
    await expect(page.getByTestId('newest-card')).toHaveText('Adds to "明日", made just now.', {
      timeout: 15_000,
    });
    await expect(page.getByTestId('play-sound')).toBeEnabled();
    await page.getByTestId('add-to-newest').click();
    await expect(page.getByTestId('sending')).toContainText('Added to "明日".');

    const note = device.collection.notes.get(id)!;
    expect(note.fields.Sentence).toBe('じゃあ、<b>明日</b>の朝に行こう。');
    const picture = device.media(pictureIn(note.fields.Picture));
    expect(isColour(await colourOf(picture), COLOURS[2]!.rgb)).toBe(true);
    const sound = await soundOf(device.media(soundIn(note.fields.SentenceAudio)));
    expect(Math.abs(sound.seconds - 3.5), `lasts ${sound.seconds} s`).toBeLessThan(0.2);

    // The browser called Anki itself, from the page's origin.
    const stores = device.seen.filter((call) => call.action === 'storeMediaFile');
    expect(stores).toHaveLength(2);
    expect(stores.every((call) => call.origin === origin && call.key === KEY)).toBe(true);
  });

  test('go through the server for users an administrator lets', async ({
    page,
    request,
    jellyfin,
    player,
    anki,
  }) => {
    const server = await anki();
    const tv = player(jellyfin.viewer, 'Anki couch', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 4_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line').first()).toBeVisible();
    await openSettings(page);
    await expect(page.getByTestId('where-server')).toBeDisabled();
    await expect(page.getByText('has not let users do this')).toBeVisible();

    await letEveryoneUseServer(request, await signIn(request, jellyfin.admin), true);
    await page.reload();
    await openSettings(page);
    await page.getByTestId('where-server').check();
    await page.getByTestId('server-address').fill(server.url);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connected')).toBeVisible();

    await page.getByTestId('deck').selectOption('Mining');
    await page.getByTestId('note-type').selectOption('Basic');
    await page.getByTestId('role-Front').selectOption('sentence');
    await page.getByTestId('role-Back').selectOption('sound');
    await page.getByTestId('tags').fill('e2e mined');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    await page.getByRole('checkbox', { name: 'Line at 00:04' }).click();
    await expect(page.getByTestId('play-sound')).toBeEnabled();
    await page.getByTestId('add-new').click();
    await expect(page.getByTestId('sending')).toContainText(
      'Made a new card, "もちろん。 約束したからね。".',
    );
    await expect(page.getByTestId('undo')).toHaveCount(0);

    const note = [...server.collection.notes.values()].at(-1)!;
    expect(note).toMatchObject({
      deck: 'Mining',
      modelName: 'Basic',
      tags: ['e2e', 'mined'],
      fields: { Front: 'もちろん。<br>約束したからね。' },
    });
    const sound = await soundOf(server.media(soundIn(note.fields.Back)));
    expect(Math.abs(sound.seconds - 2.5), `lasts ${sound.seconds} s`).toBeLessThan(0.2);

    await page.getByTestId('add-new').click();
    await expect(page.getByTestId('sending')).toContainText(
      'This deck has a card like this already.',
    );
    expect(server.collection.notes.size).toBe(1);
  });
});
