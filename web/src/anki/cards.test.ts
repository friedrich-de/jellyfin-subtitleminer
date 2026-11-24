import { describe, expect, it } from 'vitest';

import { type Collection, createCollection } from '@/testing/ankiCollection';

import type { WordContent } from '@/dictionary/word';

import {
  addNew,
  addToNewest,
  type CardMedia,
  DuplicateCardError,
  fieldHtml,
  keepMarks,
  newestCard,
  plainText,
  sentenceWithWord,
  undoUpdate,
  wordsInDeck,
} from './cards';
import { type AnkiConnect, AnkiError, type MediaCut, WordAudioMissingError } from './connect';
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
      if (cut.kind === 'word-audio' && cut.term === '無音') {
        return Promise.reject(new WordAudioMissingError('No sound.'));
      }
      const name = `${cut.kind}-${cut.kind === 'word-audio' ? cut.term : cut.itemId}.bin`;
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

  it('names the card it would change before anything is sent', async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);
    await expect(newestCard(anki, lapis(), NOW)).rejects.toThrow('Anki has no "Lapis" card');

    collection.makeNote({
      id: NOW - 3 * 60_000 - 5_000,
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '<b>約束</b>', Sentence: '約束したからね。' },
    });
    const found = await newestCard(anki, lapis(), NOW);

    expect(found).toMatchObject({ label: '約束', minutes: 3 });
    expect(found.roles.Sentence).toBe('sentence');
    // Looking changes nothing.
    expect(anki.cuts).toEqual([]);
    expect(collection.notes.get(found.info.noteId)!.fields.Sentence).toBe('約束したからね。');
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

    // The same lines again: the deck has that card, and Anki is given no picture or sound for it.
    const again = ankiOver(collection);
    await expect(addNew(again, settings, CONTENT, MEDIA)).rejects.toThrow(
      'This deck has a card like this already.',
    );
    expect(again.cuts).toEqual([]);
  });

  it('puts the lines in a first field meant for the word, for a card of lines alone', async () => {
    const collection = createCollection({ now: () => NOW });

    // Lapis as guessed: its first field is the word, which Anki needs.
    const outcome = await addNew(ankiOver(collection), lapis(), CONTENT, MEDIA);

    expect(outcome).toMatchObject({ kind: 'added', label: '昨日の話、覚えてる？ もちろん。' });
    expect(collection.notes.get(outcome.noteId)!.fields).toMatchObject({
      Expression: '昨日の話、覚えてる？<br>もちろん。',
      ExpressionReading: '',
      Sentence: '昨日の話、覚えてる？<br>もちろん。',
      Picture: '<img src="picture-item-1.bin">',
    });
  });

  it('says what a new card is missing, before its media are stored', async () => {
    const anki = ankiOver(createCollection());
    const none = defaultPageSettings();
    await expect(addNew(anki, none, CONTENT, MEDIA)).rejects.toThrow(
      'Choose a deck and a note type',
    );

    // A first field that is for nothing the page fills: Anki would not make the card.
    const settings = lapis();
    settings.anki.fields.Expression = 'none';
    await expect(addNew(anki, settings, CONTENT, MEDIA)).rejects.toThrow(
      'A new "Lapis" card needs something in its first field. Give it the lines in the settings.',
    );
    expect(anki.cuts).toEqual([]);
  });
});

const EAT: WordContent = {
  expression: '食べる',
  reading: 'たべる',
  furigana: '食[た]べる',
  meaning: '<div class="yomitan-glossary" style="text-align: left">to eat</div>',
  frequency: '512',
  pitch: '2',
  pitchCategories: 'kifuku',
  at: { start: 2, length: 8 },
};

const EATEN = {
  sentence: 'もう食べられなかった。',
  translation: 'I could not eat any more.',
  source: 'Sample Show · S1 E1 · 00:04',
  word: EAT,
};

