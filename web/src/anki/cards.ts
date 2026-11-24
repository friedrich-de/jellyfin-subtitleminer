import type { PictureOptions, SoundOptions } from '@/api/plugin';
import type { WordContent } from '@/dictionary/word';

import { AnkiError, type AnkiConnect, type MediaCut } from './connect';
import { type FieldRole, guessRoles, type PageSettings, WORD_ROLES } from './pageSettings';

/** What a card gets from the chosen lines. */
export interface CardContent {
  /** The lines, a line feed between them. */
  sentence: string;
  /** The second track's lines said at the same time, if any. */
  translation: string;
  /** Where it is from: "Sample Show · S1 E1 · 00:04". */
  source: string;
  /** The word looked up in the dictionary popup, for a card made from it. */
  word?: WordContent;
}

/** Anki has a card like this in the deck already. */
export class DuplicateCardError extends AnkiError {
  constructor() {
    super('This deck has a card like this already.');
    this.name = 'DuplicateCardError';
  }
}

/** The video and the times the picture and the sound are cut from. */
export interface CardMedia {
  itemId: string;
  source: string;
  picture: PictureOptions | undefined;
  sound: SoundOptions | undefined;
}

export interface NoteInfo {
  noteId: number;
  modelName: string;
  fields: Record<string, { value: string; order: number }>;
}

/** What a send did, for the message and for taking it back. */
export interface CardOutcome {
  kind: 'updated' | 'added';
  noteId: number;
  /** The card's first field as text, to name it. */
  label: string;
  /** The fields as they were before, for an update. */
  before: Record<string, string>;
}

export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** Text for a field: escaped, with its line feeds as breaks. */
export function fieldHtml(text: string): string {
  return escapeHtml(text.trim()).replace(/\r?\n/g, '<br>');
}

/** The sentence for a field with the word in bold, as Yomitan writes it. */
export function sentenceWithWord(text: string, at: { start: number; length: number }): string {
  const end = Math.min(text.length, at.start + at.length);
  if (at.start < 0 || at.length <= 0 || at.start >= end) return fieldHtml(text);
  const html = (part: string): string => escapeHtml(part).replace(/\r?\n/g, '<br>');
  const before = text.slice(0, at.start).trimStart();
  const after = text.slice(end).trimEnd();
  return `${html(before)}<b>${html(text.slice(at.start, end))}</b>${html(after)}`;
}

