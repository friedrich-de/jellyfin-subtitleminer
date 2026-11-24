<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';

import {
  addNew,
  addToNewest,
  type CardContent,
  type CardMedia,
  type CardOutcome,
  newCardProblem,
  newestCard,
  undoUpdate,
  wordsInDeck,
} from '@/anki/cards';
import { AnkiError, type AnkiConnect, deviceAnki, serverAnki } from '@/anki/connect';
import { readPageSettings } from '@/anki/pageSettings';
import { syncAnki } from '@/anki/sync';
import { HttpError, UnreachableError } from '@/api/http';
import { createPluginApi, type PictureOptions, type SoundOptions } from '@/api/plugin';
import type {
  DictionariesView,
  Line,
  LookupResult,
  SettingsView,
  Subtitles,
  Track,
  Watch,
} from '@/api/types';
import type { AuthEnv, Session } from '@/auth/signIn';
import type { Box } from '@/dictionary/caret';
import type { WordRequest } from '@/dictionary/popup';
import { wordOf } from '@/dictionary/word';
import { createWatchFeed, type FeedStatus, type Received } from '@/watch/feed';
import { recall, remember, showKeyOf } from '@/watch/remembered';
import {
  agoOf,
  choose,
  chooseMain,
  chooseSecond,
  clockOf,
  hintOf,
  languageTag,
  lastStartedAt,
  linesAt,
  pairLines,
  secondOf,
  type Pick,
  pickFor,
  positionOf,
  type ShownLine,
  titleOf,
  trackLabel,
} from '@/watch/watches';

import AccountMenu from './AccountMenu.vue';
import type { Making, Newest, Sending } from './making';
import SelectionBar from './SelectionBar.vue';
import SettingsScreen from './SettingsScreen.vue';
import TranscriptView from './TranscriptView.vue';
import WordPopup from './WordPopup.vue';

const props = defineProps<{ env: AuthEnv; session: Session }>();
const emit = defineEmits<{
  /** The server no longer takes the login. */
  signedOut: [];
  /** The user asked to sign out. */
  signOut: [];
}>();

/** The language whose subtitles the page shows first. A setting of its own comes with the settings. */
const LEARNING = 'japanese';

/** The longest sound the plugin cuts. */
const MAX_SOUND_MS = 60_000;

/** The longest moving picture the plugin cuts. */
const MAX_MOVING_MS = 15_000;

const api = createPluginApi(props.env.fetch, props.env.base, props.session.token);

function messageOf(error: unknown): string {
  if (error instanceof HttpError) {
    if (error.status === 401) emit('signedOut');
    return error.message;
  }

  if (error instanceof UnreachableError) return 'The server could not be reached.';
  return error instanceof Error ? error.message : String(error);
}

// The user's settings, kept on the server, and where the cards go.

const settings = shallowRef<SettingsView | null>(null);
const view = ref<'lines' | 'settings'>('lines');
const pageSettings = computed(() => readPageSettings(settings.value?.Page));
const anki = computed<AnkiConnect | null>(() => {
  const value = settings.value?.Anki;
  if (value?.Where === 'device') {
    return deviceAnki(props.env.fetch, value.DeviceAddress, value.DeviceKey, api);
  }
  return value?.Where === 'server' ? serverAnki(api) : null;
});

async function loadSettings(): Promise<boolean> {
  try {
    settings.value = await api.settings();
    return true;
  } catch {
    // The page works without them; the feed tells when the login has ended.
    return false;
  }
}

async function openSettings(): Promise<void> {
  if (settings.value === null && !(await loadSettings())) return;
  closeWord();
  view.value = 'settings';
}

// The server's dictionaries, for looking words up.

const dictionaries = shallowRef<DictionariesView | null>(null);

async function loadDictionaries(): Promise<void> {
  try {
    dictionaries.value = await api.dictionaries();
  } catch {
    // Without them words are not looked up; the rest of the page works.
  }
}

const enabledDictionaries = computed(
  () => dictionaries.value?.Dictionaries.filter((dictionary) => dictionary.Enabled) ?? [],
);
/** Whether an administrator set an audio source for words. */
const wordAudio = computed(() => dictionaries.value?.WordAudio?.Set === true);
const lookingUp = computed(
  () => pageSettings.value.dictionary.lookUp && enabledDictionaries.value.length > 0,
);

function closeSettings(): void {
  view.value = 'lines';
  // An administrator may have added or arranged dictionaries there.
  void loadDictionaries();
}

// What the user's players play, as the server tells it.

const status = ref<FeedStatus>('connecting');
const received = shallowRef<Received | null>(null);
const now = ref(performance.now());

const feed = createWatchFeed(
  {
    api,
    now: () => performance.now(),
    setTimeout: (callback, ms) => window.setTimeout(callback, ms),
    clearTimeout: (handle) => window.clearTimeout(handle),
  },
  {
    onList: (list) => {
      received.value = list;
      now.value = list.at;
    },
    onStatus: (next) => {
      status.value = next;
      if (next === 'signed-out') emit('signedOut');
    },
  },
);

const watches = computed(() => received.value?.watches ?? []);
const pick = ref<Pick>({ kind: 'auto' });
const shown = computed<Watch | undefined>(() => choose(watches.value, pick.value));
const moving = computed(() => shown.value?.Playing === true && !shown.value.Paused);

