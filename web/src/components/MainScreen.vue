<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';

import { HttpError, UnreachableError } from '@/api/http';
import { createPluginApi } from '@/api/plugin';
import type { Line, Subtitles, Track, Watch } from '@/api/types';
import type { AuthEnv, Session } from '@/auth/signIn';
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
  type Pick,
  pickFor,
  positionOf,
  titleOf,
  trackLabel,
} from '@/watch/watches';

import AccountMenu from './AccountMenu.vue';
import type { Making } from './making';
import SelectionBar from './SelectionBar.vue';
import TranscriptView from './TranscriptView.vue';

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

const api = createPluginApi(props.env.fetch, props.env.base, props.session.token);

function messageOf(error: unknown): string {
  if (error instanceof HttpError) {
    if (error.status === 401) emit('signedOut');
    return error.message;
  }

  if (error instanceof UnreachableError) return 'The server could not be reached.';
  return error instanceof Error ? error.message : String(error);
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
  const parts = [`${watch.Player} (${watch.Client})`];
  const runtime = watch.RuntimeMs === undefined ? '' : ` of ${clockOf(watch.RuntimeMs)}`;
  parts.push(`${clockOf(position.value)}${runtime}`);
  if (!watch.Playing) {
    const ago = (watch.StoppedAgoMs ?? 0) + now.value - (received.value?.at ?? now.value);
    parts.push(`ended ${agoOf(ago)}`);
  } else if (watch.Paused) {
    parts.push('paused');
  }

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

const SCROLL_KEYS = new Set(['PageUp', 'PageDown', 'Home', 'End', 'ArrowUp', 'ArrowDown']);

function onKey(event: KeyboardEvent): void {
  const target = event.target;
  if (target instanceof Element && target.closest('input, select, textarea') !== null) return;
  if (SCROLL_KEYS.has(event.key)) userScrolled();
}

// Choosing lines.

const selected = ref<Set<number>>(new Set());
let rangeFrom: number | undefined;

function toggle(id: number, range: boolean): void {
  const next = new Set(selected.value);
  const ids = lines.value.map((line) => line.Id);
  if (range && rangeFrom !== undefined && ids.includes(rangeFrom)) {
    const [from, to] = [ids.indexOf(rangeFrom), ids.indexOf(id)].sort((a, b) => a - b) as [
      number,
      number,
    ];
    for (const between of ids.slice(from, to + 1)) next.add(between);
  } else if (next.has(id)) {
    next.delete(id);
  } else {
    next.add(id);
  }

  rangeFrom = id;
  selected.value = next;
}

function clear(): void {
  selected.value = new Set();
  rangeFrom = undefined;
}

watch(mainId, clear);

const chosen = computed(() => lines.value.filter((line) => selected.value.has(line.Id)));
const span = computed(() => {
  const first = chosen.value[0];
  if (first === undefined) return undefined;
  const start = Math.min(...chosen.value.map((line) => line.StartMs));
  const end = Math.max(...chosen.value.map((line) => line.EndMs));
  return { start, end, still: (first.StartMs + first.EndMs) / 2 };
});
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

const previewKey = computed(() => {
  const watch = shown.value;
  const value = span.value;
  if (watch === undefined || value === undefined) return '';
  return [
    watch.ItemId,
    watch.MediaSourceId,
    watch.AudioKey ?? '',
    watch.CanCut,
    value.start,
    value.end,
    value.still,
  ]
    .map(String)
    .join('|');
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

  settle = window.setTimeout(() => {
    settle = null;
    const controller = new AbortController();
    making = controller;
    const { ItemId: item, MediaSourceId: source, AudioKey: track } = watch;
    void make(
      picture,
      (signal) => api.picture(item, source, { atMs: value.still, width: 640 }, signal),
      controller.signal,
    );
    if (value.end - value.start > MAX_SOUND_MS) {
      sound.value = { kind: 'none', message: 'More than a minute. Choose fewer lines.' };
    } else {
      void make(
        sound,
        (signal) =>
          api.sound(
            item,
            source,
            { startMs: value.start, endMs: value.end, ...(track === undefined ? {} : { track }) },
            signal,
          ),
        controller.signal,
      );
    }
  }, 300);
});

// The connection lives while the page is in view.

function onVisibility(): void {
  if (document.visibilityState === 'hidden') {
    feed.stop();
  } else {
    feed.start();
  }
}

function onOnline(): void {
  feed.reconnectNow();
}

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibility);
  window.addEventListener('online', onOnline);
  window.addEventListener('keydown', onKey);
  if (document.visibilityState !== 'hidden') feed.start();
});

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', onVisibility);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('keydown', onKey);
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
      <AccountMenu :session="session" @sign-out="emit('signOut')" />
    </header>

    <p v-if="status === 'retrying' && received !== null" class="banner" role="status">
      The connection to the server broke. Trying again…
    </p>

    <div v-if="shown !== undefined" class="toolbar">
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

    <main class="main" @wheel.passive="userScrolled" @touchmove.passive="userScrolled">
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
        :follow="following"
        :lang="mainLang"
        :second-lang="secondLang"
        @toggle="toggle"
      />
    </main>

    <footer v-if="chosen.length > 0" class="bottom">
      <SelectionBar
        :count="chosen.length"
        :span="spanText"
        :picture="picture"
        :sound="sound"
        @clear="clear"
      />
    </footer>
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

.bar > :last-child {
  margin-left: auto;
}

.banner {
  padding: 8px 16px;
  background: var(--raised);
  font-size: 13px;
  text-align: center;
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

.bottom {
  position: sticky;
  z-index: 10;
  bottom: 0;
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
}
</style>
