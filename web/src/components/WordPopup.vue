<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, useTemplateRef, watch } from 'vue';

import { DuplicateCardError } from '@/anki/cards';
import type { DictionaryChoices } from '@/anki/pageSettings';
import type { PluginApi } from '@/api/plugin';
import type { DictionaryView, LookupResult } from '@/api/types';
import {
  type CardState,
  type DictionaryPopup,
  loadPopup,
  type WordRequest,
} from '@/dictionary/popup';

const props = defineProps<{
  api: PluginApi;
  /** The server's dictionaries, for their names, styles and pictures. */
  dictionaries: readonly DictionaryView[];
  request: WordRequest | null;
  /**
   * Makes a card of a word. `inLine` is false for a word looked up in a definition, and `sound`
   * names the word's sound chosen from the audio button's list. Resolves with what to tell the
   * user. Without it the popup's Anki button is off, saying why.
   */
  addCard:
    | ((
        result: LookupResult,
        inLine: boolean,
        selection: string,
        sound: string | undefined,
      ) => Promise<string>)
    | undefined;
  /** Why no card can be made, for the Anki button. */
  noCards: string;
  /** Whether Anki has a card for each word already. */
  inDeck: ((results: LookupResult[]) => Promise<boolean[]>) | undefined;
  /** Whether the server has an audio source for words, for the popup's play button. */
  wordAudio?: boolean;
  /** How the popup looks: its text size, and whether it shows tags and spreads definitions out. */
  look?: Pick<DictionaryChoices, 'textSize' | 'hideTags' | 'compact'>;
}>();

const emit = defineEmits<{
  /** How much of the text the first word found takes: 0 when none was found. */
  found: [length: number];
  close: [];
}>();

const panel = useTemplateRef<HTMLDivElement>('panel');
const holder = useTemplateRef<HTMLDivElement>('holder');
const popup = shallowRef<DictionaryPopup<LookupResult> | null>(null);
const state = ref<'idle' | 'looking' | 'shown' | 'none' | 'failed'>('idle');
const status = ref<{ text: string; error: boolean } | null>(null);

/**
 * The part of the page in view, in the coordinates the popup is placed in. A phone zoomed in
 * shows only part of the page: the popup is fitted to that part, not to the whole width.
 */
interface InView {
  left: number;
  top: number;
  width: number;
  height: number;
  /** Whether that is less than the whole page width, as when zoomed in. */
  part: boolean;
}

function inView(): InView {
  const visual = window.visualViewport;
  const width = document.documentElement.clientWidth || window.innerWidth;
  if (visual === null || visual === undefined || !(visual.width > 0)) {
    return { left: 0, top: 0, width, height: window.innerHeight, part: false };
  }
  return {
    left: visual.offsetLeft,
    top: visual.offsetTop,
    width: visual.width,
    height: visual.height,
    part:
      visual.scale > 1.01 ||
      visual.offsetLeft > 0.5 ||
      visual.offsetTop > 0.5 ||
      visual.width < width - 1,
  };
}

const view = ref(inView());
const narrow = computed(() => view.value.width <= 600);
/** The panel's height, to keep it at the bottom of the part in view. */
const height = ref(0);

/** The words found in the line, as opposed to those looked up in a definition. */
let inLine = new WeakSet<LookupResult>();
let asking: AbortController | null = null;

/** The word's sound that plays, and what fetches the next. */
let playing: { audio: HTMLAudioElement; url: string } | null = null;
let fetchingSound: AbortController | null = null;

/**
 * The sound chosen for each word from the audio button's list, as Yomitan keeps it: the button
 * plays it from then on, and the word's card takes it.
 */
const chosenSounds = new Map<string, string>();

function wordKey(result: LookupResult): string {
  return `${result.term.expression}\u0000${result.term.reading}`;
}

function stopSound(): void {
  fetchingSound?.abort();
  fetchingSound = null;
  if (playing !== null) {
    playing.audio.pause();
    URL.revokeObjectURL(playing.url);
    playing = null;
  }
}