const position = computed(() =>
  shown.value === undefined || received.value === null
    ? 0
    : positionOf(shown.value, received.value.at, now.value),
);

let ticker: number | null = null;
watch(
  moving,
  (on) => {
    if (ticker !== null) window.clearInterval(ticker);
    ticker = on ? window.setInterval(() => (now.value = performance.now()), 250) : null;
  },
  { immediate: true },
);

const where = computed(() => {
  const watch = shown.value;
  if (watch === undefined) return '';
  // Where it is first: on a phone the end of the line is cut off, and a player's name can be long.
  const runtime = watch.RuntimeMs === undefined ? '' : ` of ${clockOf(watch.RuntimeMs)}`;
  const parts = [`${clockOf(position.value)}${runtime}`];
  if (!watch.Playing) {
    const ago = (watch.StoppedAgoMs ?? 0) + now.value - (received.value?.at ?? now.value);
    parts.push(`stopped ${agoOf(ago)}`);
  } else if (watch.Paused) {
    parts.push('paused');
  }
  parts.push(`${watch.Player} (${watch.Client})`);

  return parts.join(' · ');
});

function onPickWatch(event: Event): void {
  const id = (event.target as HTMLSelectElement).value;
  const watch = watches.value.find((candidate) => candidate.Id === id);
  if (watch !== undefined) pick.value = pickFor(watch);
}

// The tracks: chosen per show and remembered in this browser.

const remembered = ref(0);
const showKey = computed(() => (shown.value === undefined ? undefined : showKeyOf(shown.value)));
const hints = computed(() => {
  void remembered.value;
  return showKey.value === undefined ? undefined : recall(props.env.storage, showKey.value);
});

const mainTrack = computed<Track | undefined>(() =>
  shown.value === undefined ? undefined : chooseMain(shown.value, LEARNING, hints.value?.main),
);
const secondTrack = computed<Track | undefined>(() =>
  shown.value === undefined
    ? undefined
    : chooseSecond(shown.value, mainTrack.value, hints.value?.second),
);
const readable = computed(() => shown.value?.Subtitles.filter((track) => track.Readable) ?? []);
const otherTracks = computed(() =>
  readable.value.filter((track) => track.Key !== mainTrack.value?.Key),
);

function keep(change: Parameters<typeof remember>[2]): void {
  if (showKey.value === undefined) return;
  remember(props.env.storage, showKey.value, change, Date.now());
  remembered.value += 1;
}

function onPickMain(event: Event): void {
  const key = (event.target as HTMLSelectElement).value;
  const track = readable.value.find((candidate) => candidate.Key === key);
  if (track === undefined) return;
  const second = secondTrack.value?.Key === key ? { second: 'none' as const } : {};
  keep({ main: hintOf(track), ...second });
}

function onPickSecond(event: Event): void {
  const key = (event.target as HTMLSelectElement).value;
  const track = readable.value.find((candidate) => candidate.Key === key);
  keep({ second: track === undefined ? 'none' : hintOf(track) });
}

// The lines, read once per track and kept while the page is open.

type Loaded =
  | { kind: 'loading' }
  | { kind: 'ready'; subtitles: Subtitles }
  | { kind: 'failed'; message: string };

const read = new Map<string, Promise<Subtitles>>();

function load(itemId: string, source: string, key: string): Promise<Subtitles> {
  const id = `${itemId}|${source}|${key}`;
  let found = read.get(id);
  if (found === undefined) {
    found = api.subtitles(itemId, source, key);
    read.set(id, found);
    found.catch(() => read.delete(id));
    if (read.size > 16) read.delete(read.keys().next().value!);
  }

  return found;
}

function trackId(track: Track | undefined): string | undefined {
  const watch = shown.value;
  return watch === undefined || track === undefined
    ? undefined
    : `${watch.ItemId}|${watch.MediaSourceId}|${track.Key}`;
}

const mainId = computed(() => trackId(mainTrack.value));
const secondId = computed(() => trackId(secondTrack.value));
const mainLines = shallowRef<Loaded | null>(null);
const secondLines = shallowRef<Loaded | null>(null);
const tries = ref(0);

function loadInto(id: () => string | undefined, into: typeof mainLines): void {
  watch(
    [id, tries],
    async ([value]) => {
      if (value === undefined) {
        into.value = null;
        return;
      }

      const [itemId, source, key] = value.split('|') as [string, string, string];
      into.value = { kind: 'loading' };
      try {
        const subtitles = await load(itemId, source, key);
        if (id() === value) into.value = { kind: 'ready', subtitles };
      } catch (error) {
        if (id() === value) into.value = { kind: 'failed', message: messageOf(error) };
      }
    },
    { immediate: true },
  );
}

loadInto(() => mainId.value, mainLines);
loadInto(() => secondId.value, secondLines);

const showSigns = ref(false);
const allLines = computed<Line[]>(() =>
  mainLines.value?.kind === 'ready' ? mainLines.value.subtitles.Lines : [],
);
const hasDialogue = computed(() => allLines.value.some((line) => line.Kind === 'dialogue'));
const signCount = computed(() => allLines.value.filter((line) => line.Kind === 'sign').length);
const lines = computed(() => {
  const main =
    hasDialogue.value && !showSigns.value
      ? allLines.value.filter((line) => line.Kind !== 'sign')
      : allLines.value;
  const second =
    secondLines.value?.kind === 'ready' ? secondLines.value.subtitles.Lines : undefined;
  return pairLines(main, second);
});

