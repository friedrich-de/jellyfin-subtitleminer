export type Fetch = typeof fetch;

/** The server answered, but not with what was asked for. */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'HttpError';
  }
}

/** The server did not answer at all. */
export class UnreachableError extends Error {
  constructor() {
    super('The server could not be reached.');
    this.name = 'UnreachableError';
  }
}

/**
 * The header Jellyfin reads a login from. Jellyfin 12 no longer reads the older X-Emby-Authorization
 * header, so this is the only one the page sends.
 */
export function tokenHeader(token: string): string {
  return `MediaBrowser Token="${token}"`;
}

/** What went wrong, in the server's words where it gave any. */
export async function problemText(response: Response): Promise<string> {
  try {
    const body: unknown = await response.clone().json();
    if (body !== null && typeof body === 'object') {
      const { detail, title } = body as { detail?: unknown; title?: unknown };
      if (typeof detail === 'string' && detail.length > 0) return detail;
      if (typeof title === 'string' && title.length > 0) return title;
    }
  } catch {
    // Not JSON: the status has to do.
  }
  return `The server answered ${response.status}.`;
}

/** Sends a request and tells "no answer" apart from "an answer that says no". */
export async function send(fetchFn: Fetch, url: string, init: RequestInit): Promise<Response> {
  let response: Response;
  try {
    response = await fetchFn(url, init);
  } catch (error) {
    // A request the page called off is no sign of a server that cannot be reached.
    if (init.signal?.aborted === true) throw error;
    throw new UnreachableError();
  }
  if (!response.ok) {
    throw new HttpError(response.status, await problemText(response));
  }
  return response;
}

/** Reads one of the plugin's or Jellyfin's JSON answers with a login. */
export async function getJson<T>(
  fetchFn: Fetch,
  url: string,
  token: string,
  signal?: AbortSignal,
): Promise<T> {
  const response = await send(fetchFn, url, {
    headers: { Authorization: tokenHeader(token), Accept: 'application/json' },
    ...(signal === undefined ? {} : { signal }),
  });
  return (await response.json()) as T;
}
