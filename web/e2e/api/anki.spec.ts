import type { APIRequestContext, APIResponse } from '@playwright/test';

import { forgetAnki, letEveryoneUseServer, saveSettings } from '../support/ankiSettings';
import { expect, type Login, signIn, test, tokenHeader } from '../support/fixtures';
import { COLOURS, FIRST_TRACK_EARLY_HZ } from '../support/media';
import { colourOf, isColour, probe, soundOf } from '../support/probe';
import { API } from '../support/watching';

const KEY = 'e2e-server-key';

let viewer: Login;
let admin: Login;

function post(request: APIRequestContext, login: Login, path: string, data?: unknown) {
  return request.post(`${API}/${path}`, {
    headers: tokenHeader(login.token),
    ...(data === undefined ? {} : { data }),
  });
}

async function answer(response: APIResponse): Promise<{ result: unknown; error: unknown }> {
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json()) as { result: unknown; error: unknown };
}

async function detail(response: APIResponse, status: number): Promise<string> {
  expect(response.status(), await response.text()).toBe(status);
  return ((await response.json()) as { detail?: string }).detail ?? '';
}

test.beforeAll(async ({ request, jellyfin }) => {
  viewer = await signIn(request, jellyfin.viewer);
  admin = await signIn(request, jellyfin.admin);
});

test.afterEach(async ({ request, jellyfin }) => {
  await forgetAnki(request, jellyfin);
});