// Only a change of the lines on screen reaches the list, not every tick of the clock.
const currentKey = computed(() =>
  linesAt(lines.value, position.value)
    .map((line) => line.Id)
    .join(','),
);
const current = computed(
  () => new Set(currentKey.value === '' ? [] : currentKey.value.split(',').map(Number)),
);
const anchor = computed(() => lastStartedAt(lines.value, position.value)?.Id);

// Following: the line being said stays in view until the user scrolls away.

const following = ref(true);

function userScrolled(): void {
  following.value = false;
}

/** Follows playback again, from the line being said now. */
function goToPlaying(): void {
  closeWord();
  following.value = true;
}

const SCROLL_KEYS = new Set(['PageUp', 'PageDown', 'Home', 'End', 'ArrowUp', 'ArrowDown']);

function onKey(event: KeyboardEvent): void {
  const target = event.target;
  if (target instanceof Element && target.closest('input, select, textarea') !== null) return;
  if (SCROLL_KEYS.has(event.key)) userScrolled();
}

// Choosing lines.

const selected = ref<Set<number>>(new Set());
let rangeFrom: number | undefined;

/**
 * Chooses lines as one run in a row, since a card's sound is one piece: a line next to the run
 * joins it, a line apart from it starts a new run, and Shift takes every line from the last one
 * chosen to this one. A line at either end of the run leaves it; one inside it starts the run anew.
 */
function toggle(id: number, range: boolean): void {
  const ids = lines.value.map((line) => line.Id);
  const at = ids.indexOf(id);
  if (at === -1) return;
  const run = ids.flatMap((lineId, index) => (selected.value.has(lineId) ? [index] : []));
  let next: number[];
  if (range && rangeFrom !== undefined && ids.includes(rangeFrom)) {
    const ends = [...run, ids.indexOf(rangeFrom), at];
    next = ids.slice(Math.min(...ends), Math.max(...ends) + 1);
  } else if (run.length === 0) {
    next = [id];
  } else {
    const first = Math.min(...run);
    const last = Math.max(...run);
    if (at === first || at === last) {
      next = selected.value.has(id)
        ? ids.slice(first, last + 1).filter((lineId) => lineId !== id)
        : [id];
    } else if (at > first && at < last) {
      next = [id];
    } else if (at === first - 1 || at === last + 1) {
      next = ids.slice(Math.min(first, at), Math.max(last, at) + 1);
    } else {
      next = [id];
    }
  }

  rangeFrom = id;
  selected.value = new Set(next);
}

function clear(): void {
  selected.value = new Set();
  rangeFrom = undefined;
}

watch(mainId, clear);

const chosen = computed(() => lines.value.filter((line) => selected.value.has(line.Id)));

interface Span {
  start: number;
  end: number;
  /** The moment of a still: the middle of the first line. */
  still: number;
}

function spanOf(of: readonly Line[]): Span | undefined {
  const first = of[0];
  if (first === undefined) return undefined;
  const start = Math.min(...of.map((line) => line.StartMs));
  const end = Math.max(...of.map((line) => line.EndMs));
  return { start, end, still: (first.StartMs + first.EndMs) / 2 };
}

const span = computed(() => spanOf(chosen.value));
const spanText = computed(() => {
  const value = span.value;
  if (value === undefined) return '';
  const seconds = ((value.end - value.start) / 1000).toFixed(1);
  return `${clockOf(value.start)} to ${clockOf(value.end)}, ${seconds} seconds`;
});

// The preview: the picture and the sound the card would get, cut once the choice settles.

const picture = shallowRef<Making>({ kind: 'waiting' });
const sound = shallowRef<Making>({ kind: 'waiting' });
const urls: string[] = [];
let making: AbortController | null = null;
let settle: number | null = null;

function release(): void {
  for (const url of urls.splice(0)) URL.revokeObjectURL(url);
}

async function make(
  into: typeof picture,
  cut: (signal: AbortSignal) => Promise<Blob>,
  signal: AbortSignal,
): Promise<void> {
  try {
    const blob = await cut(signal);
    if (signal.aborted) return;
    const url = URL.createObjectURL(blob);
    urls.push(url);
    into.value = { kind: 'ready', url };
  } catch (error) {
    if (!signal.aborted) into.value = { kind: 'failed', message: messageOf(error) };
  }
}

/** The picture of lines, as the settings want it cut. */
function pictureFor(value: Span | undefined): PictureOptions | undefined {
  if (value === undefined) return undefined;
  const media = pageSettings.value.media;
  const look = {
    format: media.pictureFormat,
    width: media.pictureWidth,
    quality: media.pictureQuality,
  };
  return media.moving
    ? { atMs: value.start, endMs: Math.min(value.end, value.start + MAX_MOVING_MS), ...look }
    : { atMs: value.still, ...look };
}

