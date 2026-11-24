<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue';

import { type Box, characterAt, startsWord } from '@/dictionary/caret';
import { clockOf, type ShownLine } from '@/watch/watches';

/** Part of a line, as the said text is drawn: the word looked up apart from the rest. */
interface Part {
  text: string;
  word: boolean;
}

const props = defineProps<{
  lines: readonly ShownLine[];
  /** The lines on screen at this moment. */
  current: ReadonlySet<number>;
  selected: ReadonlySet<number>;
  /** The line the page keeps in view while it follows playback. */
  anchor: number | undefined;
  follow: boolean;
  lang: string | undefined;
  secondLang: string | undefined;
  /** A tap on a word looks it up, instead of choosing the line. */
  lookUp: boolean;
  /** The word looked up, picked out in its line. */
  marked: { id: number; start: number; length: number } | undefined;
}>();

const emit = defineEmits<{
  toggle: [id: number, range: boolean];
  /** A tap on the said text while words are looked up: the character tapped, or none. */
  word: [id: number, at: { offset: number; box: Box } | null];
}>();

const list = useTemplateRef<HTMLOListElement>('list');

const KIND_NAMES = { sign: 'Sign', song: 'Song' } as const;

function scrollToAnchor(instant: boolean): void {
  if (props.anchor === undefined || list.value === null) return;
  const row = list.value.querySelector<HTMLElement>(`[data-line="${props.anchor}"]`);
  if (row === null || typeof row.scrollIntoView !== 'function') return;
  const still =
    instant || (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false);
  row.scrollIntoView({ block: 'center', behavior: still ? 'instant' : 'smooth' });
}

// While following, the line being said stays in the middle. A new track or episode jumps there.
watch(
  () => [props.anchor, props.follow] as const,
  async ([anchor, follow], before) => {
    if (!follow || anchor === undefined) return;
    await nextTick();
    scrollToAnchor(before === undefined || before[0] === undefined || before[1] !== follow);
  },
  { immediate: true },
);

watch(
  () => props.lines,
  async () => {
    if (!props.follow) return;
    await nextTick();
    scrollToAnchor(true);
  },
);

function parts(line: ShownLine): Part[] {
  const mark = props.marked;
  if (mark?.id !== line.Id || mark.length <= 0) return [{ text: line.Text, word: false }];
  const end = mark.start + mark.length;
  return [
    { text: line.Text.slice(0, mark.start), word: false },
    { text: line.Text.slice(mark.start, end), word: true },
    { text: line.Text.slice(end), word: false },
  ].filter((part) => part.text !== '');
}

function onRowClick(event: MouseEvent, line: ShownLine): void {
  // A click that ends a text selection is for copying, not for choosing the line.
  const chosen = window.getSelection?.()?.toString() ?? '';
  if (chosen !== '') return;
  const said = event.target instanceof Element ? event.target.closest('.said') : null;
  if (props.lookUp && said !== null) {
    const at = characterAt(said, event.clientX, event.clientY);
    emit('word', line.Id, at !== undefined && startsWord(line.Text[at.offset]) ? at : null);
    return;
  }
  emit('toggle', line.Id, event.shiftKey);
}

// Holding Shift over a word looks it up, as Yomitan scans with Shift: the mouse moved with Shift
// held, or Shift pressed with the mouse over the word.

/** Where the mouse is over the lines, for Shift pressed without moving it. */
let pointer: { x: number; y: number } | undefined;
/** The character looked up last, so moving within it asks nothing new. */
let scanned: string | undefined;

function scan(target: EventTarget | null, x: number, y: number): void {
  if (!props.lookUp || !(target instanceof Element)) return;
  const said = target.closest('.said');
  const row = said?.closest<HTMLElement>('[data-line]');
  if (said === null || said === undefined || row === null || row === undefined) return;
  const id = Number(row.dataset.line);
  const line = props.lines.find((candidate) => candidate.Id === id);
  const at = line === undefined ? undefined : characterAt(said, x, y);
  // Beside a word nothing happens: the popup stays as it is.
  if (line === undefined || at === undefined || !startsWord(line.Text[at.offset])) return;
  const key = `${id}:${at.offset}`;
  if (key === scanned) return;
  scanned = key;
  emit('word', id, at);
}

function onMove(event: MouseEvent): void {
  pointer = { x: event.clientX, y: event.clientY };
  if (event.shiftKey) scan(event.target, event.clientX, event.clientY);
  else scanned = undefined;
}

function onLeave(): void {
  pointer = undefined;
  scanned = undefined;
}