/** Plays how a word sounds, from the server's audio source: the sound chosen, if one was. */
async function playSound(result: LookupResult, chosen?: string): Promise<void> {
  stopSound();
  if (chosen !== undefined) chosenSounds.set(wordKey(result), chosen);
  const sound = chosenSounds.get(wordKey(result));
  const controller = new AbortController();
  fetchingSound = controller;
  const { expression, reading } = result.term;
  try {
    const blob = await props.api.wordAudio(expression, reading, controller.signal, sound);
    if (controller.signal.aborted) return;
    const url = URL.createObjectURL(blob);
    const audio = new Audio(url);
    playing = { audio, url };
    audio.addEventListener('ended', () => {
      if (playing?.audio === audio) stopSound();
    });
    await audio.play();
  } catch (error) {
    if (controller.signal.aborted) return;
    const message = error instanceof Error ? error.message : String(error);
    status.value = {
      text: `No sound of ${expression}${sound === undefined ? '' : ` (${sound})`}: ${message}`,
      error: true,
    };
  }
}

/** What the popup is told of the user's choices of how it looks. */
function lookOf(): {
  scale: number;
  display: { glossaryLayoutMode: 'default' | 'compact' };
  css: string;
} {
  const look = props.look;
  return {
    scale: look?.textSize ?? 100,
    display: { glossaryLayoutMode: look?.compact === true ? 'compact' : 'default' },
    // The definitions scroll in the popup, not the page behind it.
    css: [
      '.gsm-hoshidicts-content-scroll { overscroll-behavior: contain; }',
      look?.hideTags === true ? HIDDEN_TAGS : '',
    ].join('\n'),
  };
}

/**
 * The tags left out when the user hides them: the popup's own (★, the dictionary's name, a
 * word's tags) and those dictionaries draw in their definitions, as Jitendex's parts of speech.
 * A word's frequencies and pitch stay.
 */
const HIDDEN_TAGS = `
.gsm-hoshidicts-definition-tags,
.definition-tag-list,
.gsm-hoshidicts-tag-term,
[data-sc-class="tag"],
[data-sc-content="part-of-speech-info"],
[data-sc-content="misc-info"],
[data-sc-content="field-info"],
[data-sc-content="dialect-info"] { display: none !important; }
ul[data-sc-content="sense-groups"] { list-style: none !important; padding-left: 0 !important; }`;

function styles(): { dictionary: string; styles: string }[] {
  return props.dictionaries
    .filter((dictionary) => dictionary.Styles !== '')
    .map((dictionary) => ({ dictionary: dictionary.Title, styles: dictionary.Styles }));
}

function plainState(): CardState {
  return props.addCard === undefined ? 'unavailable' : 'add';
}

/**
 * Makes a card of a result the popup shows. Its button changes by the result, not by its place:
 * by the time Anki answers, another word may be shown there.
 */
/** The text selected in the popup's definitions, as Yomitan's {selection-text}: "" without one. */
function selectedText(): string {
  const host = popup.value?.element;
  const root = host?.shadowRoot as (ShadowRoot & { getSelection?: () => Selection | null }) | null;
  if (host === undefined || root === null || root === undefined) return '';
  // Chrome tells a selection in a shadow root only from there; the others from the document.
  const selection = root.getSelection?.() ?? document.getSelection();
  const node = selection?.anchorNode ?? null;
  if (selection === null || node === null || !(root.contains(node) || host.contains(node))) {
    return '';
  }
  return selection.toString().trim();
}

/**
 * The selection when the + button was pressed: a press on a button may take the selection away
 * before its click is heard.
 */
let pressedWith: string | null = null;

function onPress(): void {
  pressedWith = selectedText();
}

async function makeCard(result: LookupResult): Promise<void> {
  const add = props.addCard;
  const shown = popup.value;
  const selection = pressedWith ?? selectedText();
  pressedWith = null;
  if (add === undefined || shown === null) return;
  shown.setResultState(result, 'adding');
  status.value = { text: `Making a card of ${result.term.expression}…`, error: false };
  try {
    const said = await add(
      result,
      inLine.has(result),
      selection,
      chosenSounds.get(wordKey(result)),
    );
    shown.setResultState(result, 'in-anki', said);
    status.value = { text: said, error: false };
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    shown.setResultState(
      result,
      error instanceof DuplicateCardError ? 'in-anki' : 'error',
      message,
    );
    status.value = { text: message, error: !(error instanceof DuplicateCardError) };
  }
}

