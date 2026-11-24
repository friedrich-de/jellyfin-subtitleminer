import { describe, expect, it } from 'vitest';

import { createCollection } from '@/testing/ankiCollection';
import { json } from '@/testing/fakes';

import { AnkiError, type AnkiConnect, deviceAnki, serverAnki } from './connect';
import { syncAnki } from './sync';

const NO_CUTS = {
  picture: () => Promise.reject(new Error('no pictures here')),
  sound: () => Promise.reject(new Error('no sounds here')),
  wordAudio: () => Promise.reject(new Error('no words here')),
};

/** An AnkiConnect on a server, reached through the plugin as the page reaches it. */
function onServer(collection: ReturnType<typeof createCollection>): AnkiConnect {
  return serverAnki({
    anki: (action, params) =>
      Promise.resolve(json(collection.answer({ action, version: 6, params }))),
    pictureToAnki: () => Promise.reject(new Error('unused')),
    soundToAnki: () => Promise.reject(new Error('unused')),
    wordAudioToAnki: () => Promise.reject(new Error('unused')),
  });
}

describe('syncing Anki now', () => {
  it('has the server sync with AnkiWeb and says whether anything changed', async () => {
    const collection = createCollection();
    const anki = onServer(collection);

    expect(await syncAnki(anki)).toBe(
      'Anki synced: AnkiWeb and your Anki already had the same cards.',
    );
    collection.makeNote({ deck: 'Mining', modelName: 'Basic', fields: { Front: '約束' } });
    expect(await syncAnki(anki)).toBe('Anki synced with AnkiWeb.');
    expect(collection.sync.count).toBe(2);
    expect(collection.calls.map((call) => call.action)).toEqual(['sync', 'sync']);
  });

  it('says the Anki on the device has started its sync, which it answers nothing to', async () => {
    const fetchFn: typeof fetch = () => Promise.resolve(json({ result: null, error: null }));
    const anki = deviceAnki(fetchFn, 'http://127.0.0.1:8765', '', NO_CUTS);

    expect(await syncAnki(anki)).toBe('Anki on this device is syncing with AnkiWeb.');
  });

  it('puts what stopped the sync in plain words', async () => {
    const collection = createCollection();
    const anki = onServer(collection);

    collection.sync.error = 'A synchronization is already in progress';
    await expect(syncAnki(anki)).rejects.toThrow(
      'Anki is syncing with AnkiWeb already. Your cards go with that sync.',
    );

    collection.sync.error =
      'AnkiWeb requires a full sync, and this server has changes that are not on AnkiWeb yet. Nothing was replaced.';
    await expect(syncAnki(anki)).rejects.toThrow(
      'AnkiWeb asks for a full sync, and your Anki has changes that are not on AnkiWeb yet, so it did not sync. Nothing was replaced.',
    );

    collection.sync.error = 'AnkiWeb authentication failed: wrong password';
    const failed = syncAnki(anki);
    await expect(failed).rejects.toBeInstanceOf(AnkiError);
    await expect(failed).rejects.toThrow(
      'Anki said: AnkiWeb authentication failed: wrong password',
    );
    expect(collection.sync.count).toBe(0);
  });
});