/** The sound of lines, a little before and after them as the settings say. */
function soundFor(value: Span | undefined): SoundOptions | undefined {
  const track = shown.value?.AudioKey;
  if (value === undefined) return undefined;
  const media = pageSettings.value.media;
  return {
    startMs: Math.max(0, value.start - media.padBeforeMs),
    endMs: value.end + media.padAfterMs,
    format: media.soundFormat,
    normalize: media.normalize,
    ...(track === undefined ? {} : { track }),
  };
}

const pictureOptions = computed(() => pictureFor(span.value));
const soundOptions = computed(() => soundFor(span.value));

const previewKey = computed(() => {
  const watch = shown.value;
  if (watch === undefined || span.value === undefined) return '';
  return JSON.stringify([
    watch.ItemId,
    watch.MediaSourceId,
    watch.CanCut,
    pictureOptions.value,
    soundOptions.value,
  ]);
});

watch(previewKey, (key) => {
  if (settle !== null) window.clearTimeout(settle);
  making?.abort();
  making = null;
  release();
  picture.value = { kind: 'waiting' };
  sound.value = { kind: 'waiting' };
  const watch = shown.value;
  const value = span.value;
  if (key === '' || watch === undefined || value === undefined) return;

  if (!watch.CanCut) {
    const message = 'Jellyfin cannot read this video as a file, so nothing can be cut from it.';
    picture.value = { kind: 'none', message };
    sound.value = { kind: 'none', message };
    return;
  }

  const pictureCut = pictureOptions.value!;
  const soundCut = soundOptions.value!;
  settle = window.setTimeout(() => {
    settle = null;
    const controller = new AbortController();
    making = controller;
    const { ItemId: item, MediaSourceId: source } = watch;
    void make(
      picture,
      (signal) => api.picture(item, source, pictureCut, signal),
      controller.signal,
    );
    if (soundCut.endMs - soundCut.startMs > MAX_SOUND_MS) {
      sound.value = { kind: 'none', message: 'More than a minute. Choose fewer lines.' };
    } else {
      void make(sound, (signal) => api.sound(item, source, soundCut, signal), controller.signal);
    }
  }, 300);
});

// Sending the chosen lines to Anki.

const sending = shallowRef<Sending>({ kind: 'idle' });
let lastUpdate: { anki: AnkiConnect; outcome: CardOutcome } | null = null;
/**
 * Whether the lines of the card just made were let go of. What happened to the card stays in view
 * then, with its Undo, until other lines are chosen or it is closed.
 */
const madeCard = ref(false);

watch(previewKey, (key) => {
  if (key === '' && madeCard.value) return;
  madeCard.value = false;
  if (sending.value.kind !== 'busy') sending.value = { kind: 'idle' };
  lastUpdate = null;
});

function closeMade(): void {
  madeCard.value = false;
  sending.value = { kind: 'idle' };
  lastUpdate = null;
}

/** What a card of these lines gets: their text, the second line, where they are from. */
function contentOf(watch: Watch, of: readonly Line[], value: Span): CardContent {
  return {
    sentence: of.map((line) => line.Text).join('\n'),
    translation: secondOf(
      of,
      secondLines.value?.kind === 'ready' ? secondLines.value.subtitles.Lines : undefined,
    ).join('\n'),
    source: `${titleOf(watch)} · ${clockOf(value.start)}`,
  };
}

/** The picture and sound a card of these lines gets, where they can be cut. */
function mediaOf(watch: Watch, value: Span): CardMedia {
  const sound = soundFor(value);
  return {
    itemId: watch.ItemId,
    source: watch.MediaSourceId,
    picture: watch.CanCut ? pictureFor(value) : undefined,
    sound:
      watch.CanCut && sound !== undefined && sound.endMs - sound.startMs <= MAX_SOUND_MS
        ? sound
        : undefined,
  };
}

async function send(mode: 'newest' | 'new'): Promise<void> {
  const target = anki.value;
  const watch = shown.value;
  const value = span.value;
  if (target === null || watch === undefined || value === undefined) return;

  sending.value = {
    kind: 'busy',
    text: mode === 'newest' ? 'Adding to your newest card…' : 'Making a new card…',
  };
  lastUpdate = null;
  const content = contentOf(watch, chosen.value, value);
  const media = mediaOf(watch, value);

  try {
    const outcome =
      mode === 'newest'
        ? await addToNewest(target, pageSettings.value, content, media, Date.now())
        : await addNew(target, pageSettings.value, content, media);
    const name = outcome.label === '' ? 'the card' : `"${outcome.label}"`;
    if (outcome.kind === 'updated') lastUpdate = { anki: target, outcome };
    sending.value = {
      kind: 'done',
      text: outcome.kind === 'updated' ? `Added to ${name}.` : `Made a new card, ${name}.`,
      canUndo: outcome.kind === 'updated',
    };
    // The lines are done with, so they are let go of, ready for the next card.
    madeCard.value = true;
    clear();
  } catch (error) {
    sending.value = { kind: 'failed', text: messageOf(error) };
  }
  void lookForNewest();
}

// The card "Add to my newest card" would change, looked for while lines are chosen: again every
// few seconds and when the page comes back into view, since the card is made in another window.

const newest = shallowRef<Newest>({ kind: 'looking' });
let newestTimer: number | null = null;
let newestAsked = 0;

