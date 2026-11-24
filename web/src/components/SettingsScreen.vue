<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';

import type { Fetch } from '@/api/http';
import type { PluginApi } from '@/api/plugin';
import type { AnkiWhere, SettingsChange, SettingsView } from '@/api/types';
import { type AnkiConnect, AnkiError, deviceAnki, serverAnki } from '@/anki/connect';
import { findLapis } from '@/anki/lapis';
import {
  defaultPageSettings,
  FIELD_ROLES,
  type FieldRole,
  PICTURE_WIDTHS,
  type PageSettings,
  readPageSettings,
  rolesFor,
  tagsFrom,
} from '@/anki/pageSettings';

import DictionarySettings from './DictionarySettings.vue';

const props = defineProps<{
  api: PluginApi;
  fetch: Fetch;
  /** The page's own address, whose origin AnkiConnect on the device has to let in. */
  base: string;
  settings: SettingsView;
}>();

const emit = defineEmits<{
  close: [];
  saved: [settings: SettingsView];
}>();

const current = ref<SettingsView>(props.settings);
const where = ref<AnkiWhere>(props.settings.Anki.Where);
const deviceAddress = ref(props.settings.Anki.DeviceAddress);
const deviceKey = ref(props.settings.Anki.DeviceKey);
const serverAddress = ref(props.settings.Anki.ServerAddress);
/** A new key for the server, typed. Empty keeps the one stored. */
const serverKey = ref('');
const page = ref<PageSettings>(readPageSettings(props.settings.Page));
const tags = ref(page.value.anki.tags.join(' '));

const origin = computed(() => new URL(props.base).origin);
const serverAllowed = computed(() => current.value.Anki.ServerAllowed);

/** AnkiConnect on the device must be on the device: the page's policy lets it call nothing else. */
const deviceIsLocal = computed(() => {
  try {
    const host = new URL(deviceAddress.value.trim()).hostname;
    return ['127.0.0.1', 'localhost'].includes(host);
  } catch {
    return false;
  }
});

type Connection =
  | { kind: 'idle' }
  | { kind: 'testing' }
  | { kind: 'connected'; decks: string[]; noteTypes: string[] }
  | { kind: 'failed'; message: string };

const connection = ref<Connection>({ kind: 'idle' });
// Decks, note types and errors are those of one Anki: choosing another sets them aside until it is connected.
watch(where, () => (connection.value = { kind: 'idle' }));
const fieldNames = ref<string[]>(Object.keys(page.value.anki.fields));
/** The Lapis note type chosen for the user, until they choose themselves. */
const lapisFound = ref<string | null>(null);
const saving = ref(false);
const message = ref<{ text: string; error: boolean } | null>(null);

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function ankiChange(): NonNullable<SettingsChange['Anki']> {
  return {
    Where: where.value,
    DeviceAddress: deviceAddress.value.trim() || 'http://127.0.0.1:8765',
    DeviceKey: deviceKey.value.trim(),
    ...(where.value === 'server' || serverAddress.value.trim() === ''
      ? { ServerAddress: serverAddress.value.trim() }
      : {}),
    ...(serverKey.value.trim() === '' ? {} : { ServerKey: serverKey.value.trim() }),
  };
}

function ankiFor(view: SettingsView): AnkiConnect | null {
  if (view.Anki.Where === 'device') {
    return deviceAnki(props.fetch, view.Anki.DeviceAddress, view.Anki.DeviceKey, props.api);
  }
  return view.Anki.Where === 'server' ? serverAnki(props.api) : null;
}

async function loadFields(anki: AnkiConnect, noteType: string): Promise<void> {
  if (noteType === '') {
    fieldNames.value = [];
    return;
  }
  const names = await anki.invoke<string[]>('modelFieldNames', { modelName: noteType });
  fieldNames.value = names;
  page.value.anki.fields = rolesFor(names, page.value.anki.fields);
}

/**
 * Chooses a Lapis note type, if this Anki has one, with the deck its cards are in and each field
 * filled as Lapis means it, for someone who has no note type chosen.
 */
