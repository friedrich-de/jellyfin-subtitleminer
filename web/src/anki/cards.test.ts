import { describe, expect, it } from 'vitest';

import { type Collection, createCollection } from '@/testing/ankiCollection';

import {
  addNew,
  addToNewest,
  type CardMedia,
  fieldHtml,
  keepMarks,
  plainText,
  undoUpdate,
} from './cards';
import { type AnkiConnect, AnkiError, type MediaCut } from './connect';
import { defaultPageSettings, guessRoles, type PageSettings } from './pageSettings';

const NOW = new Date(2026, 10, 24, 20, 0, 0).getTime();

/** The page's Anki over a collection in memory; a cut is stored under a name made of its kind. */
function ankiOver(collection: Collection): AnkiConnect & { cuts: MediaCut[] } {
  const cuts: MediaCut[] = [];
  return {
    where: 'server',
    cuts,
    invoke<T>(action: string, params?: Record<string, unknown>): Promise<T> {
      const { result, error } = collection.answer({ action, version: 6, params });
      return error === null
        ? Promise.resolve(result as T)
        : Promise.reject(new AnkiError(`Anki said: ${error}`));
    },
    store(cut) {
      cuts.push(cut);
      const name = `${cut.kind}-${cut.itemId}.bin`;
      collection.media.set(name, 'data');
      return Promise.resolve(name);
    },
  };
}

function lapis(): PageSettings {
  const settings = defaultPageSettings();
  settings.anki.deck = 'Mining';
  settings.anki.noteType = 'Lapis';
  settings.anki.fields = guessRoles(createCollection().models.Lapis!);
  return settings;
}

const CONTENT = {
  sentence: '昨日の話、覚えてる？\nもちろん。',
  translation: 'Do you remember?\nOf course.',
  source: 'Sample Show · S1 E1 · 00:01',
};

const MEDIA: CardMedia = {
  itemId: 'item-1',
  source: 'source-1',
  picture: { atMs: 2_250, format: 'jpeg', width: 640 },
  sound: { startMs: 750, endMs: 6_250, format: 'mp3' },
};

