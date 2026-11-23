import { type APIRequestContext, expect, test as base } from '@playwright/test';

import type { Account, ServerState } from './server';
import { readState } from './state';

/** A login made through Jellyfin's own sign-in call. */
export interface Login {
  token: string;
  userId: string;
  serverId: string;
}

/** The header Jellyfin 12 reads a login from. */
export function tokenHeader(token: string): Record<string, string> {
  return { Authorization: `MediaBrowser Token="${token}"` };
}

let devices = 0;

/** Signs in the way any Jellyfin client does. Every call is a device of its own. */
export async function signIn(request: APIRequestContext, account: Account): Promise<Login> {
  devices += 1;
  const device = `e2e-${process.pid}-${devices}`;
  const response = await request.post('/Users/AuthenticateByName', {
    headers: {
      Authorization: `MediaBrowser Client="subtitle-miner-e2e", Device="test", DeviceId="${device}", Version="1"`,
    },
    data: { Username: account.name, Pw: account.password },
  });
  expect(response.status(), await response.text()).toBe(200);
  const body = (await response.json()) as {
    AccessToken: string;
    ServerId: string;
    User: { Id: string };
  };
  return { token: body.AccessToken, userId: body.User.Id, serverId: body.ServerId };
}

interface Fixtures {
  /** The running test server. */
  jellyfin: ServerState;
  /** What the plugin's page wrote to the browser's console as an error, checked after every test. */
  pageErrors: string[];
}

export const test = base.extend<Fixtures>({
  // eslint-disable-next-line no-empty-pattern
  jellyfin: async ({}, use) => {
    await use(readState());
  },

  // eslint-disable-next-line no-empty-pattern
  baseURL: async ({}, use) => {
    await use(readState().baseUrl);
  },

  pageErrors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      const onOurPage = (): boolean => new URL(page.url()).pathname.startsWith('/SubtitleMiner/');
      page.on('pageerror', (error) => {
        if (onOurPage()) errors.push(`uncaught: ${error.message}`);
      });
      page.on('console', (message) => {
        // A refused request is the browser's own line, and the tests that expect one assert on
        // what the page shows. Everything else, a refused script or style above all, is a fault.
        if (message.type() !== 'error' || !onOurPage()) return;
        if (message.text().startsWith('Failed to load resource:')) return;
        errors.push(message.text());
      });
      await use(errors);
      expect(errors, 'errors in the console of the page').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
