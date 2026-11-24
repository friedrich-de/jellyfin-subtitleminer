import { describe, expect, it } from 'vitest';

import { BlockedStorage, MemoryStorage, SERVER_ID, webClientLogins } from '@/testing/fakes';

import {
  clearOwnSession,
  clientHeader,
  DEVICE_KEY,
  deviceId,
  deviceName,
  randomId,
  readOwnSession,
  readWebClientTokens,
  SESSION_KEY,
  WEB_CLIENT_KEY,
  writeOwnSession,
} from './credentials';

describe("the web client's logins", () => {
  it('are read from where Jellyfin keeps them, the one used last first', () => {
    const storage = new MemoryStorage();
    storage.setItem(
      WEB_CLIENT_KEY,
      webClientLogins(
        { token: 'old', used: 100 },
        { token: 'new', used: 300 },
        { token: 'mid', used: 200 },
      ),
    );

    expect(readWebClientTokens(storage)).toEqual(['new', 'mid', 'old']);
  });

  it('skip servers without a login and list a login once', () => {
    const storage = new MemoryStorage();
    storage.setItem(
      WEB_CLIENT_KEY,
      JSON.stringify({
        Servers: [
          { Id: 'a', AccessToken: 'one', DateLastAccessed: 2 },
          { Id: 'b' },
          { Id: 'c', AccessToken: '' },
          { Id: 'd', AccessToken: null },
          null,
          'text',
          { Id: 'e', AccessToken: 'one', DateLastAccessed: 1 },
        ],
      }),
    );

    expect(readWebClientTokens(storage)).toEqual(['one']);
  });

  it.each([
    ['nothing stored', null],
    ['broken JSON', '{"Servers":['],
    ['a number', '5'],
    ['null', 'null'],
    ['no list of servers', '{"Servers":{"AccessToken":"x"}}'],
    ['an empty list', '{"Servers":[]}'],
  ])('are none with %s', (_, stored) => {
    const storage = new MemoryStorage();
    if (stored !== null) storage.setItem(WEB_CLIENT_KEY, stored);

    expect(readWebClientTokens(storage)).toEqual([]);
  });

  it('are none when the browser refuses access to its storage', () => {
    expect(readWebClientTokens(new BlockedStorage())).toEqual([]);
  });

  it("are only this server's when the server is named, however its id is written", () => {
    const storage = new MemoryStorage();
    storage.setItem(
      WEB_CLIENT_KEY,
      JSON.stringify({
        Servers: [
          { Id: 'another-server', AccessToken: 'theirs', DateLastAccessed: 3 },
          { AccessToken: 'nameless', DateLastAccessed: 2 },
          { Id: SERVER_ID.toUpperCase(), AccessToken: 'ours', DateLastAccessed: 1 },
        ],
      }),
    );

    expect(readWebClientTokens(storage, '4fe8b1c2-a6d9-4a07-b2c3-5e6f71d08a93')).toEqual(['ours']);
    expect(readWebClientTokens(storage, 'nobody')).toEqual([]);
    expect(readWebClientTokens(storage)).toEqual(['theirs', 'nameless', 'ours']);
  });
});

describe("the page's own login", () => {
  it('is kept and read back', () => {
    const storage = new MemoryStorage();
    writeOwnSession(storage, { accessToken: 'tok', userId: 'u1', userName: 'mika' });

    expect(readOwnSession(storage)).toEqual({ accessToken: 'tok', userId: 'u1', userName: 'mika' });
    expect(storage.getItem(WEB_CLIENT_KEY)).toBeNull();
  });

  it('is gone after it is cleared', () => {
    const storage = new MemoryStorage();
    writeOwnSession(storage, { accessToken: 'tok', userId: 'u1', userName: 'mika' });
    clearOwnSession(storage);

    expect(readOwnSession(storage)).toBeNull();
  });

  it.each(['{', '[]', '{"accessToken":""}', '{"accessToken":5}', '"text"'])(
    'is none when %s is stored',
    (stored) => {
      const storage = new MemoryStorage();
      storage.setItem(SESSION_KEY, stored);

      expect(readOwnSession(storage)).toBeNull();
    },
  );

  it('survives a browser that refuses access to its storage', () => {
    const storage = new BlockedStorage();

    expect(() =>
      writeOwnSession(storage, { accessToken: 't', userId: '', userName: '' }),
    ).not.toThrow();
    expect(() => clearOwnSession(storage)).not.toThrow();
    expect(readOwnSession(storage)).toBeNull();
  });
});

describe('the device', () => {
  it('gets an id once and keeps it, so Jellyfin lists one device and not one for each login', () => {
    const storage = new MemoryStorage();
    let made = 0;
    const make = () => `device-${++made}-abcdef`;

    expect(deviceId(storage, make)).toBe('device-1-abcdef');
    expect(deviceId(storage, make)).toBe('device-1-abcdef');
    expect(storage.getItem(DEVICE_KEY)).toBe('device-1-abcdef');
  });

  it('gets a new id when the stored one is not an id', () => {
    const storage = new MemoryStorage();
    storage.setItem(DEVICE_KEY, 'x", Token="stolen');

    expect(deviceId(storage, () => 'fresh-id-123')).toBe('fresh-id-123');
  });

  it('still gets an id when storage is refused', () => {
    expect(deviceId(new BlockedStorage(), () => 'only-now-123')).toBe('only-now-123');
  });

  it('gets an id at an address the browser does not trust, where randomUUID is missing', () => {
    // http://192.168.1.10:8096 is no secure context: crypto.randomUUID is not there, getRandomValues is.
    const insecure = { getRandomValues: crypto.getRandomValues.bind(crypto) };
    const ids = new Set(Array.from({ length: 50 }, () => randomId(insecure)));

    expect(ids.size).toBe(50);
    for (const id of ids) {
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    }
    expect(
      randomId({ getRandomValues: insecure.getRandomValues, randomUUID: () => 'a-b-c-d-e' }),
    ).toBe('a-b-c-d-e');
  });

  it.each([
    [
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36',
      'Chrome on Windows',
    ],
    [
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0',
      'Edge on Windows',
    ],
    [
      'Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Mobile/15E148 Safari/604.1',
      'Safari on iPhone',
    ],
    [
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Safari/605.1.15',
      'Safari on Mac',
    ],
    ['Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0', 'Firefox on Linux'],
    [
      'Mozilla/5.0 (Linux; Android 16; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36',
      'Chrome on Android',
    ],
    ['curl/8.5.0', 'Browser'],
  ])('is named after its browser: %s', (userAgent, expected) => {
    expect(deviceName(userAgent)).toBe(expected);
  });

  it('introduces itself in the header Jellyfin 12 reads, with nothing that could break it apart', () => {
    expect(
      clientHeader({ device: 'Chrome on Android', deviceId: 'abc-123', version: '2.0.0.0' }),
    ).toBe(
      'MediaBrowser Client="Subtitle Miner", Device="Chrome on Android", DeviceId="abc-123", Version="2.0.0.0"',
    );
    expect(clientHeader({ device: 'a", Token="x', deviceId: "b'c,d\\", version: '1' })).toBe(
      'MediaBrowser Client="Subtitle Miner", Device="a Token=x", DeviceId="bcd", Version="1"',
    );
  });
});
