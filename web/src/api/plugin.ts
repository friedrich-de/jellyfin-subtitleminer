import { apiUrl } from './addresses';
import { type Fetch, getJson, send, tokenHeader } from './http';
import type {
  DictionariesView,
  Lookup,
  SettingsChange,
  SettingsView,
  StoredMedia,
  Subtitles,
  WordAudioSaved,
} from './types';

export type PictureFormat = 'jpeg' | 'webp' | 'avif';
export type SoundFormat = 'mp3' | 'opus';

export interface PictureOptions {
  /** The moment of a still, or where a moving picture starts. Milliseconds. */
  atMs: number;
  /** Where a moving picture ends. */
  endMs?: number;
  format?: PictureFormat;
  width?: number;
  quality?: number;
}

export interface SoundOptions {
  startMs: number;
  endMs: number;
  /** The audio track's key. The video's first when left out. */
  track?: string;
  format?: SoundFormat;
  bitrate?: number;
  normalize?: boolean;
}

/** What the page asks the plugin for, with one login. */
export interface PluginApi {
  /** Opens the stream of news about the user's players. The caller reads and closes it. */
  events(signal: AbortSignal): Promise<Response>;
  subtitles(
    itemId: string,
    source: string,
    track: string,
    signal?: AbortSignal,
  ): Promise<Subtitles>;
  picture(
    itemId: string,
    source: string,
    options: PictureOptions,
    signal?: AbortSignal,
  ): Promise<Blob>;
  sound(itemId: string, source: string, options: SoundOptions, signal?: AbortSignal): Promise<Blob>;
  settings(): Promise<SettingsView>;
  saveSettings(change: SettingsChange): Promise<SettingsView>;
  /** Only an administrator may. */
  saveEveryone(ankiThroughServer: boolean): Promise<SettingsView>;
  /** One call to the user's AnkiConnect on a server. The answer is AnkiConnect's own. */
  anki(action: string, params?: unknown): Promise<Response>;
  /** Cuts a picture and puts it into the user's Anki on a server. */
  pictureToAnki(itemId: string, source: string, options: PictureOptions): Promise<StoredMedia>;
  /** Cuts sound and puts it into the user's Anki on a server. */
  soundToAnki(itemId: string, source: string, options: SoundOptions): Promise<StoredMedia>;
  /** The server's dictionaries, and the import that runs. */
  dictionaries(): Promise<DictionariesView>;
  /** The words a text starts with, in the server's dictionaries. */
  lookUp(text: string, signal?: AbortSignal): Promise<Lookup>;
  /** A picture of a dictionary's definitions. */
  dictionaryPicture(id: string, path: string): Promise<Blob>;
  /** Only an administrator may: imports a dictionary's zip. */
  uploadDictionary(file: Blob, name: string): Promise<DictionariesView>;
  /** Only an administrator may: the server downloads a dictionary's zip and imports it. */
  downloadDictionary(url: string): Promise<DictionariesView>;
  /** Only an administrator may. */
  stopDictionaryImport(): Promise<DictionariesView>;
  /** Only an administrator may: every dictionary's id once, the first first. */
  arrangeDictionaries(order: string[], disabled: string[]): Promise<DictionariesView>;
  /** Only an administrator may. */
  removeDictionary(id: string): Promise<DictionariesView>;
  /** How a word sounds, from the audio source an administrator set. */
  wordAudio(term: string, reading: string, signal?: AbortSignal): Promise<Blob>;
  /** Puts how a word sounds into the user's Anki on a server. */
  wordAudioToAnki(term: string, reading: string): Promise<StoredMedia>;
  /** Only an administrator may: sets the audio source for words, or takes it away with "". */
  saveWordAudio(address: string): Promise<WordAudioSaved>;
}

function query(values: Record<string, string | number | boolean | undefined>): string {
  const pairs = Object.entries(values).filter(
    (pair): pair is [string, string | number | boolean] => pair[1] !== undefined,
  );
  return new URLSearchParams(pairs.map(([name, value]) => [name, String(value)])).toString();
}

