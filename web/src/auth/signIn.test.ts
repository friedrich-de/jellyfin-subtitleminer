import { describe, expect, it } from 'vitest';

import {
  BASE,
  env,
  fakeServer,
  json,
  LOGIN_URL,
  LOGOUT_URL,
  MemoryStorage,
  problem,
  PUBLIC_INFO_URL,
  SERVER_ID,
  status,
  STATUS_URL,
  webClientLogins,
} from '@/testing/fakes';

import { readOwnSession, WEB_CLIENT_KEY, writeOwnSession } from './credentials';
import { resolveSession, signInWithPassword, signOut } from './signIn';

function storageWith(...tokens: string[]): MemoryStorage {
  const storage = new MemoryStorage();
  storage.setItem(
    WEB_CLIENT_KEY,
    webClientLogins(...tokens.map((token, index) => ({ token, used: tokens.length - index }))),
  );
  return storage;
}

describe('signing in with a login the browser already holds', () => {
  it("uses the web client's login, so there is no second sign-in", async () => {
    const server = fakeServer(json(status()));

    const result = await resolveSession(env(server.fetch, storageWith('web-token')));

    expect(result).toEqual({
      kind: 'signed-in',
      session: { token: 'web-token', source: 'jellyfin', status: status() },
    });
    // First which server this is, asked without a login, then the status with the login to it.
    expect(server.sent.map((request) => request.url)).toEqual([PUBLIC_INFO_URL, STATUS_URL]);
    expect(server.sent[0]?.headers.authorization).toBeUndefined();
    expect(server.sent[1]?.headers.authorization).toBe('MediaBrowser Token="web-token"');
  });

  it('never sends the header Jellyfin 12 stopped reading', async () => {
    const server = fakeServer(json(status()));

    await resolveSession(env(server.fetch, storageWith('web-token')));

    for (const request of server.sent) {
      expect(Object.keys(request.headers)).not.toContain('x-emby-authorization');
      expect(Object.keys(request.headers)).not.toContain('x-emby-token');
    }
  });

  it("never sends the web client's login to another server", async () => {
    const storage = new MemoryStorage();
    storage.setItem(
      WEB_CLIENT_KEY,
      webClientLogins(
        { token: 'elsewhere', used: 300, server: '0c1d2e3f405162738495a6b7c8d9eaf0' },
        // The same server, as Jellyfin writes its id in another place.
        { token: 'here', used: 200, server: '4FE8B1C2-A6D9-4A07-B2C3-5E6F71D08A93' },
      ),
    );
    const server = fakeServer(json(status()));

    const result = await resolveSession(env(server.fetch, storage));

    expect(SERVER_ID).toBe('4fe8b1c2a6d94a07b2c35e6f71d08a93');
    expect(result.kind === 'signed-in' && result.session.token).toBe('here');
    expect(server.sent.map((request) => request.headers.authorization ?? '')).not.toContain(
      'MediaBrowser Token="elsewhere"',
    );
  });

  it('asks for a sign-in when the web client is signed in to other servers only', async () => {
    const storage = new MemoryStorage();
    storage.setItem(WEB_CLIENT_KEY, webClientLogins({ token: 'elsewhere', server: 'other' }));
    const server = fakeServer();

    expect(await resolveSession(env(server.fetch, storage))).toEqual({ kind: 'signed-out' });
    expect(server.sent.map((request) => request.url)).toEqual([PUBLIC_INFO_URL]);
  });

  it("sends the page's own login only to the server it was made at", async () => {
    const storage = new MemoryStorage();
    writeOwnSession(storage, {
      accessToken: 'other-path-token',
      userId: 'u',
      userName: 'mika',
      base: 'https://jellyfin.example/other/SubtitleMiner/',
    });
    const server = fakeServer();

    expect(await resolveSession(env(server.fetch, storage))).toEqual({ kind: 'signed-out' });
    expect(server.sent).toHaveLength(0);
  });

  it("prefers the page's own login and forgets it once the server refuses it", async () => {
    const storage = storageWith('web-token');
    writeOwnSession(storage, { accessToken: 'own-token', userId: 'u', userName: 'mika' });

    const accepted = fakeServer(json(status()));
    const first = await resolveSession(env(accepted.fetch, storage));
    expect(first.kind === 'signed-in' && first.session).toMatchObject({
      token: 'own-token',
      source: 'page',
    });

    const refused = fakeServer(problem(401, 'Unauthorized'), json(status()));
    const second = await resolveSession(env(refused.fetch, storage));
    expect(second.kind === 'signed-in' && second.session).toMatchObject({
      token: 'web-token',
      source: 'jellyfin',
    });
    expect(readOwnSession(storage)).toBeNull();
  });

  it('skips a login that belongs to no user', async () => {
    const server = fakeServer(problem(403, 'Sign in to Jellyfin as a user to use this page.'));

    expect(await resolveSession(env(server.fetch, storageWith('api-key')))).toEqual({
      kind: 'signed-out',
    });
  });

  it('asks for a sign-in when the browser holds no login, without calling the server', async () => {
    const server = fakeServer();

    expect(await resolveSession(env(server.fetch))).toEqual({ kind: 'signed-out' });
    expect(server.sent).toHaveLength(0);
  });

  it('says so when the server cannot be reached, and keeps the logins', async () => {
    const storage = storageWith('web-token');
    writeOwnSession(storage, { accessToken: 'own-token', userId: 'u', userName: 'mika' });
    const server = fakeServer(new TypeError('Failed to fetch'));

    expect(await resolveSession(env(server.fetch, storage))).toEqual({ kind: 'unreachable' });
    expect(readOwnSession(storage)?.accessToken).toBe('own-token');
  });

  it("passes on the server's own words when it fails", async () => {
    const server = fakeServer(problem(500, 'The plugin could not start.'));

    expect(await resolveSession(env(server.fetch, storageWith('web-token')))).toEqual({
      kind: 'failed',
      message: 'The plugin could not start.',
    });
  });
});

