import { describe, expect, it } from 'vitest';

import { LAPIS_FIELDS } from '@/testing/ankiCollection';

import {
  defaultPageSettings,
  guessRoles,
  readPageSettings,
  rolesFor,
  tagsFrom,
} from './pageSettings';

describe('the page settings', () => {
  it('reads what the server kept, and takes the default for what it cannot read', () => {
    expect(readPageSettings(undefined)).toEqual(defaultPageSettings());
    expect(readPageSettings('nonsense')).toEqual(defaultPageSettings());
    expect(readPageSettings({ dictionary: { lookUp: false } }).dictionary.lookUp).toBe(false);
    expect(readPageSettings({ dictionary: { lookUp: 'no' } }).dictionary.lookUp).toBe(true);
    expect(readPageSettings({}).dictionary).toEqual({
      lookUp: true,
      textSize: 100,
      hideTags: false,
      compact: false,
    });
    expect(
      readPageSettings({ dictionary: { textSize: 10, hideTags: true, compact: 'yes' } }).dictionary,
    ).toMatchObject({ textSize: 70, hideTags: true, compact: false });
    expect(readPageSettings({ dictionary: { textSize: 500 } }).dictionary.textSize).toBe(130);

    const read = readPageSettings({
      anki: {
        deck: 'Mining',
        noteType: 'Lapis',
        fields: { Sentence: 'sentence', Picture: 'picture', Hint: 'everything' },
        tags: ['anime', 3, ''],
        newestWithinMinutes: 99_999,
      },
      media: {
        pictureFormat: 'gif',
        pictureWidth: 10,
        moving: true,
        padBeforeMs: -5,
        soundFormat: 'opus',
      },
    });

    expect(read.anki).toEqual({
      deck: 'Mining',
      noteType: 'Lapis',
      fields: { Sentence: 'sentence', Picture: 'picture' },
      tags: ['anime'],
      newestWithinMinutes: 1_440,
    });
    // A JPEG does not move; the rest is held to its range.
    expect(read.media).toMatchObject({
      pictureFormat: 'jpeg',
      pictureWidth: 160,
      moving: false,
      padBeforeMs: 0,
      soundFormat: 'opus',
    });
  });

  it('guesses where the parts go in a Lapis note', () => {
    const roles = guessRoles(LAPIS_FIELDS);

    expect(Object.entries(roles).filter(([, role]) => role !== 'none')).toEqual([
      ['Expression', 'word'],
      ['ExpressionFurigana', 'furigana'],
      ['ExpressionReading', 'reading'],
      ['ExpressionAudio', 'wordAudio'],
      ['SelectionText', 'selectionText'],
      ['MainDefinition', 'meaning'],
      ['Sentence', 'sentence'],
      ['SentenceAudio', 'sound'],
      ['Picture', 'picture'],
      ['PitchPosition', 'pitch'],
      ['PitchCategories', 'pitchCategories'],
      ['Frequency', 'frequency'],
      // Lapis sorts new cards by the frequency: it goes there too.
      ['FreqSort', 'frequency'],
      ['MiscInfo', 'source'],
    ]);
    // The second list of meanings is the first's.
    expect(roles.Glossary).toBe('none');
  });

  it('guesses other common names, each part into one field', () => {
    expect(
      guessRoles([
        'Front',
        'Word',
        'Word Reading',
        'Sentence',
        'Sentence Translation',
        'Meaning',
        'Audio',
        'Image',
        'Sound',
        'Pitch Accent',
        'Freq',
        'Word Audio',
      ]),
    ).toEqual({
      Front: 'none',
      Word: 'word',
      'Word Reading': 'reading',
      Sentence: 'sentence',
      'Sentence Translation': 'translation',
      Meaning: 'meaning',
      Audio: 'sound',
      Image: 'picture',
      Sound: 'none',
      'Pitch Accent': 'pitch',
      Freq: 'frequency',
      'Word Audio': 'wordAudio',
    });
  });

  it('keeps the choices for the fields still there and guesses only new ones', () => {
    const chosen = { Front: 'sentence', Back: 'none', Gone: 'picture' } as const;

    // A field was added to the note type since: the choices stand, the new field is guessed.
    expect(rolesFor(['Front', 'Back', 'Audio'], chosen)).toEqual({
      Front: 'sentence',
      Back: 'none',
      Audio: 'sound',
    });
    // A part already in a field is not guessed into another.
    expect(rolesFor(['Front', 'Back', 'Sentence'], chosen)).toEqual({
      Front: 'sentence',
      Back: 'none',
      Sentence: 'none',
    });
  });

  it('splits tags at spaces as Anki does', () => {
    expect(tagsFrom('  anime  subtitle-miner anime ')).toEqual(['anime', 'subtitle-miner']);
  });
});
