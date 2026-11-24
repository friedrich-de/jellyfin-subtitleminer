import { describe, expect, it } from 'vitest';

import { type Collection, createCollection, LAPIS_FIELDS } from '@/testing/ankiCollection';

import { type AnkiConnect, AnkiError } from './connect';
import { findLapis, isLapis } from './lapis';

/** The page's Anki over a collection in memory, which can refuse an action or not be reached. */
function ankiOver(
  collection: Collection,
  refuse: { action?: string; unreachable?: boolean } = {},
): AnkiConnect {
  return {
    where: 'server',
    invoke<T>(action: string, params?: Record<string, unknown>): Promise<T> {
      if (refuse.unreachable === true) {
        return Promise.reject(new AnkiError('Anki could not be reached.', true));
      }
      if (action === refuse.action) {
        return Promise.reject(new AnkiError('Anki said: unsupported action'));
      }
      const { result, error } = collection.answer({ action, version: 6, params });
      return error === null
        ? Promise.resolve(result as T)
        : Promise.reject(new AnkiError(`Anki said: ${error}`));
    },
    store: () => Promise.reject(new Error('not used')),
  };
}

describe('finding Lapis', () => {
  it('knows Lapis by its fields, whatever it is called', () => {
    expect(isLapis(LAPIS_FIELDS)).toBe(true);
    expect(isLapis([...LAPIS_FIELDS, 'Notes'])).toBe(true);
    expect(isLapis(LAPIS_FIELDS.filter((name) => name !== 'IsSentenceCard'))).toBe(false);
    expect(isLapis(['Front', 'Back'])).toBe(false);
  });

  it('takes the one deck made when Anki cannot say where its cards are', async () => {
    const collection = createCollection();
    collection.makeNote({ id: 1, deck: 'Default', modelName: 'Lapis', fields: {} });
    // An older plugin on the server lets no question about cards through.
    const found = await findLapis(
      ankiOver(collection, { action: 'cardsInfo' }),
      ['Basic', 'Lapis'],
      ['Default', 'Mining'],
    );
    expect(found).toMatchObject({ noteType: 'Lapis', deck: 'Mining' });
  });

  it('leaves the deck to the user when there are several and no card says', async () => {
    const found = await findLapis(ankiOver(createCollection()), ['Lapis'], ['Default', 'A', 'B']);
    expect(found?.deck).toBe('');
  });

  it('passes on an Anki that cannot be reached', async () => {
    await expect(
      findLapis(ankiOver(createCollection(), { unreachable: true }), ['Lapis'], ['Default']),
    ).rejects.toThrow('could not be reached');
  });
});
