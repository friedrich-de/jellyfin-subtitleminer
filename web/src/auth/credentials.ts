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

/** The web client's logins, the one used last first. */
export function readWebClientTokens(storage: Storage): string[] {
  const stored = read(storage, WEB_CLIENT_KEY);
  if (stored === null || typeof stored !== 'object') return [];
  const servers = (stored as { Servers?: unknown }).Servers;
  if (!Array.isArray(servers)) return [];

  const logins: { token: string; used: number }[] = [];
  for (const server of servers as unknown[]) {
    if (server === null || typeof server !== 'object') continue;
    const { AccessToken, DateLastAccessed } = server as {
      AccessToken?: unknown;
      DateLastAccessed?: unknown;
    };
    if (typeof AccessToken !== 'string' || AccessToken.length === 0) continue;
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
  const { accessToken, userId, userName } = stored as Record<string, unknown>;
  if (typeof accessToken !== 'string' || accessToken.length === 0) return null;
  return {
    accessToken,
    userId: typeof userId === 'string' ? userId : '',
    userName: typeof userName === 'string' ? userName : '',
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

/** This browser's id for Jellyfin's list of devices. Made once and kept. */
export function deviceId(storage: Storage, make: () => string = () => crypto.randomUUID()): string {
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
