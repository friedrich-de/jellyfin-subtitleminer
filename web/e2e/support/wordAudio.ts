import { once } from 'node:events';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';

import type { APIRequestContext } from '@playwright/test';

import { expect, type Login, tokenHeader } from './fixtures';
import { API } from './watching';

/** The key the stand-in asks for, as the audio sources people use ask for one in the address. */
export const AUDIO_KEY = 'e2e-audio-key';

/**
 * An audio source for words as Yomitan's "Custom URL (JSON)" reads one: /audio/list?term=…&reading=…
 * answers with {"type":"audioSourceList","audioSources":[{name,url}]}, and each url with the sound.
 * It knows the words it is given, and answers 403 without its key. It listens on 127.0.0.1 only.
 */
export interface StandInAudio {
  url: string;
  /** The address an administrator types, key and all. */
  address: string;
  /** The paths and queries asked for, in order. */
  asked: string[];
  stop(): Promise<void>;
}

/** The second sound the stand-in has of a word, under the name "jpod": the first, marked. */
export function jpodOf(sound: Buffer): Buffer {
  return Buffer.concat([sound, Buffer.from('jpod')]);
}

export async function startAudioSource(sounds: Record<string, Buffer>): Promise<StandInAudio> {
  const asked: string[] = [];
  const server = createServer((request, response) => {
    const url = new URL(request.url ?? '/', 'http://127.0.0.1');
    asked.push(url.pathname + url.search);
    if (url.pathname === '/audio/list') {
      if (url.searchParams.get('apiKey') !== AUDIO_KEY) {
        response.writeHead(403, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ status: 403, message: 'Invalid API key' }));
        return;
      }
      const term = url.searchParams.get('term') ?? '';
      const audioSources =
        sounds[term] === undefined
          ? []
          : [
              // The first is gone, as a source's entry sometimes is: the next one is taken.
              { name: 'gone', url: `/audio/gone/${encodeURIComponent(term)}.mp3` },
              { name: 'nhk16', url: `${address()}/audio/nhk16/${encodeURIComponent(term)}.mp3` },
              { name: 'jpod', url: `${address()}/audio/jpod/${encodeURIComponent(term)}.mp3` },
            ];
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({ type: 'audioSourceList', audioSources }));
      return;
    }
    const [, name, sound] = /^\/audio\/(nhk16|jpod)\/(.+)\.mp3$/.exec(url.pathname) ?? [];
    const found = sound === undefined ? undefined : sounds[decodeURIComponent(sound)];
    const content = found !== undefined && name === 'jpod' ? jpodOf(found) : found;
    if (content === undefined) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { 'Content-Type': 'audio/mpeg', 'Content-Length': content.length });
    response.end(content);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address() as AddressInfo;
  const address = (): string => `http://127.0.0.1:${port}`;
  return {
    url: address(),
    address: `${address()}/audio/list?term={term}&reading={reading}&apiKey=${AUDIO_KEY}`,
    asked,
    stop: () =>
      new Promise((resolve) => {
        server.close(() => resolve());
      }),
  };
}

/** Sets the server's audio source for words, as an administrator does in the settings. */
export async function setWordAudio(
  request: APIRequestContext,
  admin: Login,
  address: string,
): Promise<{ WordAudio: { Set: boolean; Host?: string }; Tried: string }> {
  const response = await request.put(`${API}/WordAudio`, {
    headers: tokenHeader(admin.token),
    data: { Address: address },
  });
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json()) as { WordAudio: { Set: boolean; Host?: string }; Tried: string };
}

/** A real sound to stand for a word's: a second of the test video's hum, cut by the plugin. */
export async function aSound(
  request: APIRequestContext,
  login: Login,
  itemId: string,
): Promise<Buffer> {
  const response = await request.get(`${API}/Items/${itemId}/Audio?start=0&end=1000`, {
    headers: tokenHeader(login.token),
  });
  expect(response.status(), await response.text()).toBe(200);
  return response.body();
}
