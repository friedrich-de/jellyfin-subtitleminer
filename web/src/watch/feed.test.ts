import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { eventStream, type EventStream } from '@/testing/fakes';

import { createWatchFeed, type FeedStatus, type Received, RETRY_DELAYS, SILENCE_MS } from './feed';
import { sampleWatch } from './samples';

/** A server whose stream of news the test opens, answers and breaks. */
function fakeEvents() {
  const opened: { stream: EventStream; signal: AbortSignal }[] = [];
  const answers: (Response | Error | 'stream')[] = [];
  return {
    opened,
    answers,
    api: {
      events(signal: AbortSignal): Promise<Response> {
        const next = answers.shift() ?? 'stream';
        if (next instanceof Error) return Promise.reject(next);
        if (next instanceof Response) return Promise.resolve(next);
        const stream = eventStream();
        signal.addEventListener('abort', () =>
          stream.fail(new DOMException('Aborted', 'AbortError')),
        );
        opened.push({ stream, signal });
        return Promise.resolve(stream.response);
      },
    },
    get last(): EventStream {
      return opened.at(-1)!.stream;
    },
  };
}

function start(server: ReturnType<typeof fakeEvents>) {
  const lists: Received[] = [];
  const statuses: FeedStatus[] = [];
  const feed = createWatchFeed(
    {
      api: server.api,
      now: () => Date.now(),
      setTimeout: (callback, ms) => window.setTimeout(callback, ms),
      clearTimeout: (handle) => window.clearTimeout(handle),
    },
    { onList: (list) => lists.push(list), onStatus: (status) => statuses.push(status) },
  );
  feed.start();
  return { feed, lists, statuses };
}

describe('the feed of what plays', () => {
  beforeEach(() => {
    vi.useFakeTimers({ now: 1_000_000 });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('passes on every list with the moment it arrived', async () => {
    const server = fakeEvents();
    const { feed, lists, statuses } = start(server);
    await vi.advanceTimersByTimeAsync(0);
    expect(statuses).toEqual(['connecting']);

    server.last.send([sampleWatch()]);
    await vi.advanceTimersByTimeAsync(1_500);
    server.last.write(': beat\n\n');
    server.last.send([]);
    await vi.advanceTimersByTimeAsync(0);

    expect(statuses).toEqual(['connecting', 'live']);
    expect(lists.map((list) => [list.watches.length, list.at])).toEqual([
      [1, 1_000_000],
      [0, 1_001_500],
    ]);
    feed.stop();
  });

  it('opens the stream again when it breaks, waiting longer each time it fails', async () => {
    const server = fakeEvents();
    server.answers.push(new TypeError('Failed to fetch'), new Response('busy', { status: 429 }));
    const { feed, lists, statuses } = start(server);

    // Refused twice: tries again after one, then two seconds.
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0] - 1);
    expect(server.opened).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1 + RETRY_DELAYS[1]);
    expect(server.opened).toHaveLength(1);
    expect(statuses).toEqual(['connecting', 'retrying']);

    // A list puts it back to the shortest wait.
    server.last.send([]);
    await vi.advanceTimersByTimeAsync(0);
    server.last.close();
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);
    expect(server.opened).toHaveLength(2);
    server.last.send([sampleWatch()]);
    await vi.advanceTimersByTimeAsync(0);

    expect(statuses).toEqual(['connecting', 'retrying', 'live', 'retrying', 'live']);
    expect(lists).toHaveLength(2);
    feed.stop();
  });

  it('takes a stream that falls silent for a dead one', async () => {
    const server = fakeEvents();
    const { feed } = start(server);
    await vi.advanceTimersByTimeAsync(0);
    server.last.send([]);

    // Signs of life keep it open.
    for (let i = 0; i < 4; i += 1) {
      await vi.advanceTimersByTimeAsync(15_000);
      server.last.write(': beat\n\n');
    }
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(SILENCE_MS);
    expect(server.opened[0]!.signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);
    expect(server.opened).toHaveLength(2);
    feed.stop();
  });

  it('stops for good when the server no longer takes the login', async () => {
    const server = fakeEvents();
    server.answers.push(new Response(null, { status: 401 }));
    const { statuses } = start(server);

    await vi.advanceTimersByTimeAsync(60_000);

    expect(statuses).toEqual(['connecting', 'signed-out']);
    expect(server.opened).toHaveLength(0);
  });

  it('closes the stream when stopped and opens it at once when started again', async () => {
    const server = fakeEvents();
    const { feed } = start(server);
    await vi.advanceTimersByTimeAsync(0);

    feed.stop();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(server.opened).toHaveLength(1);
    expect(server.opened[0]!.signal.aborted).toBe(true);

    feed.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(2);
    feed.stop();
  });

  it('keeps one connection when stopped and started again before the old one has wound down', async () => {
    const server = fakeEvents();
    const { feed, lists } = start(server);
    await vi.advanceTimersByTimeAsync(0);

    // The page goes out of view and back at once: the old stream is still closing.
    feed.stop();
    feed.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(2);

    // No third stream opens behind the new one's back.
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS.at(-1)!);
    expect(server.opened).toHaveLength(2);
    expect(server.opened[1]!.signal.aborted).toBe(false);
    server.last.send([sampleWatch()]);
    await vi.advanceTimersByTimeAsync(0);
    expect(lists).toHaveLength(1);

    // The new one still has its watch for silence, and stop still reaches it.
    await vi.advanceTimersByTimeAsync(SILENCE_MS);
    expect(server.opened[1]!.signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);
    expect(server.opened).toHaveLength(3);
    feed.stop();
    expect(server.opened[2]!.signal.aborted).toBe(true);
  });

  it('gives up waiting when told the device is back online', async () => {
    const server = fakeEvents();
    server.answers.push(new TypeError('Failed to fetch'), new TypeError('Failed to fetch'));
    const { feed } = start(server);
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);

    // Waiting two seconds now. Back online, it does not wait.
    feed.reconnectNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(1);
    feed.stop();
  });
});
