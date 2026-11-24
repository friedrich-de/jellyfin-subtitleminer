/**
 * The logins this browser holds for the Jellyfin server the page is served from.
 *
 * Jellyfin's web client keeps its login in the browser's storage for this address. The page reads it,
 * so whoever is signed in to Jellyfin is signed in here too. Where there is none (an app installed on
 * an iPhone has its own storage, for example) the page signs in itself and keeps that login apart.
 */

/** Where Jellyfin's web client keeps its logins. */
export const WEB_CLIENT_KEY = 'jellyfin_credentials';

/** Where the page keeps a login it made itself. */
export const SESSION_KEY = 'subtitle-miner.session';

/** Where the page keeps the id it gives this browser, so Jellyfin lists it as one device. */
export const DEVICE_KEY = 'subtitle-miner.device-id';

export interface StoredSession {
  accessToken: string;
  userId: string;
  userName: string;
  /**
   * The page's address the login was made at. Two Jellyfin servers under different paths of one
   * address share the browser's storage, and one's login is never sent to the other.
   */
  base?: string;
}

function read(storage: Storage, key: string): unknown {
  try {
    const raw = storage.getItem(key);
    return raw === null ? null : (JSON.parse(raw) as unknown);
  } catch {
    // Storage switched off, or something else wrote there: the same as nothing stored.
    return null;
  }
}

/** A server's id as Jellyfin writes it in one place or another: with or without dashes, in any case. */
function sameServer(a: string, b: string): boolean {
  const plain = (id: string) => id.replaceAll('-', '').toLowerCase();
  return plain(a) === plain(b);
}

/**
 * The web client's logins to one server, the one used last first. The web client keeps a login for
 * each server it was used with, and a login to another server is never sent to this one.
 * Without a server, whether the web client holds any login at all.
 */
export function readWebClientTokens(storage: Storage, serverId?: string): string[] {
  const stored = read(storage, WEB_CLIENT_KEY);
  if (stored === null || typeof stored !== 'object') return [];
  const servers = (stored as { Servers?: unknown }).Servers;
  if (!Array.isArray(servers)) return [];

  const logins: { token: string; used: number }[] = [];
  for (const server of servers as unknown[]) {
    if (server === null || typeof server !== 'object') continue;
    const { AccessToken, DateLastAccessed, Id } = server as {
      AccessToken?: unknown;
      DateLastAccessed?: unknown;
      Id?: unknown;
    };
    if (typeof AccessToken !== 'string' || AccessToken.length === 0) continue;
    if (serverId !== undefined && !(typeof Id === 'string' && sameServer(Id, serverId))) continue;
    logins.push({
      token: AccessToken,
      used: typeof DateLastAccessed === 'number' ? DateLastAccessed : 0,
    });
  }
  logins.sort((a, b) => b.used - a.used);
  return [...new Set(logins.map((login) => login.token))];
}

/** The login the page made itself, if any. */
export function readOwnSession(storage: Storage): StoredSession | null {
  const stored = read(storage, SESSION_KEY);
  if (stored === null || typeof stored !== 'object') return null;
  const { accessToken, userId, userName, base } = stored as Record<string, unknown>;
  if (typeof accessToken !== 'string' || accessToken.length === 0) return null;
  return {
    accessToken,
    userId: typeof userId === 'string' ? userId : '',
    userName: typeof userName === 'string' ? userName : '',
    ...(typeof base === 'string' ? { base } : {}),
  };
}

export function writeOwnSession(storage: Storage, session: StoredSession): void {
  try {
    storage.setItem(SESSION_KEY, JSON.stringify(session));
  } catch {
    // Without storage the login lasts until the page is closed.
  }
}

export function clearOwnSession(storage: Storage): void {
  try {
    storage.removeItem(SESSION_KEY);
  } catch {
    // Nothing was stored then.
  }
}

/**
 * A random id. crypto.randomUUID is there only where the browser trusts the address (HTTPS, or
 * the device itself), and Jellyfin is often opened at http://192.168.1.10:8096, where it is not.
 * getRandomValues is there everywhere.
 */
export function randomId(
  random: {
    getRandomValues(array: Uint8Array<ArrayBuffer>): Uint8Array<ArrayBuffer>;
    randomUUID?: () => string;
  } = crypto,
): string {
  if (typeof random.randomUUID === 'function') return random.randomUUID();
  const bytes = random.getRandomValues(new Uint8Array(16));
  // A version 4 UUID, as randomUUID makes them.
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** This browser's id for Jellyfin's list of devices. Made once and kept. */
export function deviceId(storage: Storage, make: () => string = () => randomId()): string {
  try {
    const kept = storage.getItem(DEVICE_KEY);
    if (kept !== null && /^[A-Za-z0-9-]{8,64}$/.test(kept)) return kept;
    const made = make();
    storage.setItem(DEVICE_KEY, made);
    return made;
  } catch {
    return make();
  }
}

/** A short name for this browser, as Jellyfin's list of devices shows it. */
export function deviceName(userAgent: string): string {
  const browser = /Edg\//.test(userAgent)
    ? 'Edge'
    : /OPR\//.test(userAgent)
      ? 'Opera'
      : /Firefox\//.test(userAgent)
        ? 'Firefox'
        : /Chrome\//.test(userAgent)
          ? 'Chrome'
          : /Safari\//.test(userAgent)
            ? 'Safari'
            : 'Browser';
  const system = /iPhone/.test(userAgent)
    ? 'iPhone'
    : /iPad/.test(userAgent)
      ? 'iPad'
      : /Android/.test(userAgent)
        ? 'Android'
        : /Windows/.test(userAgent)
          ? 'Windows'
          : /Mac OS X/.test(userAgent)
            ? 'Mac'
            : /Linux/.test(userAgent)
              ? 'Linux'
              : '';
  return system === '' ? browser : `${browser} on ${system}`;
}

/** The header that introduces the page to Jellyfin when it signs in with a name and password. */
export function clientHeader(client: {
  device: string;
  deviceId: string;
  version: string;
}): string {
  // Jellyfin splits the header at quotes and commas, so none may be inside a value.
  const clean = (value: string) => value.replace(/["',\\]/g, '');
  return (
    `MediaBrowser Client="Subtitle Miner", Device="${clean(client.device)}", ` +
    `DeviceId="${clean(client.deviceId)}", Version="${clean(client.version)}"`
  );
}