async function lookForNewest(): Promise<void> {
  const target = anki.value;
  if (target === null || chosen.value.length === 0 || sending.value.kind === 'busy') return;
  const asked = (newestAsked += 1);
  try {
    const found = await newestCard(target, pageSettings.value, Date.now());
    if (asked !== newestAsked) return;
    newest.value = { kind: 'found', label: found.label, minutes: found.minutes };
  } catch (error) {
    if (asked !== newestAsked) return;
    newest.value =
      error instanceof AnkiError && !error.unreachable
        ? { kind: 'none', text: error.message }
        : { kind: 'failed', text: messageOf(error) };
  }
}

const followsNewest = computed(
  () => view.value === 'lines' && chosen.value.length > 0 && anki.value !== null,
);

function stopNewest(): void {
  if (newestTimer !== null) window.clearInterval(newestTimer);
  newestTimer = null;
  newestAsked += 1;
}

watch(
  [followsNewest, anki, pageSettings],
  ([follows], before) => {
    stopNewest();
    if (!follows) return;
    // Only a new look, not every line chosen, empties what is shown.
    if (before === undefined || !before[0]) newest.value = { kind: 'looking' };
    void lookForNewest();
    newestTimer = window.setInterval(() => void lookForNewest(), 10_000);
  },
  { immediate: true },
);

function onFocus(): void {
  if (followsNewest.value) void lookForNewest();
}

async function undo(): Promise<void> {
  const update = lastUpdate;
  if (update === null) return;
  lastUpdate = null;
  sending.value = { kind: 'busy', text: 'Putting the card back…' };
  try {
    await undoUpdate(update.anki, update.outcome);
    sending.value = { kind: 'done', text: 'The card is as it was.', canUndo: false };
  } catch (error) {
    sending.value = { kind: 'failed', text: messageOf(error) };
  }
}

// Syncing Anki with AnkiWeb now, from the bar, rather than when it next would by itself.

type Syncing = { kind: 'idle' } | { kind: 'busy' } | { kind: 'done' | 'failed'; text: string };

const syncing = shallowRef<Syncing>({ kind: 'idle' });
let syncShown: number | null = null;

function endSyncNote(): void {
  if (syncShown !== null) window.clearTimeout(syncShown);
  syncShown = null;
  syncing.value = { kind: 'idle' };
}

async function syncNow(): Promise<void> {
  const target = anki.value;
  if (target === null || syncing.value.kind === 'busy') return;
  endSyncNote();
  syncing.value = { kind: 'busy' };
  try {
    syncing.value = { kind: 'done', text: await syncAnki(target) };
    syncShown = window.setTimeout(endSyncNote, 6_000);
  } catch (error) {
    syncing.value = { kind: 'failed', text: messageOf(error) };
  }
}

// Looking a word up: a tap on it opens the popup, from which a card of it is made.

const wordRequest = shallowRef<WordRequest | null>(null);
/** The line and place of the word looked up, and how long the word found there is. */
const marked = ref<{ id: number; start: number; length: number } | undefined>(undefined);

function closeWord(): void {
  wordRequest.value = null;
  marked.value = undefined;
}

function onWord(id: number, at: { offset: number; box: Box } | null): void {
  const line = lines.value.find((candidate) => candidate.Id === id);
  if (at === null || line === undefined) {
    // A tap beside the words closes the popup, or else chooses the line as it does without one.
    if (wordRequest.value !== null) closeWord();
    else toggle(id, false);
    return;
  }
  marked.value = { id, start: at.offset, length: 0 };
  wordRequest.value = { text: line.Text.slice(at.offset, at.offset + 64), box: at.box };
}

function onFound(length: number): void {
  if (marked.value !== undefined) marked.value = { ...marked.value, length };
}

watch([mainId, view], closeWord);

/**
 * The lines a card of the word gets: the chosen lines when the word is in one of them, else its
 * own line; and where the word is in their text.
 */
function wordLines(): { of: ShownLine[]; start: number } | undefined {
  const mark = marked.value;
  const line = lines.value.find((candidate) => candidate.Id === mark?.id);
  if (mark === undefined || line === undefined) return undefined;
  const of = selected.value.has(line.Id) ? chosen.value : [line];
  let start = mark.start;
  for (const before of of.slice(0, of.indexOf(line))) start += before.Text.length + 1;
  return { of, start };
}

function wordContent(
  result: LookupResult,
  inLine: boolean,
  selection = '',
): CardContent | undefined {
  const watch = shown.value;
  const found = wordLines();
  const value = found === undefined ? undefined : spanOf(found.of);
  if (watch === undefined || found === undefined || value === undefined) return undefined;
  // A word looked up in a definition is not in the sentence to be picked out.
  const word = wordOf(result, inLine ? found.start : -1);
  return { ...contentOf(watch, found.of, value), word: { ...word, selection } };
}

const wordCardProblem = computed(() =>
  anki.value === null
    ? 'Choose where your cards go in the settings to make cards of words.'
    : (newCardProblem(pageSettings.value, true) ?? ''),
);