describe('signing in with a name and password', () => {
  it('asks Jellyfin with the current header, keeps the login and reads the status with it', async () => {
    const storage = new MemoryStorage();
    const server = fakeServer(
      json({ AccessToken: 'fresh-token', User: { Id: 'user-1', Name: 'mika' } }),
      json(status({ UserName: 'mika' })),
    );

    const result = await signInWithPassword(env(server.fetch, storage), 'mika', 'secret');

    expect(result.kind === 'signed-in' && result.session).toMatchObject({
      token: 'fresh-token',
      source: 'page',
    });
    const [login, statusCall] = server.sent;
    expect(login?.url).toBe(LOGIN_URL);
    expect(login?.method).toBe('POST');
    expect(login?.body).toEqual({ Username: 'mika', Pw: 'secret' });
    expect(login?.headers.authorization).toMatch(
      /^MediaBrowser Client="Subtitle Miner", Device="Chrome on Android", DeviceId="[A-Za-z0-9-]{8,64}", Version="2\.0\.0\.0"$/,
    );
    expect(Object.keys(login?.headers ?? {})).not.toContain('x-emby-authorization');
    expect(statusCall?.url).toBe(STATUS_URL);
    expect(statusCall?.headers.authorization).toBe('MediaBrowser Token="fresh-token"');
    expect(readOwnSession(storage)).toEqual({
      accessToken: 'fresh-token',
      userId: 'user-1',
      userName: 'mika',
      base: BASE,
    });
  });

  it('uses the same device id for every sign-in', async () => {
    const storage = new MemoryStorage();
    const answers = () => [
      json({ AccessToken: 't', User: { Id: 'u', Name: 'a' } }),
      json(status()),
    ];
    const first = fakeServer(...answers());
    const second = fakeServer(...answers());

    await signInWithPassword(env(first.fetch, storage), 'mika', 'secret');
    await signInWithPassword(env(second.fetch, storage), 'mika', 'secret');

    expect(second.sent[0]?.headers.authorization).toBe(first.sent[0]?.headers.authorization);
  });

  it('says that the name or password is not right, and keeps nothing', async () => {
    const storage = new MemoryStorage();
    const server = fakeServer(problem(401, 'Invalid username or password entered.'));

    expect(await signInWithPassword(env(server.fetch, storage), 'mika', 'wrong')).toEqual({
      kind: 'refused',
      message: 'That name or password is not right.',
    });
    expect(readOwnSession(storage)).toBeNull();
    expect(server.sent).toHaveLength(1);
  });

  it('says so when the server cannot be reached', async () => {
    const server = fakeServer(new TypeError('Failed to fetch'));

    expect(await signInWithPassword(env(server.fetch), 'mika', 'secret')).toEqual({
      kind: 'unreachable',
    });
  });

  it('fails plainly when the server answers without a login', async () => {
    const server = fakeServer(json({ User: { Id: 'u' } }));

    expect(await signInWithPassword(env(server.fetch), 'mika', 'secret')).toEqual({
      kind: 'failed',
      message: 'The server gave no login.',
    });
  });
});

describe('signing out', () => {
  it("ends the page's own login on the server and forgets it", async () => {
    const storage = new MemoryStorage();
    writeOwnSession(storage, { accessToken: 'own-token', userId: 'u', userName: 'mika' });
    const server = fakeServer(new Response(null, { status: 204 }));

    await signOut(env(server.fetch, storage), {
      token: 'own-token',
      source: 'page',
      status: status(),
    });

    expect(server.sent).toHaveLength(1);
    expect(server.sent[0]).toMatchObject({ url: LOGOUT_URL, method: 'POST' });
    expect(server.sent[0]?.headers.authorization).toBe('MediaBrowser Token="own-token"');
    expect(readOwnSession(storage)).toBeNull();
  });

  it('forgets the login even when the server cannot be reached', async () => {
    const storage = new MemoryStorage();
    writeOwnSession(storage, { accessToken: 'own-token', userId: 'u', userName: 'mika' });
    const server = fakeServer(new TypeError('Failed to fetch'));

    await signOut(env(server.fetch, storage), {
      token: 'own-token',
      source: 'page',
      status: status(),
    });

    expect(readOwnSession(storage)).toBeNull();
  });

  it("leaves the web client's login alone, because ending it would sign the user out of Jellyfin", async () => {
    const storage = storageWith('web-token');
    const server = fakeServer();

    await signOut(env(server.fetch, storage), {
      token: 'web-token',
      source: 'jellyfin',
      status: status(),
    });

    expect(server.sent).toHaveLength(0);
    expect(storage.getItem(WEB_CLIENT_KEY)).toContain('web-token');
  });
});

describe('addresses', () => {
  it('stay below the Jellyfin address the page was opened at, base path included', async () => {
    const server = fakeServer(json(status()));
    const withQuery = { ...env(server.fetch, storageWith('t')), base: `${BASE}?x=1#/somewhere` };

    await resolveSession(withQuery);

    expect(server.sent.map((request) => request.url)).toEqual([PUBLIC_INFO_URL, STATUS_URL]);
  });
});
