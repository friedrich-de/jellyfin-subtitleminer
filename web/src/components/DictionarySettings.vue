<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';

import type { PluginApi } from '@/api/plugin';
import { TEXT_SIZES } from '@/anki/pageSettings';
import type {
  DictionariesView,
  DictionaryImportOutcome,
  DictionaryView,
  WordAudioSaved,
} from '@/api/types';

const props = defineProps<{ api: PluginApi }>();

/** Whether a tap on a word looks it up, as the user's page settings keep it. */
const lookUp = defineModel<boolean>('lookUp', { required: true });
/** How the popup looks, as the user's page settings keep it. */
const textSize = defineModel<number>('textSize', { required: true });
const hideTags = defineModel<boolean>('hideTags', { required: true });
const compact = defineModel<boolean>('compact', { required: true });

/** Dictionaries an administrator may add with one tap: free, and the ones most people use. */
const SUGGESTED = [
  {
    name: 'Jitendex',
    text: 'Japanese to English',
    url: 'https://github.com/stephenmk/stephenmk.github.io/releases/latest/download/jitendex-yomitan.zip',
  },
  {
    name: 'JMnedict',
    text: 'names of people and places',
    url: 'https://github.com/yomidevs/jmdict-yomitan/releases/latest/download/JMnedict.zip',
  },
] as const;

const list = shallowRef<DictionariesView | null>(null);
const failed = ref<string | null>(null);
const message = ref<{ text: string; error: boolean } | null>(null);
const busy = ref(false);
/** The file that is on its way to the server, and which of those picked it is. */
const sending = ref<{ name: string; at: number; of: number } | null>(null);
const address = ref('');
const confirming = ref<string | null>(null);
const audioAddress = ref('');
const audioMessage = ref<{ text: string; error: boolean } | null>(null);
let polling: number | null = null;

/** How often an import that runs is looked at. A large dictionary is in within a few seconds. */
const LOOK_EVERY_MS = 400;
/** No more than this many uploads wait on the server at once: each is a file there until its turn. */
const SENT_AHEAD = 4;
const STOPPED = 'The import was stopped.';

/** The number of the last import whose end this page knows of; null before the first look. */
let known: number | null = null;
/** Imports that ended and are not told of yet, and files that never got to the server. */
let ended: DictionaryImportOutcome[] = [];
let unsent: { what: string; why: string }[] = [];
/** Whether this page saw the import run or started it: only then does it say how it ended. */
let watching = false;
/** Whether Stop was pressed while files were on their way. */
let halted = false;
let upload: AbortController | null = null;

const enabled = computed(() => list.value?.Dictionaries.filter((item) => item.Enabled) ?? []);
const running = computed(() => {
  const state = list.value?.Import.State;
  return state === 'downloading' || state === 'importing';
});
/**
 * Whether a suggested dictionary is there already: by the address it updates from, or by its title,
 * which carries its revision ("Jitendex.org [2026-11-23]").
 */
function isInstalled(suggestion: (typeof SUGGESTED)[number]): boolean {
  const named = new RegExp(`^${suggestion.name}(?![a-z])`, 'i');
  return (list.value?.Dictionaries ?? []).some(
    (item) => item.DownloadUrl === suggestion.url || named.test(item.Title),
  );
}

