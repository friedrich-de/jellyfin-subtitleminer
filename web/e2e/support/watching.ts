import { type APIRequestContext, expect } from '@playwright/test';

import { readEvents } from '../../src/api/eventStream';
import type { Watch, WatchList } from '../../src/api/types';
import { tokenHeader } from './fixtures';

export const API = '/SubtitleMiner/Api';

/** Asks the plugin once what a user's players play. */
export async function watchesOf(request: APIRequestContext, token: string): Promise<Watch[]> {
  const response = await request.get(`${API}/Watches`, { headers: tokenHeader(token) });
  expect(response.status(), await response.text()).toBe(200);
  expect(response.headers()['cache-control']).toBe('no-store');
  return ((await response.json()) as WatchList).Watches;
}

/** Waits until the plugin lists a watch that fits, and returns it. */
export async function watchOf(
  request: APIRequestContext,
  token: string,
  fits: (watch: Watch) => boolean,
  what: string,
): Promise<Watch> {
  let found: Watch | undefined;
  await expect
    .poll(
      async () => {
        found = (await watchesOf(request, token)).find(fits);
        return found !== undefined;
      },
      { message: `the plugin lists ${what}`, timeout: 10_000, intervals: [100, 200, 300] },
    )
    .toBe(true);
  return found!;
}

/** The plugin's stream of news for one user, read the way the page reads it. */
export class NewsStream {
  /** Every list the stream sent, oldest first. */
  readonly lists: Watch[][] = [];
  /** How often anything arrived, an event or only a sign of life. */
  arrivals = 0;
  status = 0;
  contentType = '';

  private readonly abort = new AbortController();
  private reading: Promise<void> = Promise.resolve();

  static async open(baseUrl: string, token: string): Promise<NewsStream> {
    const stream = new NewsStream();
    const response = await fetch(`${baseUrl}${API}/Events`, {
      headers: tokenHeader(token),
      signal: stream.abort.signal,
    });
    stream.status = response.status;
    stream.contentType = response.headers.get('content-type') ?? '';
    if (response.ok && response.body !== null) {
      stream.reading = readEvents(
        response.body,
        (event) => {
          if (event.name === 'watches')
            stream.lists.push((JSON.parse(event.data) as WatchList).Watches);
        },
        () => {
          stream.arrivals += 1;
        },
      ).catch(() => {
        // Closed by the test.
      });
    }

    return stream;
  }

  /**
   * Waits for a list that fits among those that arrived after the first `from`, and returns the
   * watch that made it fit. Take `lists.length` before the step that should cause the news.
   */
  async after(
    from: number,
    fits: (watch: Watch) => boolean,
    what: string,
    timeout = 5_000,
  ): Promise<Watch> {
    let found: Watch | undefined;
    await expect
      .poll(
        () => {
          found = this.lists.slice(from).flat().find(fits);
          return found !== undefined;
        },
        { message: `the stream tells of ${what}`, timeout, intervals: [50] },
      )
      .toBe(true);
    return found!;
  }

  async close(): Promise<void> {
    this.abort.abort();
    await this.reading;
  }
}