async function chooseLapis(anki: AnkiConnect, decks: string[], noteTypes: string[]): Promise<void> {
  const found = await findLapis(anki, noteTypes, decks);
  if (found === undefined || page.value.anki.noteType !== '') return;
  page.value.anki.noteType = found.noteType;
  if (page.value.anki.deck === '') page.value.anki.deck = found.deck;
  page.value.anki.fields = found.fields;
  fieldNames.value = found.fieldNames;
  lapisFound.value = found.noteType;
}

/** Saves where the cards go, then asks that Anki for its decks and note types. */
async function connect(): Promise<void> {
  message.value = null;
  connection.value = { kind: 'testing' };
  const asked = where.value;
  const answer = (found: Connection) =>
    (connection.value = where.value === asked ? found : { kind: 'idle' });
  try {
    const view = await props.api.saveSettings({ Anki: ankiChange() });
    current.value = view;
    serverKey.value = '';
    emit('saved', view);
    const anki = ankiFor(view);
    if (anki === null) {
      connection.value = { kind: 'idle' };
      return;
    }
    await anki.invoke<number>('version');
    const [decks, noteTypes] = await Promise.all([
      anki.invoke<string[]>('deckNames'),
      anki.invoke<string[]>('modelNames'),
    ]);
    answer({ kind: 'connected', decks, noteTypes });
    if (where.value !== asked) return;
    if (page.value.anki.noteType === '') await chooseLapis(anki, decks, noteTypes);
    else await loadFields(anki, page.value.anki.noteType);
  } catch (error) {
    answer({ kind: 'failed', message: messageOf(error) });
  }
}

async function onNoteType(event: Event): Promise<void> {
  const name = (event.target as HTMLSelectElement).value;
  page.value.anki.noteType = name;
  page.value.anki.fields = {};
  lapisFound.value = null;
  const anki = ankiFor(current.value);
  if (anki === null) return;
  try {
    await loadFields(anki, name);
  } catch (error) {
    connection.value = {
      kind: 'failed',
      message: error instanceof AnkiError ? error.message : messageOf(error),
    };
  }
}

function setRole(field: string, event: Event): void {
  page.value.anki.fields[field] = (event.target as HTMLSelectElement).value as FieldRole;
}

function forgetServerKey(): void {
  serverKey.value = '';
  void props.api
    .saveSettings({ Anki: { ServerKey: '' } })
    .then((view) => {
      current.value = view;
      emit('saved', view);
    })
    .catch((error: unknown) => (message.value = { text: messageOf(error), error: true }));
}

async function save(): Promise<void> {
  saving.value = true;
  message.value = null;
  // What was typed, within the bounds the page reads it back in.
  page.value = readPageSettings({
    ...page.value,
    anki: { ...page.value.anki, tags: tagsFrom(tags.value) },
  });
  try {
    const view = await props.api.saveSettings({ Anki: ankiChange(), Page: page.value });
    current.value = view;
    serverKey.value = '';
    emit('saved', view);
    message.value = { text: 'Saved.', error: false };
  } catch (error) {
    message.value = { text: messageOf(error), error: true };
  } finally {
    saving.value = false;
  }
}

/**
 * Puts every choice of the page back as a new user has it, with Lapis chosen if this Anki has it.
 * Where the cards go stays, so that no address or key has to be typed again. Nothing is kept until
 * the settings are saved.
 */
async function restoreDefaults(): Promise<void> {
  page.value = defaultPageSettings();
  tags.value = page.value.anki.tags.join(' ');
  fieldNames.value = [];
  lapisFound.value = null;
  message.value = {
    text: 'Defaults restored. Where your cards go is kept. Save to keep the rest.',
    error: false,
  };
  const anki = ankiFor(current.value);
  const connected = connection.value;
  if (anki === null || connected.kind !== 'connected') return;
  try {
    await chooseLapis(anki, connected.decks, connected.noteTypes);
  } catch (error) {
    message.value = { text: messageOf(error), error: true };
  }
}

async function setEveryone(event: Event): Promise<void> {
  const allowed = (event.target as HTMLInputElement).checked;
  try {
    const view = await props.api.saveEveryone(allowed);
    current.value = view;
    emit('saved', view);
  } catch (error) {
    message.value = { text: messageOf(error), error: true };
  }
}