async function made(): Promise<DictionaryPopup<LookupResult>> {
  if (popup.value !== null) return popup.value;
  const { createDictionaryPopup } = await loadPopup();
  if (popup.value !== null) return popup.value;
  const titles = new Map(props.dictionaries.map((dictionary) => [dictionary.Title, dictionary.Id]));
  const created = createDictionaryPopup<LookupResult>(holder.value!, {
    theme: 'dark',
    opacity: 100,
    dictionaries: props.dictionaries.map((dictionary) => ({
      title: dictionary.Title,
      frequencyMode: dictionary.FrequencyMode ?? null,
    })),
    ...lookOf(),
    onAddCard: (result) => void makeCard(result),
    onClose: () => emit('close'),
    // The button is there for every popup and shown only while the server has an audio source.
    // Held, it lists the word's sounds to choose one, as in Yomitan.
    onPlayAudio: (result, _index, chosen) => void playSound(result, chosen),
    hasAudio: () => props.wordAudio === true,
    audioSources: (result) =>
      props.api.wordAudioSources(result.term.expression, result.term.reading),
    chosenAudio: (result) => chosenSounds.get(wordKey(result)),
    lookup: async (text) => {
      const found = await props.api.lookUp(text);
      return {
        results: found.results,
        styles: styles(),
        states: found.results.map(plainState),
      };
    },
    media: (dictionary, path) => {
      const id = titles.get(dictionary);
      return id === undefined
        ? Promise.reject(new Error('No such dictionary.'))
        : props.api.dictionaryPicture(id, path);
    },
  });
  popup.value = created;
  return created;
}

async function lookUp(request: WordRequest): Promise<void> {
  asking?.abort();
  const controller = new AbortController();
  asking = controller;
  status.value = null;
  state.value = 'looking';
  try {
    const [shown, found] = await Promise.all([
      made(),
      props.api.lookUp(request.text, controller.signal),
    ]);
    if (controller.signal.aborted) return;
    const results = found.results;
    inLine = new WeakSet(results);
    emit('found', results[0]?.matched.length ?? 0);
    if (results.length === 0) {
      state.value = 'none';
      shown.clear();
      return;
    }
    const check = props.inDeck;
    shown.render(results, {
      styles: styles(),
      states: results.map(() => (check === undefined ? plainState() : 'waiting')),
    });
    state.value = 'shown';
    if (check === undefined) return;
    let known: boolean[];
    try {
      known = await check(results);
    } catch {
      known = results.map(() => false);
    }
    if (controller.signal.aborted) return;
    // A word in a definition may be shown by now: these are the line's words.
    known.forEach((has, index) =>
      shown.setResultState(results[index]!, has ? 'in-anki' : plainState()),
    );
  } catch (error) {
    if (controller.signal.aborted) return;
    emit('found', 0);
    state.value = 'failed';
    status.value = { text: error instanceof Error ? error.message : String(error), error: true };
  }
}

watch(
  () => [props.look?.textSize, props.look?.hideTags, props.look?.compact],
  () => popup.value?.setTheme(lookOf()),
);

watch(
  () => props.request,
  (request) => {
    if (request !== null) void lookUp(request);
    else stopSound();
  },
  { immediate: true },
);

/**
 * Beside the word on a wide screen, below it where there is room, else above it; on a narrow one
 * along the bottom. Either way inside the part of the page in view.
 */
const place = computed<Record<string, string>>(() => {
  const request = props.request;
  const seen = view.value;
  if (request === null) return {};
  if (narrow.value) {
    if (!seen.part) return {};
    const most = Math.round(seen.height * 0.6);
    return {
      left: `${seen.left}px`,
      right: 'auto',
      width: `${seen.width}px`,
      top: `${seen.top + seen.height - Math.min(height.value, most)}px`,
      bottom: 'auto',
      maxHeight: `${most}px`,
    };
  }
  const width = Math.min(520, seen.width - 32);
  const left = Math.min(
    Math.max(seen.left + 16, request.box.left - 24),
    seen.left + seen.width - width - 16,
  );
  const below = seen.top + seen.height - request.box.bottom - 16;
  const above = request.box.top - seen.top - 16;
  return below >= 280 || below >= above
    ? {
        left: `${left}px`,
        width: `${width}px`,
        top: `${request.box.bottom + 8}px`,
        maxHeight: `${below - 8}px`,
      }
    : {
        left: `${left}px`,
        width: `${width}px`,
        bottom: `${window.innerHeight - request.box.top + 8}px`,
        maxHeight: `${above - 8}px`,
      };
});

