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
  /**
   * The patterns of those pitches as Yomitan's {pitch-accent-categories} names them, which Lapis
   * colours the word by: "heiban" or "atamadaka,nakadaka"; "" without one.
   */
  pitchCategories: string;
  /** Where the word is in the sentence, as it was found there. */
  at: { start: number; length: number };
  /** The part of its definitions selected in the popup when the card was made, as Yomitan's {selection-text}: "" without one. */
  selection?: string;
}

/** Small kana that join the kana before them into one mora. っ is a mora of its own. */
const SMALL_KANA = new Set('ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ');

function moraCount(reading: string): number {
  let count = 0;
  for (const character of reading) {
    if (!(SMALL_KANA.has(character) && count > 0)) count += 1;
  }
  return count;
}

/**
 * Whether a word of these parts of speech is a verb or an adjective, whose pitch Yomitan calls
 * kifuku when it falls at all: a noun that takes する is not.
 */
function isVerbOrAdjective(rules: string): boolean {
  const parts = new Set(rules.split(/\s+/));
  const verbOrAdjective = ['v1', 'v5', 'vk', 'vz', 'adj-i', 'vs'].some((part) => parts.has(part));
  return verbOrAdjective && !(parts.has('vs') && parts.has('n'));
}

/** The name of a pitch pattern, as Yomitan's getPitchCategory gives it. */
export function pitchCategory(
  reading: string,
  position: number,
  verbOrAdjective: boolean,
): string | undefined {
  if (position === 0) return 'heiban';
  if (verbOrAdjective) return position > 0 ? 'kifuku' : undefined;
  if (position === 1) return 'atamadaka';
  if (position > 1) return position >= moraCount(reading) ? 'odaka' : 'nakadaka';
  return undefined;
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
    pitchCategories: [
      ...new Set(
        pitches.flatMap(
          (pitch) =>
            pitchCategory(reading, pitch.position, isVerbOrAdjective(result.term.rules)) ?? [],
        ),
      ),
    ].join(','),
    at: { start, length: result.matched.length },
  };
}