export function createPluginApi(fetchFn: Fetch, base: string, token: string): PluginApi {
  const item = (itemId: string, path: string): string =>
    apiUrl(base, `Items/${encodeURIComponent(itemId)}/${path}`);
  const dictionaries = (path: string): string => apiUrl(base, `Dictionaries${path}`);

  async function sendJson<T>(method: string, url: string, body?: unknown): Promise<T> {
    const response = await send(fetchFn, url, {
      method,
      headers: {
        Authorization: tokenHeader(token),
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    return (await response.json()) as T;
  }

  const pictureQuery = (source: string, options: PictureOptions): string =>
    query({
      at: Math.round(options.atMs),
      end: options.endMs === undefined ? undefined : Math.round(options.endMs),
      format: options.format,
      width: options.width,
      quality: options.quality,
      source,
    });

  const soundQuery = (source: string, options: SoundOptions): string =>
    query({
      start: Math.round(options.startMs),
      end: Math.round(options.endMs),
      track: options.track,
      format: options.format,
      bitrate: options.bitrate,
      normalize: options.normalize,
      source,
    });

  async function file(url: string, signal: AbortSignal | undefined): Promise<Blob> {
    const response = await send(fetchFn, url, {
      headers: { Authorization: tokenHeader(token) },
      ...(signal === undefined ? {} : { signal }),
    });
    return response.blob();
  }

  return {
    events: (signal) =>
      fetchFn(apiUrl(base, 'Events'), {
        headers: { Authorization: tokenHeader(token), Accept: 'text/event-stream' },
        cache: 'no-store',
        signal,
      }),

    subtitles: (itemId, source, track, signal) =>
      getJson<Subtitles>(
        fetchFn,
        item(itemId, `Subtitles?${query({ track, source })}`),
        token,
        signal,
      ),

    picture: (itemId, source, options, signal) =>
      file(item(itemId, `Picture?${pictureQuery(source, options)}`), signal),

    sound: (itemId, source, options, signal) =>
      file(item(itemId, `Audio?${soundQuery(source, options)}`), signal),

    settings: () => getJson<SettingsView>(fetchFn, apiUrl(base, 'Settings'), token),

    saveSettings: (change) => sendJson<SettingsView>('PUT', apiUrl(base, 'Settings'), change),

    saveEveryone: (ankiThroughServer) =>
      sendJson<SettingsView>('PUT', apiUrl(base, 'Settings/Everyone'), {
        AnkiThroughServer: ankiThroughServer,
      }),

    anki: (action, params) =>
      send(fetchFn, apiUrl(base, 'Anki'), {
        method: 'POST',
        headers: {
          Authorization: tokenHeader(token),
          Accept: 'application/json',
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(
          params === undefined ? { Action: action } : { Action: action, Params: params },
        ),
      }),

    pictureToAnki: (itemId, source, options) =>
      sendJson<StoredMedia>('POST', item(itemId, `Picture/Anki?${pictureQuery(source, options)}`)),

    soundToAnki: (itemId, source, options) =>
      sendJson<StoredMedia>('POST', item(itemId, `Audio/Anki?${soundQuery(source, options)}`)),

    dictionaries: () => getJson<DictionariesView>(fetchFn, dictionaries(''), token),

    lookUp: (text, signal) =>
      getJson<Lookup>(fetchFn, dictionaries(`/Lookup?${query({ text })}`), token, signal),

    dictionaryPicture: (id, path) =>
      file(dictionaries(`/${encodeURIComponent(id)}/Media?${query({ path })}`), undefined),

    uploadDictionary: async (zip, name) => {
      const response = await send(fetchFn, dictionaries(`?${query({ name })}`), {
        method: 'POST',
        headers: {
          Authorization: tokenHeader(token),
          Accept: 'application/json',
          'Content-Type': 'application/zip',
        },
        body: zip,
      });
      return (await response.json()) as DictionariesView;
    },

    downloadDictionary: (url) =>
      sendJson<DictionariesView>('POST', dictionaries('/Download'), { Url: url }),

    stopDictionaryImport: () => sendJson<DictionariesView>('DELETE', dictionaries('/Import')),

    arrangeDictionaries: (order, disabled) =>
      sendJson<DictionariesView>('PUT', dictionaries(''), { Order: order, Disabled: disabled }),

    removeDictionary: (id) =>
      sendJson<DictionariesView>('DELETE', dictionaries(`/${encodeURIComponent(id)}`)),

    wordAudio: (term, reading, signal) =>
      file(apiUrl(base, `WordAudio?${query({ term, reading })}`), signal),

    wordAudioToAnki: (term, reading) =>
      sendJson<StoredMedia>('POST', apiUrl(base, `WordAudio/Anki?${query({ term, reading })}`)),

    saveWordAudio: (address) =>
      sendJson<WordAudioSaved>('PUT', apiUrl(base, 'WordAudio'), { Address: address }),
  };
}