/** What an import is of: a suggested dictionary by its name, an address by its file's name. */
function sourceName(source: string): string {
  const suggested = SUGGESTED.find((suggestion) => suggestion.url === source);
  if (suggested !== undefined) return suggested.name;
  try {
    const file = new URL(source).pathname
      .split('/')
      .filter((part) => part !== '')
      .at(-1);
    return file === undefined ? source : decodeURIComponent(file);
  } catch {
    return source;
  }
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function stopPolling(): void {
  if (polling !== null) window.clearTimeout(polling);
  polling = null;
}

/** Keeps the imports that ended since the last look. Those from before the page was there are not its to tell. */
function collect(finished: readonly DictionaryImportOutcome[]): void {
  const last = finished.at(-1)?.Number ?? 0;
  if (known === null) {
    known = last;
    return;
  }
  // The server counts from 1 again after a restart.
  if (last < known) known = 0;
  for (const outcome of finished) {
    if (outcome.Number > known) {
      ended.push(outcome);
      known = outcome.Number;
    }
  }
}

/**
 * How the imports ended, in words: one in a line of its own, several as a count of those that are
 * ready and a line for each reason the others are not.
 */
function summary(): { text: string; error: boolean } | null {
  const ready = ended
    .filter((outcome) => outcome.Error === undefined)
    .map((outcome) => outcome.Title ?? sourceName(outcome.Source));
  const not = [
    ...ended
      .filter((outcome) => outcome.Error !== undefined)
      .map((outcome) => ({ what: sourceName(outcome.Source), why: outcome.Error! })),
    ...unsent,
  ];
  if (ready.length + not.length === 0) return null;
  if (not.length === 0 && ready.length === 1)
    return { text: `${ready[0]} is ready.`, error: false };
  if (ready.length === 0 && not.length === 1) return { text: not[0]!.why, error: true };

  const lines: string[] = [];
  if (ready.length === 1) lines.push(`${ready[0]} is ready.`);
  if (ready.length > 1) lines.push(`${ready.length} dictionaries are ready: ${ready.join(', ')}.`);
  for (const why of new Set(not.map((item) => item.why))) {
    const which = not.filter((item) => item.why === why).map((item) => item.what);
    lines.push(`${which.join(', ')}: ${why}`);
  }
  return { text: lines.join('\n'), error: not.length > 0 };
}

/** Says how the imports ended, once none runs and no file is on its way. */
function settle(): void {
  if (running.value || sending.value !== null) return;
  const said = watching ? summary() : null;
  ended = [];
  unsent = [];
  watching = false;
  if (said !== null) message.value = said;
}

/** Takes a new list in, and follows an import that runs until it and those behind it end. */
function took(view: DictionariesView): void {
  list.value = view;
  stopPolling();
  collect(view.Import.Finished);
  if (running.value) {
    watching = true;
    polling = window.setTimeout(() => void refresh(), LOOK_EVERY_MS);
  } else {
    settle();
  }
}

async function refresh(): Promise<void> {
  try {
    took(await props.api.dictionaries());
    failed.value = null;
  } catch (error) {
    failed.value = messageOf(error);
    // An import is followed to its end, also past a look the server did not answer.
    if (running.value) {
      stopPolling();
      polling = window.setTimeout(() => void refresh(), LOOK_EVERY_MS);
    }
  }
}

/**
 * From here on the page says how imports end. It looks first, so that one another page started
 * and that ended unseen is not told of as if it were this page's.
 */
async function watch(): Promise<void> {
  if (!watching) {
    await refresh();
    if (!watching) ended = [];
  }
  watching = true;
}

async function act(change: () => Promise<DictionariesView>, starts = false): Promise<void> {
  busy.value = true;
  message.value = null;
  try {
    if (starts) await watch();
    took(await change());
  } catch (error) {
    message.value = { text: messageOf(error), error: true };
  } finally {
    busy.value = false;
  }
}

function arrange(order: readonly DictionaryView[], disabled: Iterable<string>): Promise<void> {
  return act(() =>
    props.api.arrangeDictionaries(
      order.map((item) => item.Id),
      [...disabled],
    ),
  );
}

function disabledNow(): Set<string> {
  return new Set(list.value?.Dictionaries.filter((item) => !item.Enabled).map((item) => item.Id));
}

function move(index: number, by: -1 | 1): void {
  const order = [...(list.value?.Dictionaries ?? [])];
  const other = index + by;
  if (other < 0 || other >= order.length) return;
  [order[index], order[other]] = [order[other]!, order[index]!];
  void arrange(order, disabledNow());
}

function setEnabled(item: DictionaryView, on: boolean): void {
  const disabled = disabledNow();
  if (on) disabled.delete(item.Id);
  else disabled.add(item.Id);
  void arrange(list.value?.Dictionaries ?? [], disabled);
}

function remove(item: DictionaryView): void {
  confirming.value = null;
  void act(() => props.api.removeDictionary(item.Id));
}

const pause = (ms: number): Promise<void> =>
  new Promise((resolve) => window.setTimeout(resolve, ms));

/**
 * Sends the picked files to the server one after another. The server imports them in that order,
 * each as soon as it is there, while the next is on its way.
 */
async function send(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const files = [...(input.files ?? [])];
  if (files.length === 0) return;
  message.value = null;
  halted = false;
  upload = new AbortController();
  for (const [index, file] of files.entries()) {
    if (index === 0) {
      sending.value = { name: file.name, at: 1, of: files.length };
      await watch();
    }
    if (!halted) {
      sending.value = { name: file.name, at: index + 1, of: files.length };
      try {
        // Not too many at once: the server keeps each as a file until its turn.
        while (!halted && running.value && (list.value?.Import.Waiting ?? 0) >= SENT_AHEAD) {
          await pause(LOOK_EVERY_MS / 4);
        }
        if (!halted) {
          took(await props.api.uploadDictionary(file, file.name, upload.signal));
          continue;
        }
      } catch (error) {
        if (!halted) {
          unsent.push({ what: file.name, why: messageOf(error) });
          continue;
        }
      }
    }
    unsent.push({ what: file.name, why: STOPPED });
  }
  upload = null;
  sending.value = null;
  input.value = '';
  settle();
}

/** Stops the import that runs, those that wait, and the files still on their way. */
function stop(): Promise<void> {
  halted = true;
  upload?.abort();
  return act(() => props.api.stopDictionaryImport());
}

function download(url: string): void {
  const typed = url.trim();
  if (typed === '') return;
  void act(() => props.api.downloadDictionary(typed), true).then(() => {
    if (message.value?.error !== true) address.value = '';
  });
}

function describe(item: DictionaryView): string {
  const count = (n: number, one: string, many: string): string =>
    `${n.toLocaleString('en')} ${n === 1 ? one : many}`;
  const parts: string[] = [];
  if (item.Terms > 0) parts.push(count(item.Terms, 'word', 'words'));
  if (item.Frequencies > 0) parts.push(count(item.Frequencies, 'frequency', 'frequencies'));
  if (item.Pitches > 0) parts.push(count(item.Pitches, 'pitch accent', 'pitch accents'));
  parts.push(`${Math.max(0.1, item.Bytes / 1_048_576).toFixed(1)} MB`);
  if (item.Revision !== '') parts.push(`revision ${item.Revision}`);
  return parts.join(' · ');
}

function webSite(item: DictionaryView): string | undefined {
  return item.Url !== undefined && /^https?:\/\//i.test(item.Url) ? item.Url : undefined;
}

const wordAudio = computed(() => list.value?.WordAudio ?? { Set: false });

const TRIED: Readonly<Record<WordAudioSaved['Tried'], { text: string; error: boolean }>> = {
  found: { text: 'Saved. It works: it has a sound for 食べる.', error: false },
  notFound: {
    text: 'Saved, but it has no sound for 食べる. Check that {term} and {reading} are in the right places.',
    error: true,
  },
  failed: {
    text: "Saved, but it could not be reached, or did not answer as Yomitan's audio sources do. Check the address and its key.",
    error: true,
  },
  none: { text: 'Words have no sound now.', error: false },
};

/** Sets the audio source for words, or takes it away with "", and says how a try of it went. */
async function saveWordAudio(typed: string): Promise<void> {
  busy.value = true;
  audioMessage.value = null;
  try {
    const saved = await props.api.saveWordAudio(typed.trim());
    if (list.value !== null) list.value = { ...list.value, WordAudio: saved.WordAudio };
    audioMessage.value = TRIED[saved.Tried];
    // The address often holds a key: it is not kept on the page.
    audioAddress.value = '';
  } catch (error) {
    audioMessage.value = { text: messageOf(error), error: true };
  } finally {
    busy.value = false;
  }
}

const importText = computed(() => {
  const job = list.value?.Import;
  if (job === undefined) return '';
  const what = job.Title ?? (job.Source === undefined ? 'the dictionary' : sourceName(job.Source));
  const doing = job.State === 'downloading' ? 'Downloading' : 'Importing';
  const more = job.Waiting > 0 ? `, ${job.Waiting} more waiting` : '';
  return `${doing} ${what}… ${Math.round(job.Progress * 100)}%${more}`;
});

const sendingText = computed(() => {
  const file = sending.value;
  if (file === null) return '';
  const which = file.of > 1 ? ` (${file.at} of ${file.of})` : '';
  return `Sending ${file.name} to the server${which}…`;
});

onMounted(() => void refresh());
onBeforeUnmount(() => {
  stopPolling();
  // The files already on the server are imported all the same; no more are sent.
  halted = true;
  upload?.abort();
});
</script>

<template>
  <section class="group" aria-labelledby="dictionary-title" data-testid="dictionaries">
    <h2 id="dictionary-title">Dictionary</h2>

    <p v-if="list === null" class="muted small" :role="failed === null ? 'status' : 'alert'">
      {{ failed ?? 'Reading the dictionaries…' }}
    </p>

    <template v-else>
      <template v-if="enabled.length > 0">
        <label class="check">
          <input v-model="lookUp" type="checkbox" data-testid="look-up" />
          Look a word up when I tap it
        </label>
        <p class="muted small">
          In {{ enabled.map((item) => item.Title).join(', ') }}, on the Jellyfin server. A card of
          the word can be made from there. Turn it off to look words up with your own Yomitan
          instead.
        </p>
        <div v-if="lookUp" class="look" data-testid="popup-look">
          <label class="field">
            <span class="name">Text size in the popup</span>
            <select v-model.number="textSize" data-testid="text-size">
              <option v-for="size in TEXT_SIZES" :key="size" :value="size">
                {{ size === 100 ? '100% (usual)' : `${size}%` }}
              </option>
            </select>
          </label>
          <label class="check">
            <input v-model="hideTags" type="checkbox" data-testid="hide-tags" />
            Hide the tags: ★, the dictionary's name, parts of speech and the like
          </label>
          <label class="check">
            <input v-model="compact" type="checkbox" data-testid="compact" />
            Compact definitions: each meaning's words on one line, as in Yomitan
          </label>
        </div>
      </template>
      <p v-else class="muted small" data-testid="no-dictionaries">
        This Jellyfin server has no dictionaries{{
          list.Dictionaries.length > 0 ? ' turned on' : ''
        }}
        to look words up in.
        {{ list.MayManage ? 'Add one below.' : 'An administrator of the server can add them.' }}
      </p>

      <template v-if="list.MayManage">
        <h3>The server's dictionaries, for everyone</h3>
        <ol v-if="list.Dictionaries.length > 0" class="dictionaries">
          <li
            v-for="(item, index) in list.Dictionaries"
            :key="item.Id"
            class="dictionary"
            :data-testid="`dictionary-${item.Title}`"
          >
            <div class="about">
              <span class="title">{{ item.Title }}</span>
              <span class="muted small">{{ describe(item) }}</span>
              <a
                v-if="webSite(item) !== undefined"
                class="small site"
                :href="webSite(item)"
                target="_blank"
                rel="noopener noreferrer"
                >Its web site</a
              >
            </div>
            <div class="tools">
              <button
                class="switch"
                type="button"
                role="switch"
                :aria-checked="item.Enabled"
                :aria-label="`Use ${item.Title}`"
                :disabled="busy"
                @click="setEnabled(item, !item.Enabled)"
              >
                <span class="knob" aria-hidden="true" />
              </button>
              <button
                class="button icon"
                type="button"
                :aria-label="`Move ${item.Title} up`"
                title="Move up"
                :disabled="busy || index === 0"
                @click="move(index, -1)"
              >
                <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
                  <path fill="currentColor" d="M7.4 15.4 12 10.8l4.6 4.6L18 14l-6-6-6 6z" />
                </svg>
              </button>
              <button
                class="button icon"
                type="button"
                :aria-label="`Move ${item.Title} down`"
                title="Move down"
                :disabled="busy || index === list.Dictionaries.length - 1"
                @click="move(index, 1)"
              >
                <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
                  <path fill="currentColor" d="M7.4 8.6 12 13.2l4.6-4.6L18 10l-6 6-6-6z" />
                </svg>
              </button>
              <button
                class="button icon"
                type="button"
                :aria-label="`Remove ${item.Title}`"
                title="Remove"
                :disabled="busy"
                @click="confirming = item.Id"
              >
                <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
                  <path
                    fill="currentColor"
                    d="M9 3h6l1 1h4v2H4V4h4zm-3 5h12l-1 13H7zm3 2v9h2v-9zm4 0v9h2v-9z"
                  />
                </svg>
              </button>
            </div>
            <div v-if="confirming === item.Id" class="confirm" role="alert">
              <span class="small">Remove {{ item.Title }} from the server, for everyone?</span>
              <button
                class="button danger"
                type="button"
                data-testid="confirm-remove"
                @click="remove(item)"
              >
                Remove
              </button>
              <button class="button" type="button" @click="confirming = null">Keep it</button>
            </div>
          </li>
        </ol>
        <p class="muted small">
          Words are looked up in this order: a word's first definitions and its frequency come from
          the first dictionary that has them.
        </p>

        <template v-if="running || sending !== null">
          <div class="importing" role="status" data-testid="import-progress">
            <span class="small">{{ running ? importText : sendingText }}</span>
            <progress v-if="running" max="1" :value="list.Import.Progress" />
            <progress v-else />
            <button class="button" type="button" :disabled="busy" @click="stop()">Stop</button>
          </div>
          <p
            v-if="running && sending !== null"
            class="muted small"
            role="status"
            data-testid="dictionary-sending"
          >
            {{ sendingText }}
          </p>
        </template>

        <template v-else>
          <div class="suggested">
            <button
              v-for="suggestion in SUGGESTED"
              :key="suggestion.name"
              class="button"
              type="button"
              :disabled="busy || isInstalled(suggestion)"
              @click="download(suggestion.url)"
            >
              Add {{ suggestion.name }}, {{ suggestion.text }}
            </button>
          </div>
          <label class="field">
            <span class="name">Download a Yomitan dictionary from its address</span>
            <span class="row">
              <input
                v-model="address"
                type="url"
                spellcheck="false"
                placeholder="https://example.org/dictionary.zip"
                data-testid="dictionary-address"
                @keydown.enter.prevent="download(address)"
              />
              <button
                class="button"
                type="button"
                :disabled="busy || address.trim() === ''"
                data-testid="dictionary-download"
                @click="download(address)"
              >
                Download
              </button>
            </span>
          </label>
          <label class="field">
            <span class="name">Or upload Yomitan dictionaries' zips from this device</span>
            <input
              type="file"
              accept=".zip,application/zip"
              multiple
              :disabled="busy"
              data-testid="dictionary-file"
              @change="send"
            />
            <span class="muted small">Several can be picked at once.</span>
          </label>
        </template>

        <h3>Word audio, for everyone</h3>
        <p class="muted small" data-testid="word-audio-state">
          <template v-if="wordAudio.Set">
            Words' sounds come from {{ wordAudio.Host ?? 'an audio source' }}, for the popup's play
            button and for a word card's ExpressionAudio.
          </template>
          <template v-else>
            Words have no sound. Give the address of an audio source as Yomitan's "Custom URL
            (JSON)" takes it, with {term} and {reading} where the word and its reading go, and the
            server fetches each word's sound from it.
          </template>
          The server keeps the address, with any key in it, and does not show it again.
        </p>
        <label class="field">
          <span class="name">{{
            wordAudio.Set ? 'Another audio source address' : 'Audio source address'
          }}</span>
          <span class="row">
            <input
              v-model="audioAddress"
              type="text"
              inputmode="url"
              autocomplete="off"
              spellcheck="false"
              placeholder="https://example.org/audio/list?term={term}&reading={reading}"
              data-testid="word-audio-address"
              @keydown.enter.prevent="saveWordAudio(audioAddress)"
            />
            <button
              class="button"
              type="button"
              :disabled="busy || audioAddress.trim() === ''"
              data-testid="word-audio-save"
              @click="saveWordAudio(audioAddress)"
            >
              Save
            </button>
          </span>
        </label>
        <div v-if="wordAudio.Set">
          <button
            class="button"
            type="button"
            :disabled="busy"
            data-testid="word-audio-remove"
            @click="saveWordAudio('')"
          >
            Remove the audio source
          </button>
        </div>
        <p
          v-if="audioMessage !== null"
          class="small"
          :class="audioMessage.error ? 'error' : 'ok'"
          :role="audioMessage.error ? 'alert' : 'status'"
          data-testid="word-audio-message"
        >
          {{ audioMessage.text }}
        </p>
      </template>

      <p
        v-if="message !== null"
        class="small"
        :class="message.error ? 'error' : 'ok'"
        :role="message.error ? 'alert' : 'status'"
        data-testid="dictionary-message"
      >
        {{ message.text }}
      </p>
    </template>
  </section>
</template>

<style scoped>
.group {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 20px 16px;
  border-radius: var(--radius);
  background: var(--surface);
}

h2 {
  font-size: 17px;
  font-weight: 600;
  color: var(--text-strong);
}

h3 {
  margin-top: 6px;
  font-size: 15px;
  font-weight: 600;
  color: var(--text-strong);
}

.check {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  cursor: pointer;
}

.check input {
  width: 20px;
  height: 20px;
  accent-color: var(--accent);
}

.look {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: 420px;
}

.dictionaries {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 0;
  list-style: none;
}

.dictionary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
}