onMounted(() => {
  if (props.settings.Anki.Where !== 'off') void connect();
});

const WHERE_CHOICES: { value: AnkiWhere; title: string; text: string }[] = [
  { value: 'off', title: 'No Anki', text: 'The page shows the lines and nothing more.' },
  {
    value: 'device',
    title: 'Anki on this device',
    text: 'Desktop Anki or AnkiConnect for Android on the device you use this page on.',
  },
  {
    value: 'server',
    title: 'Anki on a server',
    text: 'An AnkiConnect the Jellyfin server sends your cards to, with its key. Nothing needs to run on this device.',
  },
];
</script>

<template>
  <div class="settings">
    <header class="head">
      <button class="button" type="button" data-testid="settings-back" @click="emit('close')">
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path fill="currentColor" d="M15.4 7.4 14 6l-6 6 6 6 1.4-1.4L10.8 12z" />
        </svg>
        Back
      </button>
      <h1>Settings</h1>
    </header>

    <form class="form" novalidate @submit.prevent="save">
      <section class="group" aria-labelledby="anki-title">
        <h2 id="anki-title">Anki</h2>

        <fieldset class="choices">
          <legend class="visually-hidden">Where your cards go</legend>
          <label
            v-for="choice in WHERE_CHOICES"
            :key="choice.value"
            class="choice"
            :class="{ chosen: where === choice.value }"
          >
            <input
              v-model="where"
              type="radio"
              name="where"
              :value="choice.value"
              :disabled="choice.value === 'server' && !serverAllowed"
              :data-testid="`where-${choice.value}`"
            />
            <span>
              <span class="choice-title">{{ choice.title }}</span>
              <span class="muted small">{{
                choice.value === 'server' && !serverAllowed
                  ? 'An administrator of this Jellyfin server has not let users do this.'
                  : choice.text
              }}</span>
            </span>
          </label>
        </fieldset>

        <template v-if="where === 'device'">
          <label class="field">
            <span class="name">AnkiConnect address</span>
            <input
              v-model="deviceAddress"
              type="url"
              spellcheck="false"
              data-testid="device-address"
            />
          </label>
          <p v-if="!deviceIsLocal" class="error small" role="alert">
            This page can only reach an Anki on this device (127.0.0.1 or localhost). For an Anki
            anywhere else, choose "Anki on a server".
          </p>
          <label class="field">
            <span class="name">Key, if your AnkiConnect asks for one</span>
            <input
              v-model="deviceKey"
              type="password"
              autocomplete="off"
              spellcheck="false"
              data-testid="device-key"
            />
          </label>
          <p class="muted small">
            AnkiConnect lets this page in once its address is in AnkiConnect's settings, in Anki
            under Tools, Add-ons, AnkiConnect, Config:
            <code class="code">"webCorsOriginList": ["{{ origin }}"]</code>
          </p>
        </template>

        <template v-if="where === 'server'">
          <label class="field">
            <span class="name">AnkiConnect address</span>
            <input
              v-model="serverAddress"
              type="url"
              spellcheck="false"
              placeholder="https://anki.example.org"
              data-testid="server-address"
            />
          </label>
          <label class="field">
            <span class="name">Key</span>
            <input
              v-model="serverKey"
              type="password"
              autocomplete="off"
              spellcheck="false"
              :placeholder="
                current.Anki.ServerKeySet ? 'Stored on the server. Type to replace it.' : ''
              "
              data-testid="server-key"
            />
          </label>
          <p class="muted small">
            The key stays on the Jellyfin server, encrypted; this page never gets it back.
            <button
              v-if="current.Anki.ServerKeySet"
              class="link"
              type="button"
              @click="forgetServerKey"
            >
              Remove the stored key
            </button>
          </p>
        </template>

        <div v-if="where !== 'off'" class="row">
          <button
            class="button"
            type="button"
            data-testid="connect"
            :disabled="connection.kind === 'testing' || (where === 'device' && !deviceIsLocal)"
            @click="connect"
          >
            {{ connection.kind === 'testing' ? 'Connecting…' : 'Connect' }}
          </button>
          <p
            v-if="connection.kind === 'connected'"
            class="small ok"
            role="status"
            data-testid="connected"
          >
            Connected: {{ connection.decks.length }}
            {{ connection.decks.length === 1 ? 'deck' : 'decks' }},
            {{ connection.noteTypes.length }}
            note types.
          </p>
          <p
            v-else-if="connection.kind === 'failed'"
            class="small error"
            role="alert"
            data-testid="connect-error"
          >
            {{ connection.message }}
          </p>
        </div>

        <template v-if="where !== 'off' && connection.kind === 'connected'">
          <label class="field">
            <span class="name">Deck for new cards</span>
            <select v-model="page.anki.deck" data-testid="deck">
              <option value="">Choose a deck</option>
              <option v-for="deck in connection.decks" :key="deck" :value="deck">{{ deck }}</option>
            </select>
          </label>

          <label class="field">
            <span class="name">Note type</span>
            <select :value="page.anki.noteType" data-testid="note-type" @change="onNoteType">
              <option value="">Choose a note type</option>
              <option v-for="name in connection.noteTypes" :key="name" :value="name">
                {{ name }}
              </option>
            </select>
          </label>
          <p v-if="lapisFound !== null" class="small ok" role="status" data-testid="lapis-found">
            "{{ lapisFound }}" is a Lapis note type, so it is chosen, with each field filled as
            Lapis means it. Change anything you like.
          </p>

          <div v-if="fieldNames.length > 0" class="fields" data-testid="fields">
            <p class="name">What goes into each field</p>
            <label v-for="field in fieldNames" :key="field" class="field-row">
              <span class="field-name">{{ field }}</span>
              <select
                :value="page.anki.fields[field] ?? 'none'"
                :data-testid="`role-${field}`"
                @change="setRole(field, $event)"
              >
                <option v-for="entry in FIELD_ROLES" :key="entry.role" :value="entry.role">
                  {{ entry.label }}
                </option>
              </select>
            </label>
          </div>
        </template>

        <template v-if="where !== 'off'">
          <label class="field">
            <span class="name">Tags for new cards, with spaces between them</span>
            <input v-model="tags" type="text" spellcheck="false" data-testid="tags" />
          </label>
          <label class="field">
            <span class="name"
              >"Add to my newest card" takes a card made at most this many minutes ago</span
            >
            <input
              v-model.number="page.anki.newestWithinMinutes"
              type="number"
              min="0"
              max="1440"
              inputmode="numeric"
              data-testid="newest-within"
            />
          </label>
        </template>
      </section>

      <section class="group" aria-labelledby="media-title">
        <h2 id="media-title">Pictures and sound</h2>
        <div class="pair">
          <label class="field">
            <span class="name">Picture</span>
            <select v-model="page.media.pictureFormat" data-testid="picture-format">
              <option value="jpeg">JPEG</option>
              <option value="webp">WebP, smaller</option>
              <option value="avif">AVIF, smallest</option>
            </select>
          </label>
          <label class="field">
            <span class="name">Width</span>
            <select v-model.number="page.media.pictureWidth" data-testid="picture-width">
              <option v-for="width in PICTURE_WIDTHS" :key="width" :value="width">
                {{ width }} pixels
              </option>
            </select>
          </label>
        </div>
        <label v-if="page.media.pictureFormat !== 'jpeg'" class="check">
          <input v-model="page.media.moving" type="checkbox" data-testid="moving" />
          A moving picture over the chosen lines, up to 15 seconds
        </label>

        <div class="pair">
          <label class="field">
            <span class="name">Sound</span>
            <select v-model="page.media.soundFormat" data-testid="sound-format">
              <option value="mp3">MP3</option>
              <option value="opus">Opus, smaller</option>
            </select>
          </label>
          <label class="field">
            <span class="name">Start earlier, milliseconds</span>
            <input
              v-model.number="page.media.padBeforeMs"
              type="number"
              min="0"
              max="2000"
              step="50"
              inputmode="numeric"
              data-testid="pad-before"
            />
          </label>
          <label class="field">
            <span class="name">End later, milliseconds</span>
            <input
              v-model.number="page.media.padAfterMs"
              type="number"
              min="0"
              max="2000"
              step="50"
              inputmode="numeric"
              data-testid="pad-after"
            />
          </label>
        </div>
        <label class="check">
          <input v-model="page.media.normalize" type="checkbox" data-testid="normalize" />
          Even out the loudness
        </label>
      </section>

      <DictionarySettings v-model:look-up="page.dictionary.lookUp" :api="api" />

      <section v-if="current.Everyone !== undefined" class="group" aria-labelledby="everyone-title">
        <h2 id="everyone-title">For everyone on this server</h2>
        <label class="check">
          <input
            type="checkbox"
            :checked="current.Everyone.AnkiThroughServer"
            data-testid="everyone-server"
            @change="setEveryone"
          />
          Let users who are not administrators send cards through this server
        </label>
        <p class="muted small">
          The server then calls whatever AnkiConnect address they type, also addresses inside your
          own network. Administrators always may.
        </p>
      </section>

      <div class="actions">
        <button class="button primary" type="submit" :disabled="saving" data-testid="save">
          {{ saving ? 'Saving…' : 'Save' }}
        </button>
        <button
          class="button"
          type="button"
          :disabled="saving"
          data-testid="restore-defaults"
          @click="restoreDefaults"
        >
          Restore defaults
        </button>
        <p
          v-if="message !== null"
          class="small"
          :class="message.error ? 'error' : 'ok'"
          :role="message.error ? 'alert' : 'status'"
          data-testid="settings-message"
        >
          {{ message.text }}
        </p>
      </div>
    </form>
  </div>