async function addWordCard(
  result: LookupResult,
  inLine: boolean,
  selection: string,
  sound: string | undefined,
): Promise<string> {
  const target = anki.value;
  const watch = shown.value;
  const content = wordContent(result, inLine, selection);
  const value = spanOf(wordLines()?.of ?? []);
  if (target === null || watch === undefined || content === undefined || value === undefined) {
    throw new Error('The line of this word is gone. Look the word up again.');
  }
  try {
    const media = {
      ...mediaOf(watch, value),
      wordAudio: wordAudio.value,
      ...(sound === undefined ? {} : { wordAudioChoice: sound }),
    };
    const outcome = await addNew(target, pageSettings.value, content, media);
    const name = `"${outcome.label === '' ? result.term.expression : outcome.label}"`;
    return outcome.noWordAudio === true
      ? `Made a new card, ${name}, without the word's sound: the audio source has none for it.`
      : `Made a new card, ${name}.`;
  } catch (error) {
    throw error instanceof HttpError ? new Error(messageOf(error)) : error;
  }
}

async function wordsKnown(results: LookupResult[]): Promise<boolean[]> {
  const target = anki.value;
  if (target === null) return results.map(() => false);
  const contents = results.map((result) => wordContent(result, true));
  if (contents.some((content) => content === undefined)) return results.map(() => false);
  return wordsInDeck(target, pageSettings.value, contents as CardContent[]);
}

// The connection lives while the page is in view.

function onVisibility(): void {
  if (document.visibilityState === 'hidden') {
    feed.stop();
  } else {
    feed.start();
    onFocus();
  }
}

function onOnline(): void {
  feed.reconnectNow();
}

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibility);
  window.addEventListener('online', onOnline);
  window.addEventListener('keydown', onKey);
  window.addEventListener('focus', onFocus);
  if (document.visibilityState !== 'hidden') feed.start();
  void loadSettings();
  void loadDictionaries();
});

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', onVisibility);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('keydown', onKey);
  window.removeEventListener('focus', onFocus);
  stopNewest();
  if (syncShown !== null) window.clearTimeout(syncShown);
  feed.stop();
  if (ticker !== null) window.clearInterval(ticker);
  if (settle !== null) window.clearTimeout(settle);
  making?.abort();
  release();
});

const mainLang = computed(() => languageTag(mainTrack.value));
const secondLang = computed(() => languageTag(secondTrack.value));
</script>