.about {
  display: flex;
  flex: 1 1 220px;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.title {
  font-weight: 600;
  color: var(--text-strong);
  overflow-wrap: anywhere;
}

.site {
  color: var(--text);
}

.tools {
  display: flex;
  align-items: center;
  gap: 2px;
}

.icon {
  width: 44px;
  padding: 0;
  background: transparent;
}

.confirm {
  display: flex;
  flex: 1 1 100%;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
}

.danger {
  background: var(--danger);
  color: var(--on-accent);
}

.switch {
  display: inline-flex;
  align-items: center;
  min-width: 44px;
  min-height: 44px;
  padding: 0;
  border: 0;
  background: none;
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

.importing {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
}

.importing progress {
  flex: 1 1 160px;
  height: 8px;
  accent-color: var(--accent);
}

.suggested {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.name {
  font-size: 13px;
  color: var(--text-muted);
}

.row {
  display: flex;
  gap: 8px;
}

input[type='url'],
select {
  flex: 1 1 auto;
  width: 100%;
  min-width: 0;
  min-height: 44px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--bg);
  color: var(--text-strong);
  font: inherit;
}

input[type='file'] {
  color: var(--text);
  font: inherit;
}

input:focus-visible {
  outline-offset: 0;
  border-color: var(--accent);
}

.small {
  font-size: 13px;
}

.ok {
  color: var(--text);
}

.error {
  color: var(--danger);
}

/* Several dictionaries at once end in several lines. */
[data-testid='dictionary-message'] {
  white-space: pre-line;
}

@media (prefers-reduced-motion: reduce) {
  .knob::after {
    transition: none;
  }
}
</style>
