import { forgetAnki, saveSettings } from '../support/ankiSettings';
import { expect, type Login, signIn, test, tokenHeader } from '../support/fixtures';
import { API } from '../support/watching';
import { AUDIO_KEY, aSound, jpodOf, setWordAudio, startAudioSource } from '../support/wordAudio';

let viewer: Login;
let admin: Login;

test.beforeAll(async ({ request, jellyfin }) => {
  viewer = await signIn(request, jellyfin.viewer);
  admin = await signIn(request, jellyfin.admin);
});

test.afterEach(async ({ request, jellyfin }) => {
  await setWordAudio(request, admin, '');
  await forgetAnki(request, jellyfin);
});

test.describe('word audio', () => {
  test('comes from the audio source an administrator sets, whose key nobody sees again', async ({
    request,
    jellyfin,
  }) => {
    const sound = await aSound(request, viewer, jellyfin.media.episode1.id);
    const source = await startAudioSource({ 食べる: sound, 昨日: sound });
    try {
      // Nobody hears a word before an administrator sets a source.
      const before = await request.get(`${API}/WordAudio?term=食べる&reading=たべる`, {
        headers: tokenHeader(viewer.token),
      });
      expect(before.status()).toBe(404);

      const refused = await request.put(`${API}/WordAudio`, {
        headers: tokenHeader(viewer.token),
        data: { Address: source.address },
      });
      expect(refused.status()).toBe(403);

      // Saving tries the source with 食べる.
      const saved = await setWordAudio(request, admin, source.address);
      expect(saved).toEqual({
        WordAudio: { Set: true, Host: new URL(source.url).hostname },
        Tried: 'found',
      });
      expect(JSON.stringify(saved)).not.toContain(AUDIO_KEY);

      const word = await request.get(`${API}/WordAudio?term=昨日&reading=きのう`, {
        headers: tokenHeader(viewer.token),
      });
      expect(word.status(), await word.text()).toBe(200);
      expect(word.headers()['content-type']).toBe('audio/mpeg');
      expect(Buffer.compare(await word.body(), sound)).toBe(0);
      expect(source.asked).toContain(
        `/audio/list?term=${encodeURIComponent('昨日')}&reading=${encodeURIComponent('きのう')}&apiKey=${AUDIO_KEY}`,
      );

      const none = await request.get(`${API}/WordAudio?term=猫&reading=ねこ`, {
        headers: tokenHeader(viewer.token),
      });
      expect(none.status()).toBe(404);

      // A user sees that there is a source, an administrator also where it is; nobody its key.
      for (const [login, host] of [
        [viewer, undefined],
        [admin, new URL(source.url).hostname],
      ] as const) {
        const list = await request.get(`${API}/Dictionaries`, {
          headers: tokenHeader(login.token),
        });
        const text = await list.text();
        expect(text).not.toContain(AUDIO_KEY);
        expect((JSON.parse(text) as { WordAudio: unknown }).WordAudio).toEqual(
          host === undefined ? { Set: true } : { Set: true, Host: host },
        );
      }

      const nobody = await request.get(`${API}/WordAudio?term=食べる`);
      expect(nobody.status()).toBe(401);
    } finally {
      await source.stop();
    }
  });

  test('says when the source refuses its key, and keeps it for the administrator to fix', async ({
    request,
  }) => {
    const source = await startAudioSource({});
    try {
      const saved = await setWordAudio(request, admin, source.address.replace(AUDIO_KEY, 'wrong'));
      expect(saved.Tried).toBe('failed');
      const word = await request.get(`${API}/WordAudio?term=食べる&reading=たべる`, {
        headers: tokenHeader(viewer.token),
      });
      expect(word.status()).toBe(502);

      const wrong = await request.put(`${API}/WordAudio`, {
        headers: tokenHeader(admin.token),
        data: { Address: `${source.url}/audio/list?reading={reading}` },
      });
      expect(wrong.status()).toBe(400);
    } finally {
      await source.stop();
    }
  });

  test('lists its sounds by name, as Yomitan does, and gives the one chosen', async ({
    request,
    jellyfin,
    anki,
  }) => {
    const sound = await aSound(request, viewer, jellyfin.media.episode1.id);
    const source = await startAudioSource({ 食べる: sound });
    const server = await anki();
    try {
      const unset = await request.get(`${API}/WordAudio/Sources?term=食べる&reading=たべる`, {
        headers: tokenHeader(viewer.token),
      });
      expect(unset.status()).toBe(404);
      await setWordAudio(request, admin, source.address);

      const listed = await request.get(`${API}/WordAudio/Sources?term=食べる&reading=たべる`, {
        headers: tokenHeader(viewer.token),
      });
      expect(listed.status(), await listed.text()).toBe(200);
      // Only the names: the addresses, and the key in them, stay on the server.
      expect(await listed.json()).toEqual({ Sources: ['gone', 'nhk16', 'jpod'] });
      expect(await listed.text()).not.toContain(AUDIO_KEY);

      const none = await request.get(`${API}/WordAudio/Sources?term=猫&reading=ねこ`, {
        headers: tokenHeader(viewer.token),
      });
      expect(await none.json()).toEqual({ Sources: [] });

      const chosen = await request.get(`${API}/WordAudio?term=食べる&reading=たべる&source=jpod`, {
        headers: tokenHeader(viewer.token),
      });
      expect(chosen.status()).toBe(200);
      expect(Buffer.compare(await chosen.body(), jpodOf(sound))).toBe(0);

      // A sound chosen that is gone is not swapped for another.
      const gone = await request.get(`${API}/WordAudio?term=食べる&reading=たべる&source=gone`, {
        headers: tokenHeader(viewer.token),
      });
      expect(gone.status()).toBe(404);

      // Into Anki, the chosen sound goes under a name of its own.
      await saveSettings(request, admin, {
        Anki: { Where: 'server', ServerAddress: server.url, ServerKey: '' },
      });
      const names: string[] = [];
      for (const query of ['', '&source=jpod']) {
        const stored = await request.post(
          `${API}/WordAudio/Anki?term=食べる&reading=たべる${query}`,
          { headers: tokenHeader(admin.token) },
        );
        expect(stored.status(), await stored.text()).toBe(200);
        names.push(((await stored.json()) as { FileName: string }).FileName);
      }
      expect(names[0]).not.toBe(names[1]);
      expect(Buffer.compare(server.media(names[0]!), sound)).toBe(0);
      expect(Buffer.compare(server.media(names[1]!), jpodOf(sound))).toBe(0);
    } finally {
      await source.stop();
    }
  });

  test('goes straight into the Anki on a server under a name of the word', async ({
    request,
    jellyfin,
    anki,
  }) => {
    const sound = await aSound(request, admin, jellyfin.media.episode1.id);
    const source = await startAudioSource({ 食べる: sound });
    const server = await anki();
    try {
      await setWordAudio(request, admin, source.address);
      await saveSettings(request, admin, {
        Anki: { Where: 'server', ServerAddress: server.url, ServerKey: '' },
      });

      const stored = await request.post(`${API}/WordAudio/Anki?term=食べる&reading=たべる`, {
        headers: tokenHeader(admin.token),
      });
      expect(stored.status(), await stored.text()).toBe(200);
      const { FileName } = (await stored.json()) as { FileName: string };
      expect(FileName).toMatch(/^subtitle-miner_word_[0-9a-f]{8}\.mp3$/);
      expect(Buffer.compare(server.media(FileName), sound)).toBe(0);

      // A word without a sound puts nothing there.
      const none = await request.post(`${API}/WordAudio/Anki?term=猫&reading=ねこ`, {
        headers: tokenHeader(admin.token),
      });
      expect(none.status()).toBe(404);
      expect(server.seen.filter((call) => call.action === 'storeMediaFile')).toHaveLength(1);
    } finally {
      await source.stop();
    }
  });
});
