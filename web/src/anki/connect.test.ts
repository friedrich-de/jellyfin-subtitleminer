import { describe, expect, it } from 'vitest';

import { HttpError } from '@/api/http';
import type { PluginApi } from '@/api/plugin';
import { createCollection } from '@/testing/ankiCollection';
import { json } from '@/testing/fakes';

import { deviceAnki, markOf, mediaName, serverAnki, WordAudioMissingError } from './connect';

const CUT = {
  kind: 'picture' as const,
  itemId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
  source: 'source-1',
  options: { atMs: 2_250, format: 'jpeg' as const, width: 640 },
};

const WORD = { kind: 'word-audio' as const, term: '食べる', reading: 'たべる' };

function cutsApi(): Pick<PluginApi, 'picture' | 'sound' | 'wordAudio'> {
  return {
    picture: () => Promise.resolve(new Blob(['picture'], { type: 'image/jpeg' })),
    sound: () => Promise.resolve(new Blob(['sound'], { type: 'audio/mpeg' })),
    wordAudio: (term, _reading, _signal, choice) =>
      term === '食べる'
        ? Promise.resolve(new Blob([choice ?? 'word'], { type: 'audio/mpeg' }))
        : Promise.reject(new HttpError(404, 'The audio source has no sound for this word.')),
  };
}

describe('the Anki on the device', () => {
  it('is called by the browser in version 6, with its key', async () => {
    const collection = createCollection({ key: 'device-key' });
    const bodies: unknown[] = [];
    const fetchFn: typeof fetch = (_url, init) => {
      const body = JSON.parse(init?.body as string) as unknown;
      bodies.push(body);
      return Promise.resolve(json(collection.answer(body)));
    };
    const anki = deviceAnki(fetchFn, 'http://127.0.0.1:8765', 'device-key', cutsApi());

    expect(await anki.invoke('deckNames')).toEqual(['Default', 'Mining']);
    expect(bodies[0]).toEqual({ action: 'deckNames', version: 6, key: 'device-key' });

    // The page cuts the picture through Jellyfin and hands it over as base64.
    const name = await anki.store(CUT);
    expect(name).toBe(mediaName(CUT, 'image/jpeg'));
    expect(name).toMatch(/^subtitle-miner_aaaaaaaa_2250_[0-9a-f]{8}\.jpg$/);
    expect(atob(collection.media.get(name)!)).toBe('picture');

    const wrong = deviceAnki(fetchFn, 'http://127.0.0.1:8765', 'other', cutsApi());
    await expect(wrong.invoke('version')).rejects.toThrow('wants a key');
  });

  it('says so when Anki is not open or does not let the page in', async () => {
    const anki = deviceAnki(
      () => Promise.reject(new TypeError('Failed to fetch')),
      'http://127.0.0.1:8765',
      '',
      cutsApi(),
    );

    await expect(anki.invoke('version')).rejects.toMatchObject({
      unreachable: true,
      message: expect.stringContaining('Is Anki open') as string,
    });

    const notAnki = deviceAnki(
      () => Promise.resolve(new Response('<html>')),
      'http://127.0.0.1:8765',
      '',
      cutsApi(),
    );
    await expect(notAnki.invoke('version')).rejects.toThrow('is not AnkiConnect');
  });

  it('puts how a word sounds into Anki under a name of the word', async () => {
    const collection = createCollection();
    const fetchFn: typeof fetch = (_url, init) =>
      Promise.resolve(json(collection.answer(JSON.parse(init?.body as string) as unknown)));
    const anki = deviceAnki(fetchFn, 'http://127.0.0.1:8765', '', cutsApi());

    const name = await anki.store(WORD);

    expect(name).toBe(mediaName(WORD, 'audio/mpeg'));
    expect(name).toMatch(/^subtitle-miner_word_[0-9a-f]{8}\.mp3$/);
    expect(mediaName({ ...WORD, term: ' 食べる ' }, 'audio/mpeg')).toBe(name);
    expect(mediaName({ ...WORD, reading: '' }, 'audio/mpeg')).not.toBe(name);
    expect(atob(collection.media.get(name)!)).toBe('word');

    // The sound chosen in the popup goes in under a name of its own.
    const chosen = await anki.store({ ...WORD, choice: 'jpod' });
    expect(chosen).not.toBe(name);
    expect(atob(collection.media.get(chosen)!)).toBe('jpod');
    expect(mediaName({ ...WORD, choice: '' }, 'audio/mpeg')).toBe(name);
    collection.media.delete(chosen);

    // A word the source has no sound for is told apart from Anki failing.
    await expect(anki.store({ ...WORD, term: '猫' })).rejects.toBeInstanceOf(WordAudioMissingError);
    expect(collection.media.size).toBe(1);
  });

  it('names the same cut the same way', () => {
    expect(markOf('a')).toBe(markOf('a'));
    expect(markOf('a')).not.toBe(markOf('b'));
    expect(
      mediaName({ ...CUT, kind: 'sound', options: { startMs: 750, endMs: 6_250 } }, 'audio/ogg'),
    ).toMatch(/_750_[0-9a-f]{8}\.ogg$/);
  });
});