<template>
  <div class="screen">
    <header class="bar">
      <span class="brand">Subtitle Miner</span>
      <div v-if="shown !== undefined" class="playing-now">
        <p class="title" data-testid="title">{{ titleOf(shown) }}</p>
        <p class="where muted" data-testid="where">{{ where }}</p>
      </div>
      <div class="bar-end">
        <button
          v-if="anki !== null"
          class="button icon"
          :class="{ turning: syncing.kind === 'busy' }"
          type="button"
          aria-label="Sync Anki now"
          title="Sync Anki now"
          data-testid="sync-anki"
          :disabled="syncing.kind === 'busy'"
          :aria-busy="syncing.kind === 'busy'"
          @click="syncNow"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path
              fill="currentColor"
              d="M12 4a8 8 0 0 0-7.4 5h2.2A6 6 0 0 1 17.7 8.3L15 11h7V4l-2.9 2.9A8 8 0 0 0 12 4Zm7.4 11h-2.2A6 6 0 0 1 6.3 15.7L9 13H2v7l2.9-2.9A8 8 0 0 0 19.4 15Z"
            />
          </svg>
        </button>
        <button
          class="button icon"
          type="button"
          aria-label="Settings"
          title="Settings"
          data-testid="open-settings"
          :aria-pressed="view === 'settings'"
          @click="view === 'settings' ? (view = 'lines') : openSettings()"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path
              fill="currentColor"
              d="M19.4 13a7.5 7.5 0 0 0 0-2l2.1-1.6-2-3.5-2.5 1a7.3 7.3 0 0 0-1.7-1L15 3.3h-4l-.4 2.6a7.3 7.3 0 0 0-1.7 1l-2.5-1-2 3.5L6.6 11a7.5 7.5 0 0 0 0 2l-2.1 1.6 2 3.5 2.5-1c.5.4 1.1.7 1.7 1l.4 2.6h4l.4-2.6c.6-.3 1.2-.6 1.7-1l2.5 1 2-3.5ZM13 15.5A3.5 3.5 0 1 1 13 8.5a3.5 3.5 0 0 1 0 7Z"
              transform="translate(-1 0)"
            />
          </svg>
        </button>
        <AccountMenu :session="session" @sign-out="emit('signOut')" />
      </div>
    </header>

    <SettingsScreen
      v-if="view === 'settings' && settings !== null"
      :api="api"
      :fetch="env.fetch"
      :base="env.base"
      :settings="settings"
      @close="closeSettings"
      @saved="(next) => (settings = next)"
    />

    <p
      v-if="syncing.kind !== 'idle'"
      class="sync-note"
      :class="{ error: syncing.kind === 'failed' }"
      :role="syncing.kind === 'failed' ? 'alert' : 'status'"
      data-testid="sync-status"
    >
      <span>{{ syncing.kind === 'busy' ? 'Syncing Anki with AnkiWeb…' : syncing.text }}</span>
      <button
        v-if="syncing.kind !== 'busy'"
        class="link"
        type="button"
        data-testid="close-sync-status"
        @click="endSyncNote"
      >
        Close
      </button>
    </p>

    <p v-if="status === 'retrying' && received !== null" class="banner" role="status">
      The connection to the server broke. Trying again…
    </p>

    <div v-if="view === 'lines' && shown !== undefined" class="toolbar">
      <label class="picker">
        <span class="picker-name">Player</span>
        <select :value="shown.Id" data-testid="pick-watch" @change="onPickWatch">
          <option v-for="item in watches" :key="item.Id" :value="item.Id">
            {{ item.Player }} · {{ titleOf(item) }}{{ item.Playing ? '' : ' · watched' }}
          </option>
        </select>
      </label>

      <label v-if="shown.Subtitles.length > 0" class="picker">
        <span class="picker-name">Subtitles</span>
        <select :value="mainTrack?.Key ?? ''" data-testid="pick-subtitles" @change="onPickMain">
          <option
            v-for="track in shown.Subtitles"
            :key="track.Key"
            :value="track.Key"
            :disabled="!track.Readable"
          >
            {{ trackLabel(track) }}
          </option>
        </select>
      </label>

      <label v-if="otherTracks.length > 0" class="picker">
        <span class="picker-name">Second line</span>
        <select
          :value="secondTrack?.Key ?? 'none'"
          data-testid="pick-second"
          @change="onPickSecond"
        >
          <option value="none">None</option>
          <option v-for="track in otherTracks" :key="track.Key" :value="track.Key">
            {{ trackLabel(track) }}
          </option>
        </select>
      </label>

      <div class="switches">
        <button
          class="switch"
          type="button"
          role="switch"
          :aria-checked="following"
          @click="following = !following"
        >
          <span class="knob" aria-hidden="true" />
          Follow playback
        </button>
        <button
          v-if="hasDialogue && signCount > 0"
          class="switch"
          type="button"
          role="switch"
          :aria-checked="showSigns"
          @click="showSigns = !showSigns"
        >
          <span class="knob" aria-hidden="true" />
          Signs ({{ signCount }})
        </button>
      </div>
    </div>

    <main
      v-if="view === 'lines'"
      class="main"
      @wheel.passive="userScrolled"
      @touchmove.passive="userScrolled"
    >
      <section v-if="received === null" class="note" role="status">
        <p class="muted">
          {{
            status === 'retrying'
              ? 'The server could not be reached. Trying again…'
              : 'Looking for what you watch…'
          }}
        </p>
      </section>

      <section v-else-if="shown === undefined" class="note" data-testid="nothing-playing">
        <h1>Nothing is playing</h1>
        <p class="muted">
          Play an episode or a film in any Jellyfin app signed in as {{ session.status.UserName }}.
          It shows up here by itself, with its subtitles.
        </p>
      </section>

      <section v-else-if="readable.length === 0" class="note">
        <h1>No subtitles to read</h1>
        <p class="muted">
          {{
            shown.Subtitles.length === 0
              ? 'This video has no subtitles. Add a subtitle file next to it in Jellyfin.'
              : 'Its subtitles are pictures, not text, so their lines cannot be read.'
          }}
        </p>
      </section>

      <section v-else-if="mainLines?.kind === 'loading'" class="note" role="status">
        <p class="muted">Reading the subtitles…</p>
      </section>

      <section v-else-if="mainLines?.kind === 'failed'" class="note" role="alert">
        <h1>The subtitles could not be read</h1>
        <p class="muted">{{ mainLines.message }}</p>
        <p><button class="button" type="button" @click="tries += 1">Try again</button></p>
      </section>

      <section v-else-if="lines.length === 0" class="note">
        <p class="muted">This subtitle track has no lines.</p>
      </section>

      <TranscriptView
        v-else
        :lines="lines"
        :current="current"
        :selected="selected"
        :anchor="anchor"
        :follow="following && wordRequest === null"
        :lang="mainLang"
        :second-lang="secondLang"
        :look-up="lookingUp"
        :marked="marked"
        @toggle="toggle"
        @word="onWord"
      />
    </main>

    <WordPopup
      v-if="lookingUp && view === 'lines'"
      :api="api"
      :dictionaries="dictionaries?.Dictionaries ?? []"
      :request="wordRequest"
      :add-card="wordCardProblem === '' ? addWordCard : undefined"
      :no-cards="wordCardProblem"
      :in-deck="wordCardProblem === '' ? wordsKnown : undefined"
      :word-audio="wordAudio"
      :look="pageSettings.dictionary"
      @found="onFound"
      @close="closeWord"
    />

    <div v-if="view === 'lines'" class="dock">
      <button
        v-if="!following && anchor !== undefined"
        class="button now"
        type="button"
        data-testid="go-to-playing"
        @click="goToPlaying"
      >
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path
            fill="currentColor"
            d="M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8m8.94 3A9 9 0 0 0 13 3.06V1h-2v2.06A9 9 0 0 0 3.06 11H1v2h2.06A9 9 0 0 0 11 20.94V23h2v-2.06A9 9 0 0 0 20.94 13H23v-2zM12 19a7 7 0 1 1 0-14 7 7 0 0 1 0 14"
          />
        </svg>
        Go to the line playing
      </button>
      <footer v-if="chosen.length > 0 || madeCard" class="bottom">
        <SelectionBar
          v-if="chosen.length > 0"
          :count="chosen.length"
          :span="spanText"
          :picture="picture"
          :sound="sound"
          :anki-ready="anki !== null"
          :sending="sending"
          :newest="newest"
          @clear="clear"
          @newest="send('newest')"
          @add="send('new')"
          @undo="undo"
          @settings="openSettings"
        />
        <p
          v-else
          class="made"
          :class="{ error: sending.kind === 'failed' }"
          :role="sending.kind === 'failed' ? 'alert' : 'status'"
          data-testid="sending"
        >
          <span v-if="sending.kind !== 'idle'">{{ sending.text }}</span>
          <button
            v-if="sending.kind === 'done' && sending.canUndo"
            class="link"
            type="button"
            data-testid="undo"
            @click="undo"
          >
            Undo
          </button>
          <button
            v-if="sending.kind !== 'busy'"
            class="link"
            type="button"
            data-testid="close-sent"
            @click="closeMade"
          >
            Close
          </button>
        </p>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.screen {
  display: flex;
  flex-direction: column;
  min-height: 100%;
}

