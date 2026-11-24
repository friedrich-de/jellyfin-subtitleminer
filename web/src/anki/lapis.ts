import { type AnkiConnect, AnkiError } from './connect';
import { type FieldRole, guessRoles } from './pageSettings';

/**
 * The fields that make a note type Lapis, whatever it is called: Lapis itself, Kiku, which is
 * built on it, and the copies people make of either.
 */
const LAPIS_SIGNATURE = [
  'Expression',
  'ExpressionFurigana',
  'ExpressionReading',
  'ExpressionAudio',
  'SelectionText',
  'MainDefinition',
  'Sentence',
  'SentenceAudio',
  'Picture',
  'IsSentenceCard',
  'MiscInfo',
] as const;

/** How many note types are looked into at most, so that a large collection is not asked for long. */
const MOST_LOOKED_INTO = 30;

/** A note type, deck and fields chosen for someone who has not chosen any. */
export interface FoundNoteType {
  noteType: string;
  /** The deck of the newest card of that note type, or the one deck there is; "" when unsure. */
  deck: string;
  fieldNames: string[];
  fields: Record<string, FieldRole>;
}

export function isLapis(fieldNames: readonly string[]): boolean {
  const names = new Set(fieldNames);
  return LAPIS_SIGNATURE.every((name) => names.has(name));
}

function escaped(text: string): string {
  return text.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

/** Lapis first by name, then Kiku and the rest, each in the order Anki gives them. */
function byLikelihood(noteTypes: readonly string[]): string[] {
  const rank = (name: string) =>
    name === 'Lapis' ? 0 : /lapis/i.test(name) ? 1 : /kiku/i.test(name) ? 2 : 3;
  return [...noteTypes].sort((a, b) => rank(a) - rank(b)).slice(0, MOST_LOOKED_INTO);
}

/** The deck the newest card of this note type is in, if Anki says. */
async function deckOfNewest(
  anki: AnkiConnect,
  noteType: string,
  decks: readonly string[],
): Promise<string | undefined> {
  try {
    const ids = await anki.invoke<number[]>('findNotes', { query: `note:"${escaped(noteType)}"` });
    if (ids.length === 0) return undefined;
    const newest = Math.max(...ids);
    const [info] = await anki.invoke<{ cards?: number[] }[]>('notesInfo', { notes: [newest] });
    const card = info?.cards?.[0];
    if (card === undefined) return undefined;
    const [cardInfo] = await anki.invoke<{ deckName?: string }[]>('cardsInfo', { cards: [card] });
    const deck = cardInfo?.deckName;
    return deck !== undefined && decks.includes(deck) ? deck : undefined;
  } catch (error) {
    // An AnkiConnect that does not answer one of these leaves the deck to be chosen.
    if (error instanceof AnkiError && !error.unreachable) return undefined;
    throw error;
  }
}

/** Where cards go when no card says: the one deck made besides Default, or Default alone. */
function onlyDeck(decks: readonly string[]): string {
  const made = decks.filter((deck) => deck !== 'Default');
  if (made.length === 1) return made[0]!;
  return made.length === 0 && decks.includes('Default') ? 'Default' : '';
}

/**
 * A Lapis note type in this Anki, with the deck its cards go into and each field given what
 * fills it best, for someone who has not chosen a note type: nothing when there is none.
 */
export async function findLapis(
  anki: AnkiConnect,
  noteTypes: readonly string[],
  decks: readonly string[],
): Promise<FoundNoteType | undefined> {
  for (const noteType of byLikelihood(noteTypes)) {
    let fieldNames: string[];
    try {
      fieldNames = await anki.invoke<string[]>('modelFieldNames', { modelName: noteType });
    } catch (error) {
      if (error instanceof AnkiError && !error.unreachable) continue;
      throw error;
    }
    if (!isLapis(fieldNames)) continue;
    const deck = (await deckOfNewest(anki, noteType, decks)) ?? onlyDeck(decks);
    return { noteType, deck, fieldNames, fields: guessRoles(fieldNames) };
  }
  return undefined;
}
