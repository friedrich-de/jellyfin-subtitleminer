import type { PictureFormat, SoundFormat } from '@/api/plugin';

/** What goes into a field of a card. */
export type FieldRole =
  | 'none'
  | 'sentence'
  | 'translation'
  | 'picture'
  | 'sound'
  | 'source'
  | 'word'
  | 'reading'
  | 'furigana'
  | 'meaning'
  | 'frequency'
  | 'pitch'
  | 'pitchCategories'
  | 'wordAudio'
  | 'selectionText';

/** The parts that only a card made from a word in the dictionary popup has. */
export const WORD_ROLES: ReadonlySet<FieldRole> = new Set([
  'word',
  'reading',
  'furigana',
  'meaning',
  'frequency',
  'pitch',
  'pitchCategories',
  'wordAudio',
  'selectionText',
]);

/**
 * The choices for a field, named as Lapis and most mining note types name their fields, each with
 * what it holds and an example of it.
 */
export const FIELD_ROLES: readonly { role: FieldRole; label: string }[] = [
  { role: 'none', label: 'Nothing: left as it is, or to Yomitan' },
  { role: 'sentence', label: 'Sentence: the chosen lines (e.g. 昨日の話をしよう)' },
  {
    role: 'translation',
    label: "SentenceTranslation: the second track's lines (e.g. Remember?)",
  },
  { role: 'sound', label: 'SentenceAudio: the sound of the lines (e.g. [sound:….mp3])' },
  { role: 'picture', label: 'Picture: a still from the video (e.g. <img src="….jpg">)' },
  { role: 'source', label: 'MiscInfo: where it is from (e.g. Show · S1 E3 · 04:12)' },
  { role: 'word', label: 'Expression: the word looked up (e.g. 食べる)' },
  { role: 'reading', label: 'ExpressionReading: its reading (e.g. たべる)' },
  { role: 'furigana', label: 'ExpressionFurigana: with its reading (e.g. 食[た]べる)' },
  { role: 'wordAudio', label: 'ExpressionAudio: its sound (e.g. [sound:….mp3])' },
  { role: 'meaning', label: 'MainDefinition: its meanings (e.g. to eat)' },
  { role: 'selectionText', label: 'SelectionText: what you select in the popup (e.g. to eat)' },
  { role: 'frequency', label: 'Frequency: how common it is (e.g. 512)' },
  { role: 'pitch', label: 'PitchPosition: its pitch accent (e.g. 2)' },
  { role: 'pitchCategories', label: 'PitchCategories: its pitch pattern (e.g. nakadaka)' },
];

export interface AnkiChoices {
  deck: string;
  noteType: string;
  /** For each field of the note type, what goes in it. */
  fields: Record<string, FieldRole>;
  tags: string[];
  /** "Add to the newest card" takes a card only if it was made this many minutes ago at most. */
  newestWithinMinutes: number;
}

export interface MediaChoices {
  pictureFormat: PictureFormat;
  pictureWidth: number;
  pictureQuality: number;
  /** A moving picture over the chosen lines instead of a still from the first. WebP or AVIF only. */
  moving: boolean;
  soundFormat: SoundFormat;
  normalize: boolean;
  /** The sound starts this much before the first line and ends this much after the last. */
  padBeforeMs: number;
  padAfterMs: number;
}

export interface DictionaryChoices {
  /** A tap on a word looks it up in the server's dictionaries. Off for those who use Yomitan. */
  lookUp: boolean;
}

/** What only the page reads, kept on the server with the user's other settings. */
export interface PageSettings {
  anki: AnkiChoices;
  media: MediaChoices;
  dictionary: DictionaryChoices;
}

export const PICTURE_WIDTHS = [480, 640, 800, 960, 1280] as const;

export function defaultPageSettings(): PageSettings {
  return {
    anki: { deck: '', noteType: '', fields: {}, tags: ['subtitle-miner'], newestWithinMinutes: 15 },
    media: {
      pictureFormat: 'jpeg',
      pictureWidth: 640,
      pictureQuality: 85,
      moving: false,
      soundFormat: 'mp3',
      normalize: false,
      padBeforeMs: 250,
      padAfterMs: 250,
    },
    dictionary: { lookUp: true },
  };
}

const ROLES = new Set<string>(FIELD_ROLES.map((entry) => entry.role));