test.describe('the AnkiConnect on a server', () => {
  test('gets the page’s calls with the key, which the page never sees', async ({
    request,
    anki,
  }) => {
    const server = await anki({ key: KEY });
    const saved = await request.put(`${API}/Settings`, {
      headers: tokenHeader(admin.token),
      data: { Anki: { Where: 'server', ServerAddress: server.url, ServerKey: KEY } },
    });
    expect(saved.status(), await saved.text()).toBe(200);
    expect(await saved.text()).not.toContain(KEY);
    const read = await request.get(`${API}/Settings`, { headers: tokenHeader(admin.token) });
    expect(await read.json()).toMatchObject({
      Anki: { Where: 'server', ServerAddress: server.url, ServerKeySet: true, ServerAllowed: true },
    });

    expect(await answer(await post(request, admin, 'Anki', { Action: 'deckNames' }))).toEqual({
      result: ['Default', 'Mining'],
      error: null,
    });
    // The Jellyfin server made the call, not a browser, and put the key in.
    expect(server.seen.at(-1)).toEqual({ action: 'deckNames', key: KEY, origin: undefined });

    // AnkiConnect's own errors pass on as they are.
    const missing = { Action: 'modelFieldNames', Params: { modelName: 'Nope' } };
    expect(await answer(await post(request, admin, 'Anki', missing))).toEqual({
      result: null,
      error: 'model was not found: Nope',
    });

    // A sync holds the collection: the plugin asks once more.
    server.busyOnce();
    expect(await answer(await post(request, admin, 'Anki', { Action: 'version' }))).toEqual({
      result: 6,
      error: null,
    });
    expect(server.seen.slice(-2).map((call) => call.action)).toEqual(['version', 'version']);

    // A wrong key: AnkiConnect says so, and the page tells the user.
    await saveSettings(request, admin, { Anki: { ServerKey: 'not-the-key' } });
    expect(await answer(await post(request, admin, 'Anki', { Action: 'deckNames' }))).toEqual({
      result: null,
      error: 'valid api key must be provided',
    });

    // Gone: the plugin says it could not be reached.
    await server.stop();
    expect(await detail(await post(request, admin, 'Anki', { Action: 'version' }), 502)).toContain(
      'could not be reached',
    );
  });

  test('gets nothing that deletes, opens windows or fetches files', async ({ request, anki }) => {
    const server = await anki({ key: KEY });
    await saveSettings(request, admin, {
      Anki: { Where: 'server', ServerAddress: server.url, ServerKey: KEY },
    });

    for (const action of ['deleteDecks', 'deleteNotes', 'storeMediaFile', 'guiBrowse', 'multi']) {
      const refused = await post(request, admin, 'Anki', { Action: action, Params: {} });
      expect(await detail(refused, 400), action).toBe(
        'The page cannot ask Anki for that through the server.',
      );
    }

    const fetching = {
      Action: 'addNote',
      Params: {
        note: {
          deckName: 'Mining',
          modelName: 'Basic',
          fields: { Front: 'a', Back: 'b' },
          picture: [{ url: 'http://127.0.0.1/secret.jpg', filename: 'x.jpg', fields: ['Back'] }],
        },
      },
    };
    expect(await detail(await post(request, admin, 'Anki', fetching), 400)).toContain(
      'through the plugin',
    );
    expect(server.seen).toEqual([]);
  });

  test('gets the plugin’s own pictures and sound in its media', async ({
    request,
    jellyfin,
    anki,
  }) => {
    const server = await anki({ key: KEY });
    await saveSettings(request, admin, {
      Anki: { Where: 'server', ServerAddress: server.url, ServerKey: KEY },
    });
    const episode = jellyfin.media.episode1.id;

    const picture = (await (
      await post(request, admin, `Items/${episode}/Picture/Anki?at=2250&width=320`)
    ).json()) as { FileName: string };
    expect(picture.FileName).toMatch(/\.jpg$/);
    const stored = server.media(picture.FileName);
    expect(await probe(stored)).toMatchObject({ codec: 'mjpeg', width: 320 });
    expect(isColour(await colourOf(stored), COLOURS[0]!.rgb)).toBe(true);

    const sound = (await (
      await post(request, admin, `Items/${episode}/Audio/Anki?start=1000&end=3500`)
    ).json()) as { FileName: string };
    expect(sound.FileName).toMatch(/\.mp3$/);
    const heard = await soundOf(server.media(sound.FileName));
    expect(Math.abs(heard.hz - FIRST_TRACK_EARLY_HZ), `hums at ${heard.hz} Hz`).toBeLessThan(12);
    expect(Math.abs(heard.seconds - 2.5), `lasts ${heard.seconds} s`).toBeLessThan(0.2);

    // The same cut gets the same name, so Anki keeps one file.
    const again = (await (
      await post(request, admin, `Items/${episode}/Audio/Anki?start=1000&end=3500`)
    ).json()) as { FileName: string };
    expect(again.FileName).toBe(sound.FileName);
    expect(server.seen.filter((call) => call.action === 'storeMediaFile')).toHaveLength(3);
    expect(server.seen.every((call) => call.key === KEY)).toBe(true);

    // A video the user may not see is not cut for Anki either.
    const film = jellyfin.media.film.id;
    expect(
      await detail(await post(request, viewer, `Items/${film}/Picture/Anki?at=1000`), 404),
    ).toBe('This video is not in your library.');
  });

  test('is for administrators unless they let everyone use it', async ({ request, anki }) => {
    const server = await anki();
    const change = { Anki: { Where: 'server', ServerAddress: server.url } };
    const refused = await request.put(`${API}/Settings`, {
      headers: tokenHeader(viewer.token),
      data: change,
    });
    expect(refused.status()).toBe(403);
    expect(await detail(await post(request, viewer, 'Anki', { Action: 'version' }), 403)).not.toBe(
      '',
    );
    const everyone = await request.put(`${API}/Settings/Everyone`, {
      headers: tokenHeader(viewer.token),
      data: { AnkiThroughServer: true },
    });
    expect(everyone.status()).toBe(403);

    await letEveryoneUseServer(request, admin, true);
    await saveSettings(request, viewer, change);
    expect(await answer(await post(request, viewer, 'Anki', { Action: 'version' }))).toEqual({
      result: 6,
      error: null,
    });

    // Closed again: the address stays in the settings, but nothing is passed on.
    await letEveryoneUseServer(request, admin, false);
    expect(await detail(await post(request, viewer, 'Anki', { Action: 'version' }), 403)).not.toBe(
      '',
    );
    expect(server.seen).toHaveLength(1);
  });
});
