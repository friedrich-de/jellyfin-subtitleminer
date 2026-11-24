import { describe, expect, it } from 'vitest';

import { LAPIS_FIELDS } from '@/testing/ankiCollection';

import { defaultPageSettings, guessRoles, readPageSettings, tagsFrom } from './pageSettings';

describe('the page settings', () => {
  it('reads what the server kept, and takes the default for what it cannot read', () => {
    expect(readPageSettings(undefined)).toEqual(defaultPageSettings());
    expect(readPageSettings('nonsense')).toEqual(defaultPageSettings());

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

  it('guesses where the parts go in a Lapis note and leaves the word to Yomitan', () => {
    const roles = guessRoles(LAPIS_FIELDS);

    expect(Object.entries(roles).filter(([, role]) => role !== 'none')).toEqual([
      ['Sentence', 'sentence'],
      ['SentenceAudio', 'sound'],
      ['Picture', 'picture'],
      ['MiscInfo', 'source'],
    ]);
    expect(roles.Expression).toBe('none');
    expect(roles.ExpressionAudio).toBe('none');
  });

  it('guesses other common names, each part into one field', () => {
    expect(
      guessRoles(['Front', 'Sentence', 'Sentence Translation', 'Audio', 'Image', 'Sound']),
    ).toEqual({
      Front: 'none',
      Sentence: 'sentence',
      'Sentence Translation': 'translation',
      Audio: 'sound',
      Image: 'picture',
      Sound: 'none',
    });
  });

  it('splits tags at spaces as Anki does', () => {
    expect(tagsFrom('  anime  subtitle-miner anime ')).toEqual(['anime', 'subtitle-miner']);
  });
});