describe('making cards', () => {
  it('writes text for a field with its breaks and nothing that is markup', () => {
    expect(fieldHtml(' a < b & "c"\nd ')).toBe('a &lt; b &amp; &quot;c&quot;<br>d');
    expect(plainText('<b>約束</b>&nbsp;&amp;<br>more')).toBe('約束 & more');
  });

  it('keeps the word Yomitan marked when the sentence grows', () => {
    expect(keepMarks('昨日の<b>話</b>、覚えてる？', '昨日の話、覚えてる？<br>もちろん。')).toBe(
      '昨日の<b>話</b>、覚えてる？<br>もちろん。',
    );
    expect(keepMarks('<span class="x">約束</span>', 'もちろん。<br>約束したからね。')).toBe(
      'もちろん。<br><span class="x">約束</span>したからね。',
    );
    // A mark whose word is not in the lines is dropped.
    expect(keepMarks('<b>明日</b>', '昨日の話')).toBe('昨日の話');
  });

  it('adds the lines, the picture and the sound to the card Yomitan made a moment ago', async () => {
    const collection = createCollection({ now: () => NOW });
    collection.makeNote({
      id: NOW - 600_000,
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '前' },
    });
    const newest = collection.makeNote({
      id: NOW - 60_000,
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '話', Sentence: '昨日の<b>話</b>、覚えてる？', Glossary: 'talk' },
    });
    const anki = ankiOver(collection);

    const outcome = await addToNewest(anki, lapis(), CONTENT, MEDIA, NOW);

    expect(outcome).toMatchObject({ kind: 'updated', noteId: newest, label: '話' });
    const fields = collection.notes.get(newest)!.fields;
    expect(fields.Sentence).toBe('昨日の<b>話</b>、覚えてる？<br>もちろん。');
    expect(fields.Picture).toBe('<img src="picture-item-1.bin">');
    expect(fields.SentenceAudio).toBe('[sound:sound-item-1.bin]');
    expect(fields.MiscInfo).toBe('Sample Show · S1 E1 · 00:01');
    // What Yomitan wrote and the settings give no part stays.
    expect([fields.Expression, fields.Glossary]).toEqual(['話', 'talk']);
    expect(anki.cuts.map((cut) => cut.kind).sort()).toEqual(['picture', 'sound']);

    // Undo puts back what was there.
    await undoUpdate(anki, outcome);
    expect(collection.notes.get(newest)!.fields).toMatchObject({
      Sentence: '昨日の<b>話</b>、覚えてる？',
      Picture: '',
      SentenceAudio: '',
      Expression: '話',
    });
  });

  it('takes no card made too long ago, or none at all, and says why', async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);
    await expect(addToNewest(anki, lapis(), CONTENT, MEDIA, NOW)).rejects.toThrow(
      'Anki has no "Lapis" card made today',
    );

    collection.makeNote({
      id: NOW - 40 * 60_000,
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '前' },
    });
    await expect(addToNewest(anki, lapis(), CONTENT, MEDIA, NOW)).rejects.toThrow(
      'made 40 minutes ago',
    );
    expect(anki.cuts).toHaveLength(0);

    // With no limit it is taken.
    const settings = lapis();
    settings.anki.newestWithinMinutes = 0;
    await expect(addToNewest(anki, settings, CONTENT, MEDIA, NOW)).resolves.toMatchObject({
      label: '前',
    });
  });

  it('guesses the fields of a card of another note type', async () => {
    const collection = createCollection({ now: () => NOW });
    collection.models['Mining Note'] = [
      'Word',
      'Sentence',
      'Sentence Translation',
      'Audio',
      'Image',
    ];
    const id = collection.makeNote({
      id: NOW - 1_000,
      deck: 'Mining',
      modelName: 'Mining Note',
      fields: { Word: '約束' },
    });
    const settings = lapis();
    settings.anki.noteType = '';

    await addToNewest(ankiOver(collection), settings, CONTENT, { ...MEDIA, sound: undefined }, NOW);

    expect(collection.notes.get(id)!.fields).toEqual({
      Word: '約束',
      Sentence: '昨日の話、覚えてる？<br>もちろん。',
      'Sentence Translation': 'Do you remember?<br>Of course.',
      Audio: '',
      Image: '<img src="picture-item-1.bin">',
    });
  });

  it('makes a new card in the deck and note type of the settings', async () => {
    const collection = createCollection({ now: () => NOW });
    const settings = lapis();
    settings.anki.fields.Expression = 'sentence';
    settings.anki.tags = ['subtitle-miner', 'anime'];

    const outcome = await addNew(ankiOver(collection), settings, CONTENT, MEDIA);

    const note = collection.notes.get(outcome.noteId)!;
    expect(outcome).toMatchObject({ kind: 'added', label: '昨日の話、覚えてる？ もちろん。' });
    expect(note.deck).toBe('Mining');
    expect(note.tags).toEqual(['subtitle-miner', 'anime']);
    expect(note.fields.Sentence).toBe('昨日の話、覚えてる？<br>もちろん。');
    expect(note.fields.Picture).toBe('<img src="picture-item-1.bin">');

    // The same lines again: the deck has that card.
    await expect(addNew(ankiOver(collection), settings, CONTENT, MEDIA)).rejects.toThrow(
      'This deck has a card like this already.',
    );
  });

  it('says what a new card is missing', async () => {
    const anki = ankiOver(createCollection());
    const none = defaultPageSettings();
    await expect(addNew(anki, none, CONTENT, MEDIA)).rejects.toThrow(
      'Choose a deck and a note type',
    );

    // Lapis's first field is the word: empty, Anki will not make the card.
    await expect(addNew(anki, lapis(), CONTENT, MEDIA)).rejects.toThrow(
      'needs something in its first field',
    );
  });
});
