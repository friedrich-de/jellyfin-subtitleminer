import { readEvents } from '@/api/eventStream';
import type { PluginApi } from '@/api/plugin';
import type { Watch, WatchList } from '@/api/types';

/** A list of watches and when it arrived, by the page's clock. */
export interface Received {
  watches: Watch[];
  /** performance.now() when the list arrived. */
  at: number;
}

export type FeedStatus =
  /** Connecting for the first time. */
  | 'connecting'
  /** Connected: news arrives as it happens. */
  | 'live'
  /** The connection broke. The feed tries again by itself. */
  | 'retrying'
  /** The server no longer takes the login. The feed has stopped. */
  | 'signed-out';

export interface FeedEnv {
  api: Pick<PluginApi, 'events'>;
  now: () => number;
  setTimeout: (callback: () => void, ms: number) => number;
  clearTimeout: (handle: number) => void;
}

export interface FeedHandlers {
  onList: (list: Received) => void;
  onStatus: (status: FeedStatus) => void;
}

/** How long the feed waits before it tries again, by the number of tries so far. */
export const RETRY_DELAYS = [1_000, 2_000, 5_000, 10_000, 20_000, 30_000] as const;

/**
 * The server writes a sign of life every 15 seconds. When three are missing, the connection is
 * taken to be dead even if the browser has not noticed: a phone that changed networks, a sleeping
 * laptop, a proxy that dropped it.
 */
export const SILENCE_MS = 45_000;

/**
 * Follows the stream of news about the user's players: opens it, reads it, and opens it again when
 * it breaks, waiting longer each time.
 */
export function createWatchFeed(env: FeedEnv, handlers: FeedHandlers) {
  let running = false;
  let tries = 0;
  let abort: AbortController | null = null;
  let retryTimer: number | null = null;
  let silenceTimer: number | null = null;
  let status: FeedStatus | null = null;

  function setStatus(next: FeedStatus): void {
    if (next === status) return;
    status = next;
    handlers.onStatus(next);
  }

  function clearTimers(): void {
    if (retryTimer !== null) env.clearTimeout(retryTimer);
    if (silenceTimer !== null) env.clearTimeout(silenceTimer);
    retryTimer = null;
    silenceTimer = null;
  }

  function heardFromServer(): void {
    if (silenceTimer !== null) env.clearTimeout(silenceTimer);
    silenceTimer = env.setTimeout(() => abort?.abort(), SILENCE_MS);
  }

  function retryLater(): void {
    if (!running) return;
    setStatus('retrying');
    const delay = RETRY_DELAYS[Math.min(tries, RETRY_DELAYS.length - 1)]!;
    tries += 1;
    retryTimer = env.setTimeout(() => {
      retryTimer = null;
      void connect();
    }, delay);
  }

  async function connect(): Promise<void> {
    if (!running) return;
    const mine = new AbortController();
    abort = mine;
    try {
      heardFromServer();
      const response = await env.api.events(mine.signal);
      if (response.status === 401 || response.status === 403) {
        running = false;
        clearTimers();
        setStatus('signed-out');
        return;
      }

      if (!response.ok || response.body === null) throw new Error(`answered ${response.status}`);
      await readEvents(
        response.body,
        (event) => {
          if (event.name !== 'watches') return;
          const list = JSON.parse(event.data) as WatchList;
          tries = 0;
          setStatus('live');
          handlers.onList({ watches: list.Watches, at: env.now() });
        },
        heardFromServer,
      );
    } catch {
      // Broken, refused, unreadable or called off: the same for the page. It tries again.
    }

    // The stream ended, broke or fell silent. Unless the page stopped it, it is opened again.
    if (abort === mine) abort = null;
    if (silenceTimer !== null) env.clearTimeout(silenceTimer);
    silenceTimer = null;
    if (running) retryLater();
  }

  return {
    /** Starts following. Calling it again while it runs does nothing. */
    start(): void {
      if (running) return;
      running = true;
      tries = 0;
      setStatus('connecting');
      void connect();
    },

    /** Stops following and closes the connection. */
    stop(): void {
      running = false;
      clearTimers();
      abort?.abort();
      abort = null;
    },

    /** Gives up waiting and connects at once, after the device woke or came back online. */
    reconnectNow(): void {
      if (!running || abort !== null) return;
      clearTimers();
      void connect();
    },
  };
}

export type WatchFeed = ReturnType<typeof createWatchFeed>;
