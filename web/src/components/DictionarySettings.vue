<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';

import type { PluginApi } from '@/api/plugin';
import type { DictionariesView, DictionaryView, WordAudioSaved } from '@/api/types';

const props = defineProps<{ api: PluginApi }>();

/** Whether a tap on a word looks it up, as the user's page settings keep it. */
const lookUp = defineModel<boolean>('lookUp', { required: true });

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
const sending = ref<string | null>(null);
const address = ref('');
const confirming = ref<string | null>(null);
const audioAddress = ref('');
const audioMessage = ref<{ text: string; error: boolean } | null>(null);
let polling: number | null = null;

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

/**
 * Takes a new list in, and follows an import that runs until it ends. `started` is for the
 * answer to starting one, which may have ended by then.
 */
function took(view: DictionariesView, started = false): void {
  const before = list.value?.Import.State;
  list.value = view;
  stopPolling();
  if (running.value) {
    polling = window.setTimeout(() => void refresh(), 1_000);
  } else if (started || before === 'downloading' || before === 'importing') {
    if (view.Import.State === 'done') {
      message.value = { text: `${view.Import.Title ?? 'The dictionary'} is ready.`, error: false };
    } else if (view.Import.State === 'failed') {
      message.value = { text: view.Import.Error ?? 'The import failed.', error: true };
    }
  }
}

async function refresh(): Promise<void> {
  try {
    took(await props.api.dictionaries());
    failed.value = null;
  } catch (error) {
    failed.value = messageOf(error);
  }
}

async function act(change: () => Promise<DictionariesView>, starts = false): Promise<void> {
  busy.value = true;
  message.value = null;
  try {
    took(await change(), starts);
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

async function upload(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (file === undefined) return;
  sending.value = file.name;
  await act(() => props.api.uploadDictionary(file, file.name), true);
  sending.value = null;
  input.value = '';
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
  return job.State === 'downloading'
    ? `Downloading ${what}… ${Math.round(job.Progress * 100)}%`
    : `Importing ${what}… ${Math.round(job.Progress * 100)}%`;
});

onMounted(() => void refresh());
onBeforeUnmount(stopPolling);
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

        <div v-if="running" class="importing" role="status" data-testid="import-progress">
          <span class="small">{{ importText }}</span>
          <progress max="1" :value="list.Import.Progress" />
          <button
            class="button"
            type="button"
            :disabled="busy"
            @click="act(() => api.stopDictionaryImport())"
          >
            Stop
          </button>
        </div>

        <template v-else>
          <div class="suggested">
            <button
              v-for="suggestion in SUGGESTED"
              :key="suggestion.name"
              class="button"
              type="button"
              :disabled="busy || sending !== null || isInstalled(suggestion)"
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
            <span class="name">Or upload a Yomitan dictionary's zip from this device</span>
            <input
              type="file"
              accept=".zip,application/zip"
              :disabled="busy"
              data-testid="dictionary-file"
              @change="upload"
            />
          </label>
          <p v-if="sending !== null" class="muted small" role="status">
            Sending {{ sending }} to the server…
          </p>
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

input[type='url'] {
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

@media (prefers-reduced-motion: reduce) {
  .knob::after {
    transition: none;
  }
}
</style>