function record(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

function text(value: unknown, fallback: string, max = 200): string {
  return typeof value === 'string' ? value.slice(0, max) : fallback;
}

function whole(value: unknown, fallback: number, min: number, max: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.min(max, Math.max(min, Math.round(value)))
    : fallback;
}

function oneOf<T extends string>(value: unknown, choices: readonly T[], fallback: T): T {
  return choices.includes(value as T) ? (value as T) : fallback;
}

/** Reads what the server kept, whatever it holds: what cannot be read takes the default. */
export function readPageSettings(stored: unknown): PageSettings {
  const fallback = defaultPageSettings();
  const raw = record(stored);
  const anki = record(raw.anki);
  const media = record(raw.media);
  const dictionary = record(raw.dictionary);

  const fields: Record<string, FieldRole> = {};
  for (const [name, role] of Object.entries(record(anki.fields)).slice(0, 64)) {
    if (typeof role === 'string' && ROLES.has(role)) fields[name] = role as FieldRole;
  }

  const tags = Array.isArray(anki.tags)
    ? anki.tags.filter((tag): tag is string => typeof tag === 'string' && tag !== '').slice(0, 20)
    : fallback.anki.tags;

  const pictureFormat = oneOf(media.pictureFormat, ['jpeg', 'webp', 'avif'], 'jpeg');
  return {
    anki: {
      deck: text(anki.deck, ''),
      noteType: text(anki.noteType, ''),
      fields,
      tags,
      newestWithinMinutes: whole(anki.newestWithinMinutes, 15, 0, 24 * 60),
    },
    media: {
      pictureFormat,
      pictureWidth: whole(media.pictureWidth, 640, 160, 1920),
      pictureQuality: whole(media.pictureQuality, 85, 1, 100),
      moving: media.moving === true && pictureFormat !== 'jpeg',
      soundFormat: oneOf(media.soundFormat, ['mp3', 'opus'], 'mp3'),
      normalize: media.normalize === true,
      padBeforeMs: whole(media.padBeforeMs, 250, 0, 2_000),
      padAfterMs: whole(media.padAfterMs, 250, 0, 2_000),
    },
    dictionary: { lookUp: dictionary.lookUp !== false },
  };
}

/** Tags as typed: split at spaces, as Anki does. */
export function tagsFrom(typed: string): string[] {
  return [...new Set(typed.split(/\s+/).filter((tag) => tag !== ''))].slice(0, 20);
}

const WORD = '(expression|word|vocab(ulary)?|term)';

/** What a field's name says it is for. A part that also goes into a second field says so. */
const GUESSES: readonly [RegExp, FieldRole, 'also'?][] = [
  [/^sentence([ _-]?(kanji|text|jp|ja|japanese))?$/i, 'sentence'],
  [/^(sentence[ _-]?(translation|meaning|english|eng|en)|translation)$/i, 'translation'],
  [/^((sentence[ _-]?)?audio|sentence[ _-]?sound|sound)$/i, 'sound'],
  [/^(picture|image|screenshot|snapshot)$/i, 'picture'],
  [/^(misc[ _-]?info|source|sentence[ _-]?source)$/i, 'source'],
  [new RegExp(`^(${WORD}|key)$`, 'i'), 'word'],
  [new RegExp(`^(${WORD}[ _-]?reading|reading|kana)$`, 'i'), 'reading'],
  [new RegExp(`^(${WORD}[ _-]?furigana|furigana)$`, 'i'), 'furigana'],
  [new RegExp(`^(${WORD}[ _-]?(audio|sound)|word[ _-]?pronunciation)$`, 'i'), 'wordAudio'],
  [
    new RegExp(
      `^(main[ _-]?definition|definition|meaning|glossary|${WORD}[ _-]?(meaning|definition|english))$`,
      'i',
    ),
    'meaning',
  ],
  [/^(selection[ _-]?text|selected[ _-]?(text|definition))$/i, 'selectionText'],
  [/^(freq(uency)?|frequencies)$/i, 'frequency'],
  // Lapis sorts new cards by it: the same number as the frequency.
  [/^freq(uency)?[ _-]?sort$/i, 'frequency', 'also'],
  [/^(pitch([ _-]?(position|accent))?|accent)$/i, 'pitch'],
  [/^pitch[ _-]?(categories|category|pattern)$/i, 'pitchCategories'],
];

/**
 * What goes into each field of a note type, by the names of its fields: the sentence into
 * "Sentence", the sound into "SentenceAudio", the word into "Expression", and so on, as Lapis
 * and most mining note types name them. Each part goes into one field at most, but for the
 * frequency, which Lapis also sorts by. The word's parts fill only cards made from the dictionary
 * popup; "Add to my newest card" leaves them to Yomitan.
 */
export function guessRoles(fieldNames: readonly string[]): Record<string, FieldRole> {
  return rolesFor(fieldNames, {});
}

/**
 * What goes into each field of a note type as it is now: the choices made before for the fields
 * still there, and guesses for new fields, for parts no field has yet.
 */
export function rolesFor(
  fieldNames: readonly string[],
  chosen: Readonly<Record<string, FieldRole>>,
): Record<string, FieldRole> {
  const kept = fieldNames.filter((name) => Object.hasOwn(chosen, name));
  const taken = new Set<FieldRole>(kept.map((name) => chosen[name]!));
  const roles: Record<string, FieldRole> = {};
  for (const name of fieldNames) {
    if (Object.hasOwn(chosen, name)) {
      roles[name] = chosen[name]!;
      continue;
    }
    const [, role, also] = GUESSES.find(([pattern]) => pattern.test(name.trim())) ?? [];
    if (role === undefined) {
      roles[name] = 'none';
    } else if (also === 'also') {
      roles[name] = role;
    } else {
      roles[name] = taken.has(role) ? 'none' : role;
      taken.add(role);
    }
  }
  return roles;
}