</template>

<style scoped>
.settings {
  width: 100%;
  max-width: 720px;
  margin: 0 auto;
  padding: 0 16px 48px;
}

.head {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 16px 0;
}

.head .button {
  padding: 0 14px 0 8px;
  background: transparent;
}

h1 {
  font-size: 22px;
  font-weight: 600;
  color: var(--text-strong);
}

h2 {
  font-size: 17px;
  font-weight: 600;
  color: var(--text-strong);
}

.form {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.group {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 20px 16px;
  border-radius: var(--radius);
  background: var(--surface);
}

.choices {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: 8px;
  margin: 0;
  padding: 0;
  border: 0;
}

.choice {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  cursor: pointer;
}

.choice.chosen {
  border-color: var(--accent);
}

.choice > span {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.choice input {
  margin-top: 3px;
  accent-color: var(--accent);
}

.choice-title {
  font-weight: 600;
  color: var(--text-strong);
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

input[type='url'],
input[type='text'],
input[type='password'],
input[type='number'],
select {
  width: 100%;
  min-height: 44px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--bg);
  color: var(--text-strong);
  font: inherit;
}

input:focus-visible,
select:focus-visible {
  outline-offset: 0;
  border-color: var(--accent);
}

.pair {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 12px;
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

.fields {
  display: grid;
  /* The field's name as wide as the longest, and its choice, with an example, the rest. */
  grid-template-columns: minmax(0, max-content) minmax(0, 1fr);
  align-items: center;
  gap: 8px 16px;
}

.fields > .name {
  grid-column: 1 / -1;
}

.field-row {
  display: contents;
}

/* On a phone the choice gets the whole width under its field's name. */
@media (max-width: 520px) {
  .fields {
    grid-template-columns: minmax(0, 1fr);
    gap: 4px;
  }

  .field-name {
    margin-top: 8px;
  }
}

.field-name {
  overflow: hidden;
  color: var(--text-strong);
  text-overflow: ellipsis;
}

.row,
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 16px;
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

.code {
  display: block;
  margin-top: 6px;
  padding: 8px;
  border-radius: var(--radius);
  background: var(--bg);
  color: var(--text-strong);
  font-size: 13px;
  overflow-wrap: anywhere;
  user-select: all;
}

.link {
  padding: 0;
  border: 0;
  background: none;
  color: var(--text);
  text-decoration: underline;
  cursor: pointer;
}
</style>