/** A field's HTML as plain text, to name a card. */
export function plainText(html: string): string {
  const text = html
    .replace(/<br\s*\/?>/gi, ' ')
    .replace(/<[^>]*>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > 40 ? `${text.slice(0, 40)}…` : text;
}

/**
 * The chosen lines as a sentence field, keeping the marks the field had: Yomitan writes the
 * sentence with the looked-up word in bold, and the word stays bold in the longer sentence.
 */
export function keepMarks(original: string, sentenceHtml: string): string {
  let result = sentenceHtml;
  let from = 0;
  const marked = /<(b|strong|u|i|em|span)(\s[^>]*)?>([^<]+)<\/\1>/gi;
  for (const match of original.matchAll(marked)) {
    const [whole, tag, attributes = '', inner = ''] = match;
    if (inner.trim() === '' || whole === undefined || tag === undefined) continue;
    const at = result.indexOf(inner, from);
    if (at === -1) continue;
    const replacement = `<${tag}${attributes}>${inner}</${tag}>`;
    result = result.slice(0, at) + replacement + result.slice(at + inner.length);
    from = at + replacement.length;
  }
  return result;
}

function query(term: string): string {
  return `"${term.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

async function storeAll(
  anki: AnkiConnect,
  roles: Record<string, FieldRole>,
  media: CardMedia,
): Promise<{ picture?: string; sound?: string }> {
  const wanted = new Set(Object.values(roles));
  const cuts: Promise<[keyof CardMedia & ('picture' | 'sound'), string]>[] = [];
  const base = { itemId: media.itemId, source: media.source };
  if (wanted.has('picture') && media.picture !== undefined) {
    const cut: MediaCut = { kind: 'picture', ...base, options: media.picture };
    cuts.push(anki.store(cut).then((name) => ['picture', name]));
  }
  if (wanted.has('sound') && media.sound !== undefined) {
    const cut: MediaCut = { kind: 'sound', ...base, options: media.sound };
    cuts.push(anki.store(cut).then((name) => ['sound', name]));
  }
  return Object.fromEntries(await Promise.all(cuts));
}

function valueFor(
  role: FieldRole,
  content: CardContent,
  stored: { picture?: string; sound?: string },
  existing: string,
): string | undefined {
  const word = content.word;
  switch (role) {
    case 'sentence': {
      if (word !== undefined) return sentenceWithWord(content.sentence, word.at);
      const sentence = fieldHtml(content.sentence);
      return existing.trim() === '' ? sentence : keepMarks(existing, sentence);
    }
    case 'word':
      return word === undefined ? undefined : escapeHtml(word.expression);
    case 'reading':
      return word === undefined ? undefined : escapeHtml(word.reading);
    case 'furigana':
      return word === undefined ? undefined : escapeHtml(word.furigana);
    case 'meaning':
      return word?.meaning;
    case 'frequency':
      return word?.frequency;
    case 'pitch':
      return word?.pitch;
    case 'translation':
      return content.translation.trim() === '' ? undefined : fieldHtml(content.translation);
    case 'picture':
      return stored.picture === undefined ? undefined : `<img src="${escapeHtml(stored.picture)}">`;
    case 'sound':
      return stored.sound === undefined ? undefined : `[sound:${stored.sound}]`;
    case 'source':
      return fieldHtml(content.source);
    default:
      return undefined;
  }
}

/** The card that "Add to the newest card" would change, or why there is none. */
export async function newestNote(
  anki: AnkiConnect,
  settings: PageSettings,
  now: number,
): Promise<NoteInfo> {
  const noteType = settings.anki.noteType;
  const ids = await anki.invoke<number[]>('findNotes', {
    query: noteType === '' ? 'added:1' : `added:1 note:${query(noteType)}`,
  });
  const newest = ids.length === 0 ? undefined : Math.max(...ids);
  if (newest === undefined) {
    throw new AnkiError(
      noteType === ''
        ? 'Anki has no card made today. Make one with Yomitan first, or make a new card.'
        : `Anki has no "${noteType}" card made today. Make one with Yomitan first, or make a new card.`,
    );
  }

  // A note's id is the moment it was made.
  const within = settings.anki.newestWithinMinutes;
  const minutes = Math.floor((now - newest) / 60_000);
  if (within > 0 && minutes > within) {
    throw new AnkiError(
      `Your newest card was made ${minutes} minutes ago, which is too long ago to be the one you mean. Make the card first, or make a new card.`,
    );
  }

  const [info] = await anki.invoke<NoteInfo[]>('notesInfo', { notes: [newest] });
  if (info?.fields === undefined) throw new AnkiError('Anki did not say what is on the card.');
  return info;
}

function labelOf(info: Pick<NoteInfo, 'fields'>): string {
  const first = Object.values(info.fields).sort((a, b) => a.order - b.order)[0];
  return plainText(first?.value ?? '');
}

/**
 * Adds the chosen lines, the picture and the sound to the newest card, as made with Yomitan a
 * moment ago. The fields the settings give no part are left as they are.
 */
export async function addToNewest(
  anki: AnkiConnect,
  settings: PageSettings,
  content: CardContent,
  media: CardMedia,
  now: number,
): Promise<CardOutcome> {
  const info = await newestNote(anki, settings, now);
  const names = Object.keys(info.fields);
  const chosen = info.modelName === settings.anki.noteType ? settings.anki.fields : {};
  const roles = Object.keys(chosen).length > 0 ? chosen : guessRoles(names);
  const present = Object.fromEntries(
    Object.entries(roles).filter(([name]) => names.includes(name)),
  );
  if (!Object.values(present).some((role) => role !== 'none')) {
    throw new AnkiError(
      `The settings put nothing into the fields of "${info.modelName}" cards. Choose what goes where in the settings.`,
    );
  }

  const stored = await storeAll(anki, present, media);
  const fields: Record<string, string> = {};
  const before: Record<string, string> = {};
  for (const [name, role] of Object.entries(present)) {
    const existing = info.fields[name]?.value ?? '';
    const value = valueFor(role, content, stored, existing);
    if (value === undefined) continue;
    fields[name] = value;
    before[name] = existing;
  }

  await anki.invoke('updateNoteFields', { note: { id: info.noteId, fields } });
  return { kind: 'updated', noteId: info.noteId, label: labelOf(info), before };
}

/** Puts the fields of a card back as they were before an update. */
export async function undoUpdate(anki: AnkiConnect, outcome: CardOutcome): Promise<void> {
  await anki.invoke('updateNoteFields', { note: { id: outcome.noteId, fields: outcome.before } });
}

/** Why a new card cannot be made with these settings, or nothing when it can. */
export function newCardProblem(settings: PageSettings, withWord: boolean): string | undefined {
  const { deck, noteType, fields: roles } = settings.anki;
  if (deck === '' || noteType === '') return 'Choose a deck and a note type in the settings first.';
  const needed = withWord ? 'word' : 'sentence';
  if (!Object.values(roles).includes(needed)) {
    return withWord
      ? 'Give the word a field in the settings first.'
      : 'Give the lines a field in the settings first.';
  }
  return undefined;
}

function noteOf(settings: PageSettings, fields: Record<string, string>): Record<string, unknown> {
  const { deck, noteType, tags } = settings.anki;
  return {
    deckName: deck,
    modelName: noteType,
    fields,
    tags,
    options: { allowDuplicate: false, duplicateScope: 'deck' },
  };
}

/**
 * Whether Anki has a card for each of these words in the deck already, as a new card would be
 * made: true where it has. The media are left out, which Anki does not compare.
 */
export async function wordsInDeck(
  anki: AnkiConnect,
  settings: PageSettings,
  contents: readonly CardContent[],
): Promise<boolean[]> {
  if (contents.length === 0 || newCardProblem(settings, true) !== undefined) {
    return contents.map(() => false);
  }
  const notes = contents.map((content) => {
    const fields: Record<string, string> = {};
    for (const [name, role] of Object.entries(settings.anki.fields)) {
      fields[name] = valueFor(role, content, {}, '') ?? '';
    }
    return noteOf(settings, fields);
  });
  const answers = await anki.invoke<{ canAdd: boolean; error?: string }[]>(
    'canAddNotesWithErrorDetail',
    { notes },
  );
  return contents.map((_, index) => {
    const answer = answers[index];
    return answer?.canAdd === false && /duplicate/i.test(answer.error ?? '');
  });
}

/** Makes a new card of the chosen lines in the deck and note type of the settings. */
export async function addNew(
  anki: AnkiConnect,
  settings: PageSettings,
  content: CardContent,
  media: CardMedia,
): Promise<CardOutcome> {
  const { noteType, fields: roles } = settings.anki;
  const problem = newCardProblem(settings, content.word !== undefined);
  if (problem !== undefined) throw new AnkiError(problem);

  // The words first: a card Anki would refuse gets no picture or sound stored for nothing.
  const fields: Record<string, string> = {};
  for (const [name, role] of Object.entries(roles)) {
    fields[name] = MEDIA_ROLES.has(role) ? '' : (valueFor(role, content, {}, '') ?? '');
  }
  const firstName = Object.keys(fields)[0];
  const firstRole = firstName === undefined ? undefined : roles[firstName];
  if (firstName !== undefined && fields[firstName] === '' && content.word === undefined) {
    // Anki needs the first field. Where it is for the word, a card of lines alone has the lines.
    if (firstRole !== undefined && WORD_ROLES.has(firstRole)) {
      fields[firstName] = fieldHtml(content.sentence);
    }
  }
  const empty = new AnkiError(
    content.word === undefined
      ? `A new "${noteType}" card needs something in its first field. Give it the lines in the settings.`
      : `A new "${noteType}" card needs something in its first field. Give it the word in the settings.`,
  );
  const mediaFirst = firstRole !== undefined && MEDIA_ROLES.has(firstRole);
  if (firstName === undefined || (!mediaFirst && plainText(fields[firstName] ?? '') === '')) {
    throw empty;
  }
  await refuseDuplicate(anki, settings, fields);

  const stored = await storeAll(anki, roles, media);
  for (const [name, role] of Object.entries(roles)) {
    if (MEDIA_ROLES.has(role)) fields[name] = valueFor(role, content, stored, '') ?? '';
  }

  let noteId: number;
  try {
    noteId = await anki.invoke<number>('addNote', { note: noteOf(settings, fields) });
  } catch (error) {
    if (error instanceof AnkiError && /empty/i.test(error.message)) throw empty;
    if (error instanceof AnkiError && /duplicate/i.test(error.message)) {
      throw new DuplicateCardError();
    }
    throw error;
  }

  return { kind: 'added', noteId, label: plainText(fields[firstName] ?? ''), before: {} };
}

/** The parts a card gets as files in Anki's media folder. */
const MEDIA_ROLES: ReadonlySet<FieldRole> = new Set(['picture', 'sound']);

/**
 * Asks Anki whether it has this card in the deck already, before its media are stored. An
 * AnkiConnect that does not know the question is left to say so when the card is added.
 */
async function refuseDuplicate(
  anki: AnkiConnect,
  settings: PageSettings,
  fields: Record<string, string>,
): Promise<void> {
  let answers: { canAdd: boolean; error?: string }[];
  try {
    answers = await anki.invoke('canAddNotesWithErrorDetail', {
      notes: [noteOf(settings, fields)],
    });
  } catch {
    return;
  }
  const [answer] = answers;
  if (answer?.canAdd === false && /duplicate/i.test(answer.error ?? '')) {
    throw new DuplicateCardError();
  }
}
