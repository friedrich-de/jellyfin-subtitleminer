import type { PictureFormat, SoundFormat } from '@/api/plugin';

/** What goes into a field of a card. */
export type FieldRole = 'none' | 'sentence' | 'translation' | 'picture' | 'sound' | 'source';

export const FIELD_ROLES: readonly { role: FieldRole; label: string }[] = [
  { role: 'none', label: 'Nothing, or what Yomitan puts there' },
  { role: 'sentence', label: 'The lines' },
  { role: 'translation', label: 'The second line, the meaning' },
  { role: 'picture', label: 'The picture' },
  { role: 'sound', label: 'The sound' },
  { role: 'source', label: 'Where it is from' },
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

/** What only the page reads, kept on the server with the user's other settings. */
export interface PageSettings {
  anki: AnkiChoices;
  media: MediaChoices;
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
  };
}

/** Tags as typed: split at spaces, as Anki does. */
export function tagsFrom(typed: string): string[] {
  return [...new Set(typed.split(/\s+/).filter((tag) => tag !== ''))].slice(0, 20);
}

const GUESSES: readonly [RegExp, FieldRole][] = [
  [/^sentence([ _-]?(kanji|text|jp|ja|japanese))?$/i, 'sentence'],
  [/^(sentence[ _-]?(translation|meaning|english|eng|en)|translation)$/i, 'translation'],
  [/^((sentence[ _-]?)?audio|sentence[ _-]?sound|sound)$/i, 'sound'],
  [/^(picture|image|screenshot|snapshot)$/i, 'picture'],
  [/^(misc[ _-]?info|source|sentence[ _-]?source)$/i, 'source'],
];

/**
 * What goes into each field of a note type, by the names of its fields: the sentence into
 * "Sentence", the sound into "SentenceAudio", and so on, as Lapis and most mining note types
 * name them. Each part goes into one field at most; the word's fields are left to Yomitan.
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
    const role = GUESSES.find(([pattern]) => pattern.test(name.trim()))?.[1] ?? 'none';
    roles[name] = role !== 'none' && !taken.has(role) ? role : 'none';
    if (role !== 'none') taken.add(role);
  }
  return roles;
}
