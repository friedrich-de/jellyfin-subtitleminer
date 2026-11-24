import type { LookupResult } from '@/api/types';

import { furiganaOf } from './furigana';
import { meaningHtml } from './glossaryHtml';

/** What a card gets from a word looked up in the popup. */
export interface WordContent {
  /** The word as the dictionary writes it: 食べる. */
  expression: string;
  reading: string;
  /** In Anki's furigana: 食[た]べる. */
  furigana: string;
  /** Its definitions in its first dictionary, as HTML. */
  meaning: string;
  /** Its frequency in the first dictionary that has one, as a number: "" without one. */
  frequency: string;
  /** Where its pitch falls, in the first dictionary that has it: "0" or "2, 3"; "" without one. */
  pitch: string;
  /** Where the word is in the sentence, as it was found there. */
  at: { start: number; length: number };
}

export function wordOf(result: LookupResult, start: number): WordContent {
  const { expression, reading } = result.term;
  const frequency = result.term.frequencies.find((group) => group.frequencies.length > 0)
    ?.frequencies[0]?.value;
  const pitches = result.term.pitches.find((group) => group.pitches.length > 0)?.pitches ?? [];
  return {
    expression,
    reading,
    furigana: furiganaOf(expression, reading),
    meaning: meaningHtml(result),
    frequency:
      frequency === undefined || !Number.isFinite(frequency) ? '' : String(Math.round(frequency)),
    pitch: [...new Set(pitches.map((pitch) => pitch.position))].join(', '),
    at: { start, length: result.matched.length },
  };
}
