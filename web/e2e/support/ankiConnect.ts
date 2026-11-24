import { once } from 'node:events';
import { createServer, type IncomingMessage } from 'node:http';
import type { AddressInfo } from 'node:net';

import { type Collection, createCollection } from '../../src/testing/ankiCollection';

/** A call as it reached the stand-in. */
export interface SeenCall {
  action: string;
  /** The key in the body, if any. */
  key: unknown;
  /** The page's origin when a browser made the call; none when the Jellyfin server did. */
  origin: string | undefined;
}

export interface StandInOptions {
  /** The key it asks for. None by default. */
  key?: string;
  /** The origins it lets in, as AnkiConnect's webCorsOriginList. */
  origins?: string[];
}

/**
 * An AnkiConnect for the tests: AnkiConnect version 6 over HTTP, with its key check and its CORS
 * answers, in front of a small collection the tests look into. It listens on 127.0.0.1 only.
 */
export interface StandInAnki {
  /** Its address, such as http://127.0.0.1:41234. */
  url: string;
  collection: Collection;
  seen: SeenCall[];
  /** Lets a browser page from this origin in. */
  allow(origin: string): void;
  /** Answers the next call that is not requestPermission as a server AnkiConnect does while syncing. */
  busyOnce(): void;
  /** Holds the answer to the next call of `action` until the function it gives back is called. */
  hold(action: string): () => void;
  /** The file Anki was given under that name. */
  media(name: string): Buffer;
  stop(): Promise<void>;
}

async function bodyOf(request: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) chunks.push(chunk as Buffer);
  return Buffer.concat(chunks).toString('utf8');
}

export async function startAnkiConnect(options: StandInOptions = {}): Promise<StandInAnki> {
  const collection = createCollection(options.key === undefined ? {} : { key: options.key });
  const origins = new Set(options.origins ?? []);
  const seen: SeenCall[] = [];
  let busy = false;
  let held: { action: string; until: Promise<void> } | undefined;
  let stopped: Promise<void> | undefined;

  const server = createServer((request, response) => {
    void (async () => {
      const origin = request.headers.origin;
      // As AnkiConnect: a call without an origin is let in, one from a browser only if listed.
      const allowed = origin === undefined || origins.has(origin);
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (allowed) {
        headers['Access-Control-Allow-Origin'] = origin ?? 'http://localhost';
        headers['Access-Control-Allow-Headers'] = '*';
      }

      if (request.method === 'OPTIONS') {
        response.writeHead(200, headers).end();
        return;
      }

      const text = await bodyOf(request);
      let body: unknown;
      try {
        body = JSON.parse(text) as unknown;
      } catch {
        response.writeHead(200, headers).end(JSON.stringify({ result: null, error: 'bad json' }));
        return;
      }

      const call = (body ?? {}) as { action?: unknown; key?: unknown };
      seen.push({ action: String(call.action), key: call.key, origin });
      if (!allowed) {
        response.writeHead(200, headers).end('null');
        return;
      }

      if (busy && call.action !== 'requestPermission') {
        busy = false;
        const error = 'The collection is busy (a sync is running); try again shortly.';
        response.writeHead(200, headers).end(JSON.stringify({ result: null, error }));
        return;
      }

      const waiting = held;
      if (waiting !== undefined && waiting.action === call.action) {
        held = undefined;
        await waiting.until;
      }

      response.writeHead(200, headers).end(JSON.stringify(collection.answer(body)));
    })().catch((error: unknown) => {
      response.writeHead(500).end(String(error));
    });
  });

  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address() as AddressInfo;

  return {
    url: `http://127.0.0.1:${port}`,
    collection,
    seen,
    allow: (origin) => origins.add(origin),
    busyOnce: () => {
      busy = true;
    },
    hold(action) {
      let release!: () => void;
      held = { action, until: new Promise<void>((resolve) => (release = resolve)) };
      return release;
    },
    media(name) {
      const data = collection.media.get(name);
      if (data === undefined) throw new Error(`Anki has no file named ${name}.`);
      return Buffer.from(data, 'base64');
    },
    stop() {
      stopped ??= new Promise<void>((resolve) => {
        server.close(() => resolve());
        server.closeAllConnections();
      });
      return stopped;
    },
  };
}