describe('the Anki on a server', () => {
  it('goes through the plugin, which answers in AnkiConnect words', async () => {
    const collection = createCollection();
    const calls: [string, unknown][] = [];
    const anki = serverAnki({
      anki: (action, params) => {
        calls.push([action, params]);
        return Promise.resolve(json(collection.answer({ action, params })));
      },
      pictureToAnki: () => Promise.resolve({ FileName: 'stored.jpg' }),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: (term, reading, choice) =>
        Promise.resolve({
          FileName: `${term}-${reading}${choice === undefined ? '' : `-${choice}`}.mp3`,
        }),
    });

    expect(await anki.invoke('findNotes', { query: 'added:1' })).toEqual([]);
    expect(calls).toEqual([['findNotes', { query: 'added:1' }]]);
    expect(await anki.store(CUT)).toBe('stored.jpg');
    expect(await anki.store(WORD)).toBe('食べる-たべる.mp3');
    expect(await anki.store({ ...WORD, choice: 'jpod' })).toBe('食べる-たべる-jpod.mp3');

    const refusing = serverAnki({
      anki: () => Promise.resolve(json({ result: null, error: 'valid api key must be provided' })),
      pictureToAnki: () => Promise.reject(new Error('unused')),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: () => Promise.reject(new Error('unused')),
    });
    await expect(refusing.invoke('version')).rejects.toThrow('did not take the key');
  });

  it('passes on what the plugin says when it cannot reach the AnkiConnect', async () => {
    const anki = serverAnki({
      anki: () =>
        Promise.reject(
          new HttpError(
            502,
            'The AnkiConnect at anki.example.org could not be reached from the Jellyfin server.',
          ),
        ),
      pictureToAnki: () =>
        Promise.reject(
          new HttpError(409, 'Set the address of the AnkiConnect on your server in the settings.'),
        ),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: () => Promise.reject(new Error('unused')),
    });

    await expect(anki.invoke('version')).rejects.toMatchObject({ unreachable: true });
    await expect(anki.store(CUT)).rejects.toThrow('Set the address');
  });

  it('tells a word without a sound apart from an Anki it cannot reach', async () => {
    const answer = { status: 404, text: 'The audio source has no sound for this word.' };
    const anki = serverAnki({
      anki: () => Promise.reject(new Error('unused')),
      pictureToAnki: () => Promise.reject(new Error('unused')),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: () => Promise.reject(new HttpError(answer.status, answer.text)),
    });

    await expect(anki.store(WORD)).rejects.toBeInstanceOf(WordAudioMissingError);
    Object.assign(answer, { status: 502, text: 'The audio source could not be reached.' });
    await expect(anki.store(WORD)).rejects.toBeInstanceOf(WordAudioMissingError);
    Object.assign(answer, { status: 409, text: 'Set the address of the AnkiConnect.' });
    await expect(anki.store(WORD)).rejects.toThrow('Set the address');
    await expect(anki.store(WORD)).rejects.not.toBeInstanceOf(WordAudioMissingError);
  });
});
