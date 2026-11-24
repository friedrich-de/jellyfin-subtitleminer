import type { APIRequestContext } from '@playwright/test';

import { expect, type Login, signIn, tokenHeader } from './fixtures';
import type { ServerState } from './server';
import { API } from './watching';

/** Changes a user's settings through the plugin, as the page does. */
export async function saveSettings(
  request: APIRequestContext,
  login: Login,
  change: unknown,
): Promise<void> {
  const response = await request.put(`${API}/Settings`, {
    headers: tokenHeader(login.token),
    data: change,
  });
  expect(response.status(), await response.text()).toBe(200);
}

/** Whether users who are not administrators may send cards through the server. */
export async function letEveryoneUseServer(
  request: APIRequestContext,
  admin: Login,
  allowed: boolean,
): Promise<void> {
  const response = await request.put(`${API}/Settings/Everyone`, {
    headers: tokenHeader(admin.token),
    data: { AnkiThroughServer: allowed },
  });
  expect(response.status(), await response.text()).toBe(200);
}

/**
 * Puts both users' Anki settings back to how a new user finds them, and closes the server to
 * users who are not administrators. The settings outlive a test, and the tests run twice.
 */
export async function forgetAnki(request: APIRequestContext, jellyfin: ServerState): Promise<void> {
  const admin = await signIn(request, jellyfin.admin);
  for (const account of [jellyfin.admin, jellyfin.viewer]) {
    const login = account === jellyfin.admin ? admin : await signIn(request, account);
    await saveSettings(request, login, {
      Anki: {
        Where: 'off',
        DeviceAddress: 'http://127.0.0.1:8765',
        DeviceKey: '',
        ServerAddress: '',
        ServerKey: '',
      },
      Page: {},
    });
  }
  await letEveryoneUseServer(request, admin, false);
}