function onKey(event: KeyboardEvent): void {
  if (event.key === 'Escape' && props.request !== null) {
    if (popup.value?.back() === true) return;
    emit('close');
  }
}

function onPointer(event: PointerEvent): void {
  if (props.request === null || !(event.target instanceof Node)) return;
  if (panel.value?.contains(event.target) === true) return;
  // A tap on another word looks that one up; the line's own handler sees to it.
  if (event.target instanceof Element && event.target.closest('[data-word-text]') !== null) return;
  emit('close');
}

function onScroll(): void {
  // Beside a word, the popup would be left behind by it.
  if (props.request !== null && !narrow.value) emit('close');
}

function onResize(): void {
  view.value = inView();
}

const measure =
  typeof ResizeObserver === 'function'
    ? new ResizeObserver(() => {
        height.value = panel.value?.offsetHeight ?? 0;
      })
    : null;

onMounted(() => {
  window.addEventListener('keydown', onKey);
  window.addEventListener('pointerdown', onPointer, true);
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onResize);
  // Zooming in on a phone, and moving about while zoomed, change the part in view.
  window.visualViewport?.addEventListener('resize', onResize);
  window.visualViewport?.addEventListener('scroll', onResize);
  if (panel.value) measure?.observe(panel.value);
});

onBeforeUnmount(() => {
  asking?.abort();
  stopSound();
  window.removeEventListener('keydown', onKey);
  window.removeEventListener('pointerdown', onPointer, true);
  window.removeEventListener('scroll', onScroll);
  window.removeEventListener('resize', onResize);
  window.visualViewport?.removeEventListener('resize', onResize);
  window.visualViewport?.removeEventListener('scroll', onResize);
  measure?.disconnect();
  popup.value?.destroy();
});
</script>

<template>
  <div
    v-show="request !== null"
    ref="panel"
    class="word-popup"
    :class="{ sheet: narrow }"
    :style="place"
    role="dialog"
    aria-label="Dictionary"
    data-testid="word-popup"
  >
    <p v-if="state === 'looking' && popup === null" class="note muted" role="status">
      Looking it up…
    </p>
    <p v-else-if="state === 'none'" class="note muted" role="status" data-testid="word-none">
      No word in the dictionaries starts here.
    </p>
    <div
      ref="holder"
      class="holder"
      :hidden="state !== 'shown'"
      @pointerdown.capture="onPress"
      @keydown.capture="pressedWith = null"
    />
    <p
      v-if="status !== null"
      class="status"
      :class="{ error: status.error }"
      :role="status.error ? 'alert' : 'status'"
      data-testid="word-status"
    >
      {{ status.text }}
    </p>
    <p v-if="state === 'shown' && addCard === undefined && noCards !== ''" class="status muted">
      {{ noCards }}
    </p>
  </div>
</template>

<style scoped>
.word-popup {
  position: fixed;
  z-index: 30;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  box-shadow: 0 8px 24px rgb(0 0 0 / 45%);
}

.word-popup.sheet {
  right: 0;
  bottom: 0;
  left: 0;
  max-height: 60vh;
  padding-bottom: env(safe-area-inset-bottom);
  border-width: 1px 0 0;
  border-radius: var(--radius) var(--radius) 0 0;
}

.holder {
  display: flex;
  flex-direction: column;
  flex: 1 1 auto;
  min-height: 0;
}

.holder[hidden] {
  display: none;
}

/* The popup's own element: as wide as the panel, its definitions scrolling inside. */
.holder :deep(.dictionary-popup) {
  flex: 1 1 auto;
  min-height: 0;
}

.note,
.status {
  padding: 10px 14px;
  font-size: 13px;
}

.status {
  border-top: 1px solid var(--border);
}

.error {
  color: var(--danger);
}
</style>
