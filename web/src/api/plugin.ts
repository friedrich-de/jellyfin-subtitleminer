import { apiUrl } from './addresses';
import { type Fetch, getJson, send, tokenHeader } from './http';
import type { SettingsChange, SettingsView, StoredMedia, Subtitles } from './types';

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
  };
}
