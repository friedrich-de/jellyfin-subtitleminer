import { type Fetch, HttpError, UnreachableError } from '@/api/http';
import type { PictureOptions, PluginApi, SoundOptions } from '@/api/plugin';
import type { AnkiWhere } from '@/api/types';

/** The version of AnkiConnect's interface the page speaks. */
export const ANKI_VERSION = 6;

/** Something went wrong on the way to Anki or in it, in words for the user. */
export class AnkiError extends Error {
  constructor(
    message: string,
    /** Whether Anki could not be reached at all, as opposed to saying no. */
    readonly unreachable = false,
  ) {
    super(message);
    this.name = 'AnkiError';
  }
}

/** A picture or a piece of sound to cut from a video for a card. */
export type MediaCut =
  | { kind: 'picture'; itemId: string; source: string; options: PictureOptions }
  | { kind: 'sound'; itemId: string; source: string; options: SoundOptions };

/** The user's Anki, wherever its AnkiConnect is. */
export interface AnkiConnect {
  readonly where: Exclude<AnkiWhere, 'off'>;
  invoke<T>(action: string, params?: Record<string, unknown>): Promise<T>;
  /** Cuts a picture or sound and puts it into Anki's media. Returns the name it is stored under. */
  store(cut: MediaCut): Promise<string>;
}

function explain(error: string, where: AnkiConnect['where']): string {
  if (/api key/i.test(error)) {
    return where === 'device'
      ? 'The AnkiConnect on this device wants a key. Enter it in the settings.'
      : 'The AnkiConnect on your server did not take the key. Check it in the settings.';
  }
  return `Anki said: ${error}`;
}

/** Reads AnkiConnect's answer: the result, or its error in words. */
async function resultOf<T>(response: Response, where: AnkiConnect['where']): Promise<T> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }
  if (body === null || typeof body !== 'object' || !('result' in body) || !('error' in body)) {
    throw new AnkiError(
      'What answers at this address is not AnkiConnect. Check it in the settings.',
    );
  }
  const { result, error } = body as { result: T; error: unknown };
  if (typeof error === 'string' && error.length > 0) throw new AnkiError(explain(error, where));
  return result;
}

/** A short mark of how a file was cut, so the same cut gets the same name. FNV-1a, 32 bits. */
export function markOf(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16).padStart(8, '0');
}

/** The name a file goes into Anki under when the page puts it there itself. */
export function mediaName(cut: MediaCut, type: string): string {
  const at = cut.kind === 'picture' ? cut.options.atMs : cut.options.startMs;
  const extension =
    { 'image/jpeg': 'jpg', 'image/webp': 'webp', 'image/avif': 'avif', 'audio/mpeg': 'mp3' }[
      type
    ] ?? (type.startsWith('audio/') ? 'ogg' : 'bin');
  const mark = markOf(JSON.stringify([cut.kind, cut.itemId, cut.source, cut.options]));
  return `subtitle-miner_${cut.itemId.replace(/-/g, '').slice(0, 8)}_${Math.round(at)}_${mark}.${extension}`;
}

async function base64Of(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

/**
 * An AnkiConnect on the user's own device, which the browser calls itself. AnkiConnect lets the
 * page in only when the Jellyfin address is in its webCorsOriginList.
 */
export function deviceAnki(
  fetchFn: Fetch,
  address: string,
  key: string,
  api: Pick<PluginApi, 'picture' | 'sound'>,
): AnkiConnect {
  async function invoke<T>(action: string, params?: Record<string, unknown>): Promise<T> {
    let response: Response;
    try {
      response = await fetchFn(address, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          action,
          version: ANKI_VERSION,
          ...(key === '' ? {} : { key }),
          ...(params === undefined ? {} : { params }),
        }),
      });
    } catch {
      throw new AnkiError(
        `Anki could not be reached at ${address}. Is Anki open, with AnkiConnect letting this page in?`,
        true,
      );
    }
    if (!response.ok) {
      throw new AnkiError(`The AnkiConnect at ${address} answered ${response.status}.`);
    }
    return resultOf<T>(response, 'device');
  }

  return {
    where: 'device',
    invoke,
    async store(cut) {
      const blob =
        cut.kind === 'picture'
          ? await api.picture(cut.itemId, cut.source, cut.options)
          : await api.sound(cut.itemId, cut.source, cut.options);
      const filename = mediaName(cut, blob.type);
      const stored = await invoke<unknown>('storeMediaFile', {
        filename,
        data: await base64Of(blob),
      });
      return typeof stored === 'string' && stored !== '' ? stored : filename;
    },
  };
}

function serverError(error: unknown): AnkiError {
  if (error instanceof AnkiError) return error;
  if (error instanceof HttpError) return new AnkiError(error.message, error.status >= 500);
  if (error instanceof UnreachableError) return new AnkiError(error.message, true);
  return new AnkiError(error instanceof Error ? error.message : String(error));
}

/** An AnkiConnect on a server, which the Jellyfin server calls for the page with the key. */
export function serverAnki(
  api: Pick<PluginApi, 'anki' | 'pictureToAnki' | 'soundToAnki'>,
): AnkiConnect {
  return {
    where: 'server',
    async invoke<T>(action: string, params?: Record<string, unknown>): Promise<T> {
      try {
        return await resultOf<T>(await api.anki(action, params), 'server');
      } catch (error) {
        throw serverError(error);
      }
    },
    async store(cut) {
      try {
        const stored =
          cut.kind === 'picture'
            ? await api.pictureToAnki(cut.itemId, cut.source, cut.options)
            : await api.soundToAnki(cut.itemId, cut.source, cut.options);
        return stored.FileName;
      } catch (error) {
        throw serverError(error);
      }
    },
  };
}
