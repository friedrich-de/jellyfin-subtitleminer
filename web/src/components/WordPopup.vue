<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, useTemplateRef, watch } from 'vue';

import { DuplicateCardError } from '@/anki/cards';
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
   * Makes a card of a word. `inLine` is false for a word looked up in a definition. Resolves with
   * what to tell the user. Without it the popup's Anki button is off, saying why.
   */
  addCard: ((result: LookupResult, inLine: boolean) => Promise<string>) | undefined;
  /** Why no card can be made, for the Anki button. */
  noCards: string;
  /** Whether Anki has a card for each word already. */
  inDeck: ((results: LookupResult[]) => Promise<boolean[]>) | undefined;
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
const narrow = ref(window.innerWidth <= 600);

/** The words found in the line, as opposed to those looked up in a definition. */
let inLine = new WeakSet<LookupResult>();
let asking: AbortController | null = null;

function styles(): { dictionary: string; styles: string }[] {
  return props.dictionaries
    .filter((dictionary) => dictionary.Styles !== '')
    .map((dictionary) => ({ dictionary: dictionary.Title, styles: dictionary.Styles }));
}

function plainState(): CardState {
  return props.addCard === undefined ? 'unavailable' : 'add';
}

async function makeCard(result: LookupResult, index: number): Promise<void> {
  const add = props.addCard;
  const shown = popup.value;
  if (add === undefined || shown === null) return;
  shown.setCardState(index, 'adding');
  status.value = { text: `Making a card of ${result.term.expression}…`, error: false };
  try {
    const said = await add(result, inLine.has(result));
    shown.setCardState(index, 'in-anki', said);
    status.value = { text: said, error: false };
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    shown.setCardState(index, error instanceof DuplicateCardError ? 'in-anki' : 'error', message);
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
    // The definitions scroll in the popup, not the page behind it.
    css: '.gsm-hoshidicts-content-scroll { overscroll-behavior: contain; }',
    onAddCard: (result, index) => void makeCard(result, index),
    onClose: () => emit('close'),
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
    known.forEach((has, index) => shown.setCardState(index, has ? 'in-anki' : plainState()));
  } catch (error) {
    if (controller.signal.aborted) return;
    emit('found', 0);
    state.value = 'failed';
    status.value = { text: error instanceof Error ? error.message : String(error), error: true };
  }
}

watch(
  () => props.request,
  (request) => {
    if (request !== null) void lookUp(request);
  },
  { immediate: true },
);

/** Beside the word on a wide screen, below it where there is room, else above it. */
const place = computed<Record<string, string>>(() => {
  const request = props.request;
  if (request === null || narrow.value) return {};
  const width = Math.min(520, window.innerWidth - 32);
  const left = Math.min(Math.max(16, request.box.left - 24), window.innerWidth - width - 16);
  const below = window.innerHeight - request.box.bottom - 16;
  const above = request.box.top - 16;
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
  narrow.value = window.innerWidth <= 600;
}

onMounted(() => {
  window.addEventListener('keydown', onKey);
  window.addEventListener('pointerdown', onPointer, true);
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onResize);
});

onBeforeUnmount(() => {
  asking?.abort();
  window.removeEventListener('keydown', onKey);
  window.removeEventListener('pointerdown', onPointer, true);
  window.removeEventListener('scroll', onScroll);
  window.removeEventListener('resize', onResize);
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
    <div ref="holder" class="holder" :hidden="state !== 'shown'" />
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
