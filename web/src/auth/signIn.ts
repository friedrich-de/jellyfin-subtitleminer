import { apiUrl, jellyfinUrl } from '@/api/addresses';
import { type Fetch, getJson, HttpError, send, tokenHeader, UnreachableError } from '@/api/http';

import {
  clearOwnSession,
  clientHeader,
  deviceId,
  deviceName,
  readOwnSession,
  readWebClientTokens,
  writeOwnSession,
} from './credentials';

/** What the plugin says about a login: GET /SubtitleMiner/Api/Status. */
export interface Status {
  Version: string;
  UserId: string;
  UserName: string;
  IsAdministrator: boolean;
  ServerName: string;
  ServerVersion: string;
}

export interface Session {
  token: string;
  /** Whose login it is: Jellyfin's web client in this browser, or one the page made itself. */
  source: 'jellyfin' | 'page';
  status: Status;
}

export type Resolution =
  | { kind: 'signed-in'; session: Session }
  | { kind: 'signed-out' }
  | { kind: 'unreachable' }
  | { kind: 'failed'; message: string };

export type PasswordResult = Resolution | { kind: 'refused'; message: string };

/** Everything signing in touches, so tests can stand in for the browser. */
export interface AuthEnv {
  storage: Storage;
  fetch: Fetch;
  /** The page's address. */
  base: string;
  userAgent: string;
  /** The plugin's version, as the page was built with it. */
  version: string;
}

/**
 * Signs in with a login this browser already holds: the page's own first, then the web client's.
 * A login the server no longer accepts is skipped.
 */
export async function resolveSession(env: AuthEnv): Promise<Resolution> {
  const own = readOwnSession(env.storage);
  const candidates: { token: string; source: Session['source'] }[] = [];
  if (own !== null) candidates.push({ token: own.accessToken, source: 'page' });
  for (const token of readWebClientTokens(env.storage)) {
    if (!candidates.some((candidate) => candidate.token === token)) {
      candidates.push({ token, source: 'jellyfin' });
    }
  }

  for (const candidate of candidates) {
    try {
      const status = await getJson<Status>(env.fetch, apiUrl(env.base, 'Status'), candidate.token);
      return { kind: 'signed-in', session: { ...candidate, status } };
    } catch (error) {
      if (error instanceof UnreachableError) return { kind: 'unreachable' };
      if (error instanceof HttpError && (error.status === 401 || error.status === 403)) {
        // Signed out elsewhere, or a login without a user. The page's own dead login is forgotten.
        if (candidate.source === 'page' && error.status === 401) clearOwnSession(env.storage);
        continue;
      }
      return { kind: 'failed', message: error instanceof Error ? error.message : String(error) };
    }
  }
  return { kind: 'signed-out' };
}

/** Signs in with a Jellyfin name and password and keeps the login for next time. */
export async function signInWithPassword(
  env: AuthEnv,
  name: string,
  password: string,
): Promise<PasswordResult> {
  let token: string;
  try {
    const response = await send(env.fetch, jellyfinUrl(env.base, 'Users/AuthenticateByName'), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
        Authorization: clientHeader({
          device: deviceName(env.userAgent),
          deviceId: deviceId(env.storage),
          version: env.version,
        }),
      },
      body: JSON.stringify({ Username: name, Pw: password }),
    });
    const answer = (await response.json()) as {
      AccessToken?: unknown;
      User?: { Id?: unknown; Name?: unknown };
    };
    if (typeof answer.AccessToken !== 'string' || answer.AccessToken.length === 0) {
      return { kind: 'failed', message: 'The server gave no login.' };
    }
    token = answer.AccessToken;
    writeOwnSession(env.storage, {
      accessToken: token,
      userId: typeof answer.User?.Id === 'string' ? answer.User.Id : '',
      userName: typeof answer.User?.Name === 'string' ? answer.User.Name : name,
    });
  } catch (error) {
    if (error instanceof UnreachableError) return { kind: 'unreachable' };
    if (error instanceof HttpError && error.status === 401) {
      return { kind: 'refused', message: 'That name or password is not right.' };
    }
    return { kind: 'failed', message: error instanceof Error ? error.message : String(error) };
  }

  try {
    const status = await getJson<Status>(env.fetch, apiUrl(env.base, 'Status'), token);
    return { kind: 'signed-in', session: { token, source: 'page', status } };
  } catch (error) {
    if (error instanceof UnreachableError) return { kind: 'unreachable' };
    return { kind: 'failed', message: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * Ends a login the page made itself. A login that belongs to Jellyfin's web client is left alone:
 * ending it here would sign the user out of Jellyfin.
 */
export async function signOut(env: AuthEnv, session: Session): Promise<void> {
  if (session.source !== 'page') return;
  clearOwnSession(env.storage);
  try {
    await env.fetch(jellyfinUrl(env.base, 'Sessions/Logout'), {
      method: 'POST',
      headers: { Authorization: tokenHeader(session.token) },
    });
  } catch {
    // The login is forgotten here either way.
  }
}