function onKey(event: KeyboardEvent): void {
  if (event.key !== 'Shift' || event.repeat || pointer === undefined) return;
  if (event.target instanceof Element && event.target.closest('input, textarea, select') !== null) {
    return;
  }
  scanned = undefined;
  scan(document.elementFromPoint(pointer.x, pointer.y), pointer.x, pointer.y);
}

onMounted(() => window.addEventListener('keydown', onKey));
onBeforeUnmount(() => window.removeEventListener('keydown', onKey));
</script>

<template>
  <ol
    ref="list"
    class="lines"
    aria-label="Subtitle lines"
    @mousemove="onMove"
    @mouseleave="onLeave"
  >
    <li
      v-for="line in lines"
      :key="line.Id"
      class="line"
      :class="{
        selected: selected.has(line.Id),
        current: current.has(line.Id),
        quiet: line.Kind !== 'dialogue',
      }"
      :data-line="line.Id"
      :aria-current="current.has(line.Id) ? 'time' : undefined"
    >
      <button
        class="check"
        type="button"
        role="checkbox"
        :aria-checked="selected.has(line.Id)"
        :aria-label="`Line at ${clockOf(line.StartMs)}`"
        @click="emit('toggle', line.Id, $event.shiftKey)"
      >
        <span class="box">
          <svg v-if="selected.has(line.Id)" viewBox="0 0 24 24" width="18" height="18">
            <path fill="currentColor" d="m9.5 16.2-4-4L4 13.7l5.5 5.5L20 8.7l-1.5-1.5z" />
          </svg>
        </span>
      </button>
      <span class="time">
        {{ clockOf(line.StartMs) }}
        <span v-if="line.Kind !== 'dialogue'" class="kind">{{ KIND_NAMES[line.Kind] }}</span>
      </span>
      <div class="text" :class="{ words: lookUp }" @click="onRowClick($event, line)">
        <p class="said" :lang="lang" :data-word-text="lookUp ? '' : undefined">
          <!-- Each part as it is, with no space of the template's around it. -->
          <span
            v-for="(part, index) in parts(line)"
            :key="index"
            :class="{ word: part.word }"
            v-text="part.text"
          />
        </p>
        <p v-if="line.second !== undefined" class="second" :lang="secondLang">
          {{ line.second }}
        </p>
      </div>
      <span v-if="current.has(line.Id)" class="playing" title="Playing now">
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path fill="currentColor" d="M4 10h3v10H4zm6.5-6h3v16h-3zM17 13h3v7h-3z" />
        </svg>
        <span class="visually-hidden">Playing now</span>
      </span>
    </li>
  </ol>
</template>

<style scoped>
.lines {
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  padding: 8px 0;
  list-style: none;
}

.line {
  display: grid;
  grid-template-columns: 44px 64px 1fr 28px;
  align-items: start;
  gap: 0 8px;
  padding: 10px 16px 10px 8px;
  border-radius: var(--radius);
}

.line.selected {
  background: var(--surface);
}

.line.current {
  background: color-mix(in srgb, var(--accent) 22%, var(--bg));
}

.check {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 44px;
  height: 44px;
  margin-top: -6px;
  padding: 0;
  border: 0;
  background: transparent;
  cursor: pointer;
}

.box {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: 2px solid var(--text-muted);
  border-radius: var(--radius);
}

.selected .box {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--on-accent);
}

.time {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding-top: 6px;
  font-size: 13px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.kind {
  font-size: 12px;
}

.text {
  min-width: 0;
  cursor: pointer;
}

.words .said {
  cursor: text;
}

.word {
  border-radius: 2px;
  background: color-mix(in srgb, var(--accent) 45%, transparent);
  color: var(--text-strong);
}

.said {
  font-size: 22px;
  line-height: 1.5;
  color: var(--text-strong);
  white-space: pre-line;
  overflow-wrap: anywhere;
}

.current .said {
  font-weight: 500;
}

.quiet .said {
  color: var(--text-muted);
  font-size: 18px;
}

.second {
  margin-top: 2px;
  font-size: 15px;
  color: var(--text-muted);
  white-space: pre-line;
  overflow-wrap: anywhere;
}

.playing {
  padding-top: 8px;
  color: var(--accent);
}

@media (width <= 600px) {
  .line {
    grid-template-columns: 44px 1fr 24px;
    padding: 8px 12px 8px 4px;
  }

  .time {
    grid-column: 2;
    grid-row: 1;
    flex-direction: row;
    gap: 8px;
    padding-top: 0;
    font-size: 12px;
  }

  .text {
    grid-column: 2;
    grid-row: 2;
  }

  .check {
    grid-row: 1 / span 2;
  }

  .playing {
    grid-column: 3;
    grid-row: 1 / span 2;
  }

  .said {
    font-size: 20px;
  }
}
</style>