.bar {
  position: sticky;
  z-index: 10;
  top: 0;
  display: flex;
  align-items: center;
  gap: 8px 16px;
  min-height: 64px;
  padding: 8px max(16px, env(safe-area-inset-right)) 8px max(16px, env(safe-area-inset-left));
  padding-top: max(8px, env(safe-area-inset-top));
  background: var(--surface);
}

.brand {
  flex: 0 0 auto;
  font-size: 18px;
  font-weight: 600;
  color: var(--text-strong);
}

.playing-now {
  flex: 1 1 auto;
  min-width: 0;
}

.title {
  overflow: hidden;
  font-weight: 600;
  color: var(--text-strong);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.where {
  overflow: hidden;
  font-size: 13px;
  font-variant-numeric: tabular-nums;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bar-end {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
}

.icon {
  width: 44px;
  padding: 0;
  background: transparent;
}

.icon[aria-pressed='true'] {
  background: var(--raised);
}

.banner {
  padding: 8px 16px;
  background: var(--raised);
  font-size: 13px;
  text-align: center;
}

.sync-note {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px 12px;
  flex-wrap: wrap;
  padding: 4px 16px;
  background: var(--raised);
  font-size: 13px;
  text-align: center;
}

.sync-note.error {
  color: var(--danger);
}

.made {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 16px;
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  font-size: 14px;
}

.made.error {
  color: var(--danger);
}

.made .link,
.sync-note .link {
  padding: 4px 0;
  border: 0;
  background: none;
  color: var(--text);
  text-decoration: underline;
  cursor: pointer;
}

.turning svg {
  animation: turn 1s linear infinite;
}

@keyframes turn {
  to {
    transform: rotate(360deg);
  }
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 8px 12px;
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  padding: 12px 16px 4px;
}

.picker {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex: 1 1 180px;
  min-width: 0;
}

.picker-name {
  font-size: 12px;
  color: var(--text-muted);
}

select {
  width: 100%;
  min-height: 44px;
  padding: 0 12px;
  border: 0;
  border-radius: var(--radius);
  background: var(--raised);
  color: var(--text-strong);
  font: inherit;
  text-overflow: ellipsis;
}

select:hover {
  background: var(--raised-hover);
}

.switches {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--text);
  cursor: pointer;
}

.knob {
  position: relative;
  width: 36px;
  height: 20px;
  border-radius: 10px;
  background: var(--raised-hover);
}

.knob::after {
  content: '';
  position: absolute;
  top: 3px;
  left: 3px;
  width: 14px;
  height: 14px;
  border-radius: 50%;
  background: var(--text-muted);
  transition: transform 120ms ease;
}

.switch[aria-checked='true'] .knob {
  background: var(--accent);
}

.switch[aria-checked='true'] .knob::after {
  background: var(--on-accent);
  transform: translateX(16px);
}

.main {
  flex: 1 1 auto;
}

.note {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: 100%;
  max-width: 560px;
  margin: 0 auto;
  padding: 32px 16px;
}

.note h1 {
  font-size: 22px;
  font-weight: 600;
  color: var(--text-strong);
}

.dock {
  position: sticky;
  z-index: 10;
  bottom: 0;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  /* Only its button and bar take taps; the lines behind the space between stay reachable. */
  pointer-events: none;
}

.dock > * {
  pointer-events: auto;
}

.now {
  margin: 0 max(16px, env(safe-area-inset-right)) 12px 16px;
  border-radius: 999px;
  box-shadow: 0 2px 10px rgb(0 0 0 / 0.5);
}

.bottom {
  align-self: stretch;
  padding: 12px max(16px, env(safe-area-inset-right)) 12px max(16px, env(safe-area-inset-left));
  padding-bottom: max(12px, env(safe-area-inset-bottom));
  background: var(--surface);
  box-shadow: 0 -1px 0 var(--border);
}

@media (width <= 600px) {
  .brand {
    display: none;
  }

  .bar {
    min-height: 56px;
  }

  .toolbar {
    padding-top: 8px;
  }

  .picker {
    flex-basis: 140px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .knob::after {
    transition: none;
  }

  .turning svg {
    animation: none;
  }
}
</style>
