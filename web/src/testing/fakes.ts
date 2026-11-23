import type { Fetch } from '@/api/http';
import type { AuthEnv, Status } from '@/auth/signIn';

/** A browser's storage, held in memory. */
export class MemoryStorage implements Storage {
  private readonly items = new Map<string, string>();

  get length(): number {
    return this.items.size;
  }

  clear(): void {
    this.items.clear();
  }

  getItem(key: string): string | null {
    return this.items.get(key) ?? null;
  }

  key(index: number): string | null {
    return [...this.items.keys()][index] ?? null;
  }

  removeItem(key: string): void {
    this.items.delete(key);
  }

  setItem(key: string, value: string): void {
    this.items.set(key, value);
  }
}

/** A storage the browser refuses to open, as in some private windows. */
export class BlockedStorage extends MemoryStorage {
  override getItem(): string | null {
    throw new DOMException('The operation is insecure.', 'SecurityError');
  }

  override setItem(): void {
    throw new DOMException('The operation is insecure.', 'SecurityError');
  }

  override removeItem(): void {
    throw new DOMException('The operation is insecure.', 'SecurityError');
  }
}

export interface SentRequest {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: unknown;
}

export type Answer = Response | Error | ((request: SentRequest) => Response | Error);

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

export function problem(status: number, detail: string): Response {
  return json({ status, detail }, status);
}

/** The headers of a request with lower-case names, whichever way they were given. */
function headerMap(headers: HeadersInit | undefined): Record<string, string> {
  const map: Record<string, string> = {};
  if (headers === undefined) return map;
  const pairs =
    headers instanceof Headers
      ? [...headers.entries()]
      : Array.isArray(headers)
        ? headers
        : Object.entries(headers);
  for (const [name, value] of pairs) {
    if (name !== undefined && value !== undefined) map[name.toLowerCase()] = value;
  }
  return map;
}

/** A server that answers from a list and remembers what it was asked. */
export function fakeServer(...answers: Answer[]): { fetch: Fetch; sent: SentRequest[] } {
  const sent: SentRequest[] = [];
  const fetch: Fetch = (input, init) => {
    const request: SentRequest = {
      url: typeof input === 'string' ? input : input instanceof URL ? input.href : input.url,
      method: init?.method ?? 'GET',
      headers: headerMap(init?.headers),
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : null,
    };
    sent.push(request);
    const next = answers.shift();
    if (next === undefined) return Promise.reject(new Error(`No answer left for ${request.url}`));
    const answer = typeof next === 'function' ? next(request) : next;
    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  };
  return { fetch, sent };
}

export const BASE = 'https://jellyfin.example/base/SubtitleMiner/';
export const STATUS_URL = 'https://jellyfin.example/base/SubtitleMiner/Api/Status';
export const LOGIN_URL = 'https://jellyfin.example/base/Users/AuthenticateByName';
export const LOGOUT_URL = 'https://jellyfin.example/base/Sessions/Logout';

export function status(overrides: Partial<Status> = {}): Status {
  return {
    Version: '2.0.0.0',
    UserId: '0f8fad5b-d9cb-469f-a165-70867728950e',
    UserName: 'mika',
    IsAdministrator: false,
    ServerName: 'Home',
    ServerVersion: '12.4.0',
    ...overrides,
  };
}

export function env(fetch: Fetch, storage: Storage = new MemoryStorage()): AuthEnv {
  return {
    storage,
    fetch,
    base: BASE,
    userAgent:
      'Mozilla/5.0 (Linux; Android 16; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36',
    version: '2.0.0.0',
  };
}

/** What Jellyfin's web client writes into the browser's storage after a login. */
export function webClientLogins(...logins: { token: string; used?: number }[]): string {
  return JSON.stringify({
    Servers: logins.map((login, index) => ({
      ManualAddress: 'https://jellyfin.example/base',
      Id: `server-${index}`,
      LastConnectionMode: 2,
      manualAddressOnly: true,
      Name: 'Home',
      UserId: `user-${index}`,
      AccessToken: login.token,
      DateLastAccessed: login.used ?? index,
    })),
  });
}