describe('making a card of a word from the dictionary', () => {
  it('puts the word in bold in its sentence', () => {
    expect(sentenceWithWord('もう食べられなかった。', EAT.at)).toBe(
      'もう<b>食べられなかった</b>。',
    );
    expect(sentenceWithWord(' 昨日の\n<話> ', { start: 5, length: 3 })).toBe(
      '昨日の<br><b>&lt;話&gt;</b>',
    );
    // A place outside the sentence marks nothing.
    expect(sentenceWithWord('昨日', { start: 5, length: 2 })).toBe('昨日');
  });

  it('fills the word, its reading, meaning, frequency and pitch with the lines and the media', async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);

    const outcome = await addNew(anki, lapis(), EATEN, MEDIA);

    expect(outcome).toMatchObject({ kind: 'added', label: '食べる' });
    expect(collection.notes.get(outcome.noteId)!.fields).toMatchObject({
      Expression: '食べる',
      ExpressionFurigana: '食[た]べる',
      ExpressionReading: 'たべる',
      MainDefinition: EAT.meaning,
      Sentence: 'もう<b>食べられなかった</b>。',
      SentenceAudio: '[sound:sound-item-1.bin]',
      Picture: '<img src="picture-item-1.bin">',
      PitchPosition: '2',
      Frequency: '512',
      MiscInfo: 'Sample Show · S1 E1 · 00:04',
      Glossary: '',
    });

    // The deck has that word now: Anki says so before a card is made, and when one is.
    const other = { ...EATEN, word: { ...EAT, expression: '食う' } };
    await expect(wordsInDeck(anki, lapis(), [EATEN, other])).resolves.toEqual([true, false]);
    const before = collection.media.size;
    await expect(addNew(anki, lapis(), EATEN, MEDIA)).rejects.toThrow(DuplicateCardError);
    expect(collection.media.size).toBe(before);
  });

  it("gives the word's sound to its field when the server has an audio source", async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);

    const outcome = await addNew(anki, lapis(), EATEN, { ...MEDIA, wordAudio: true });

    expect(outcome.noWordAudio).toBeUndefined();
    expect(collection.notes.get(outcome.noteId)!.fields).toMatchObject({
      ExpressionAudio: '[sound:word-audio-食べる.bin]',
      SentenceAudio: '[sound:sound-item-1.bin]',
    });
    expect(anki.cuts).toContainEqual({ kind: 'word-audio', term: '食べる', reading: 'たべる' });

    // The sound chosen in the popup is the one fetched.
    const other = ankiOver(createCollection({ now: () => NOW }));
    await addNew(other, lapis(), EATEN, { ...MEDIA, wordAudio: true, wordAudioChoice: 'jpod' });
    expect(other.cuts).toContainEqual({
      kind: 'word-audio',
      term: '食べる',
      reading: 'たべる',
      choice: 'jpod',
    });
  });

  it('puts only the part of a definition selected in the popup into SelectionText', async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);
    const picked = { ...EATEN, word: { ...EAT, selection: ' to eat\n(food) ' } };

    const first = await addNew(anki, lapis(), picked, MEDIA);
    expect(collection.notes.get(first.noteId)!.fields).toMatchObject({
      SelectionText: 'to eat<br>(food)',
      MainDefinition: EAT.meaning,
    });

    // Nothing selected leaves it empty, and Lapis shows MainDefinition.
    const plain = { ...EATEN, word: { ...EAT, expression: '食う' } };
    const second = await addNew(anki, lapis(), plain, MEDIA);
    expect(collection.notes.get(second.noteId)!.fields.SelectionText).toBe('');
  });

  it('makes the card of a word without a sound, and says so', async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);
    const quiet = { ...EATEN, word: { ...EAT, expression: '無音' } };

    const outcome = await addNew(anki, lapis(), quiet, { ...MEDIA, wordAudio: true });

    expect(outcome.noWordAudio).toBe(true);
    expect(collection.notes.get(outcome.noteId)!.fields).toMatchObject({
      Expression: '無音',
      ExpressionAudio: '',
      Picture: '<img src="picture-item-1.bin">',
    });
  });

  it('asks for no sound of the word without an audio source, nor for a card of lines', async () => {
    const collection = createCollection({ now: () => NOW });
    const anki = ankiOver(collection);

    await addNew(anki, lapis(), EATEN, MEDIA);
    await addNew(anki, lapis(), CONTENT, { ...MEDIA, wordAudio: true });

    expect(anki.cuts.map((cut) => cut.kind)).not.toContain('word-audio');
  });

  it('needs a field for the word', async () => {
    const anki = ankiOver(createCollection());
    const settings = lapis();
    settings.anki.fields.Expression = 'sentence';

    await expect(addNew(anki, settings, EATEN, MEDIA)).rejects.toThrow(
      'Give the word a field in the settings first.',
    );
    // Nothing to ask Anki then.
    await expect(wordsInDeck(anki, settings, [EATEN])).resolves.toEqual([false]);
  });
});
