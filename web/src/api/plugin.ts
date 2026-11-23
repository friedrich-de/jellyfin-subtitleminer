import { apiUrl } from './addresses';
import { type Fetch, getJson, send, tokenHeader } from './http';
import type { Subtitles } from './types';

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
      file(
        item(
          itemId,
          `Picture?${query({
            at: Math.round(options.atMs),
            end: options.endMs === undefined ? undefined : Math.round(options.endMs),
            format: options.format,
            width: options.width,
            quality: options.quality,
            source,
          })}`,
        ),
        signal,
      ),

    sound: (itemId, source, options, signal) =>
      file(
        item(
          itemId,
          `Audio?${query({
            start: Math.round(options.startMs),
            end: Math.round(options.endMs),
            track: options.track,
            format: options.format,
            bitrate: options.bitrate,
            normalize: options.normalize,
            source,
          })}`,
        ),
        signal,
      ),
  };
}
