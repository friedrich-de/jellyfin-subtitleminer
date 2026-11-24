/**
 * A small Anki collection that answers AnkiConnect's actions the way AnkiConnect version 6 does,
 * for the page's tests and for the stand-in AnkiConnect of the end-to-end tests. It imports
 * nothing, so both can use it.
 */

export interface StoredNote {
  id: number;
  deck: string;
  modelName: string;
  fields: Record<string, string>;
  tags: string[];
}

/** A note as addNote and canAddNotes are given it. */
interface NewNote {
  deckName: string;
  modelName: string;
  fields: Record<string, string>;
  tags?: string[];
  options?: { allowDuplicate?: boolean };
}

/** AnkiConnect's own error: the answer is {"result": null, "error": message}. */
export class AnkiConnectError extends Error {}

export const LAPIS_FIELDS = [
  'Expression',
  'ExpressionFurigana',
  'ExpressionReading',
  'ExpressionAudio',
  'SelectionText',
  'MainDefinition',
  'DefinitionPicture',
  'Sentence',
  'SentenceFurigana',
  'SentenceAudio',
  'Picture',
  'Glossary',
  'Hint',
  'IsWordAndSentenceCard',
  'IsClickCard',
  'IsSentenceCard',
  'PitchPosition',
  'PitchCategories',
  'Frequency',
  'FreqSort',
  'MiscInfo',
] as const;

export function createCollection(options: { key?: string; now?: () => number } = {}) {
  const now = options.now ?? (() => Date.now());
  const decks = ['Default', 'Mining'];
  const models: Record<string, string[]> = {
    Lapis: [...LAPIS_FIELDS],
    Basic: ['Front', 'Back'],
  };
  const notes = new Map<number, StoredNote>();
  const media = new Map<string, string>();
  const calls: { action: string; params: Record<string, unknown> }[] = [];
  let lastId = 0;
  /**
   * Syncs with AnkiWeb, as the server AnkiConnect answers them: how many there were, and an error
   * for the next ones to give.
   */
  const sync = { count: 0, error: undefined as string | undefined, changed: false };

  function nextId(): number {
    lastId = Math.max(lastId + 1, now());
    return lastId;
  }

  /** A card as Yomitan would make it, a moment ago. */
  function makeNote(note: Omit<StoredNote, 'id' | 'tags'> & { tags?: string[]; id?: number }) {
    const id = note.id ?? nextId();
    lastId = Math.max(lastId, id);
    sync.changed = true;
    const fields = Object.fromEntries((models[note.modelName] ?? []).map((name) => [name, '']));
    notes.set(id, { tags: [], ...note, id, fields: { ...fields, ...note.fields } });
    return id;
  }

  function search(query: string): number[] {
    const today = new Date(now());
    today.setHours(0, 0, 0, 0);
    const type = /note:"((?:[^"\\]|\\.)*)"/.exec(query)?.[1]?.replace(/\\(.)/g, '$1');
    return [...notes.values()]
      .filter((note) => !/added:1/.test(query) || note.id >= today.getTime())
      .filter((note) => type === undefined || note.modelName === type)
      .map((note) => note.id);
  }

  /** What Anki checks before it makes a note: its deck, note type, first field and duplicates. */
  function check(note: NewNote): void {
    if (!decks.includes(note.deckName))
      throw new AnkiConnectError(`deck was not found: ${note.deckName}`);
    const names = models[note.modelName];
    if (names === undefined) throw new AnkiConnectError(`model was not found: ${note.modelName}`);
    const first = note.fields[names[0]!] ?? '';
    if (first.trim() === '') throw new AnkiConnectError('cannot create note because it is empty');
    const duplicate = [...notes.values()].some(
      (other) =>
        other.deck === note.deckName &&
        other.modelName === note.modelName &&
        other.fields[names[0]!] === first,
    );
    if (duplicate && note.options?.allowDuplicate !== true) {
      throw new AnkiConnectError('cannot create note because it is a duplicate');
    }
  }

  function handle(action: string, params: Record<string, unknown> = {}): unknown {
    calls.push({ action, params });
    switch (action) {
      case 'version':
        return 6;
      case 'requestPermission':
        return { permission: 'granted', requireApikey: options.key !== undefined, version: 6 };
      case 'deckNames':
        return [...decks];
      case 'modelNames':
        return Object.keys(models);
      case 'modelFieldNames': {
        const names = models[String(params.modelName)];
        if (names === undefined)
          throw new AnkiConnectError(`model was not found: ${String(params.modelName)}`);
        return [...names];
      }
      case 'findNotes':
        return search(typeof params.query === 'string' ? params.query : '');
      case 'notesInfo':
        return (params.notes as number[]).map((id) => {
          const note = notes.get(id);
          if (note === undefined) return {};
          const order = models[note.modelName] ?? Object.keys(note.fields);
          return {
            noteId: id,
            modelName: note.modelName,
            tags: note.tags,
            // One card for each note, under the note's own number.
            cards: [id],
            fields: Object.fromEntries(
              Object.entries(note.fields).map(([name, value]) => [
                name,
                { value, order: order.indexOf(name) },
              ]),
            ),
          };
        });
      case 'cardsInfo':
        return (params.cards as number[]).map((id) => {
          const note = notes.get(id);
          return note === undefined
            ? {}
            : { cardId: id, note: id, deckName: note.deck, modelName: note.modelName };
        });
      case 'storeMediaFile': {
        const name = String(params.filename);
        if (typeof params.data !== 'string') throw new AnkiConnectError('data is required');
        media.set(name, params.data);
        return name;
      }
      case 'updateNoteFields': {
        const update = params.note as { id: number; fields: Record<string, string> };
        const note = notes.get(update.id);
        if (note === undefined) throw new AnkiConnectError('Note was not found: ' + update.id);
        Object.assign(note.fields, update.fields);
        sync.changed = true;
        return null;
      }
      case 'sync': {
        if (sync.error !== undefined) throw new AnkiConnectError(sync.error);
        sync.count += 1;
        const outcome = sync.changed ? 'merged' : 'no_changes';
        sync.changed = false;
        return {
          status: 'completed',
          collection: { outcome, local_data_replaced: false },
          media: { outcome: 'completed' },
        };
      }
      case 'canAddNotesWithErrorDetail':
        return (params.notes as NewNote[]).map((note) => {
          try {
            check(note);
            return { canAdd: true };
          } catch (error) {
            if (!(error instanceof AnkiConnectError)) throw error;
            return { canAdd: false, error: error.message };
          }
        });
      case 'addNote': {
        const note = params.note as NewNote;
        check(note);
        return makeNote({
          deck: note.deckName,
          modelName: note.modelName,
          fields: note.fields,
          tags: note.tags ?? [],
        });
      }
      default:
        throw new AnkiConnectError('unsupported action');
    }
  }

  /** Answers a request body as AnkiConnect would, key check included. */
  function answer(body: unknown): { result: unknown; error: string | null } {
    const request = (body ?? {}) as {
      action?: unknown;
      version?: unknown;
      key?: unknown;
      params?: unknown;
    };
    const action = String(request.action);
    if (
      options.key !== undefined &&
      action !== 'requestPermission' &&
      request.key !== options.key
    ) {
      return { result: null, error: 'valid api key must be provided' };
    }
    try {
      return {
        result: handle(action, (request.params ?? {}) as Record<string, unknown>),
        error: null,
      };
    } catch (error) {
      if (error instanceof AnkiConnectError) return { result: null, error: error.message };
      throw error;
    }
  }

  return { decks, models, notes, media, calls, sync, makeNote, handle, answer };
}

export type Collection = ReturnType<typeof createCollection>;
