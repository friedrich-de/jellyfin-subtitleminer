/**
 * What the page's service worker keeps and answers. It imports nothing, so the worker and the
 * tests can both use it.
 */

/** The start of the name of every cache the worker makes. */
export const CACHE_PREFIX = 'subtitle-miner-page-';

/** The cache of one version of the page. A new version gets a new one; the old ones go. */
export function cacheName(version: string): string {
  return CACHE_PREFIX + version;
}

/** Whether a cache is one of the worker's own from another version. */
export function isStale(name: string, current: string): boolean {
  return name.startsWith(CACHE_PREFIX) && name !== current;
}

/**
 * The files the worker keeps: the page itself, kept as its address, and the rest of its build,
 * relative to that address. Not the worker itself, which the browser looks after.
 */
export function keptFiles(built: readonly string[]): string[] {
  const files = built
    .filter((name) => name !== 'sw.js' && name !== 'index.html' && !name.endsWith('.map'))
    .sort();
  return ['./', ...new Set(files)];
}

/**
 * Where a request is kept, or `undefined` when it is not one of the page's files. The worker only
 * ever answers these: what the plugin's API, Jellyfin or anyone else sends is never touched or kept.
 */
export function keyOf(
  request: { url: string; method: string; mode: string },
  scope: URL,
  files: ReadonlySet<string>,
): string | undefined {
  if (request.method !== 'GET') return undefined;
  const url = new URL(request.url);
  if (url.origin !== scope.origin || !url.pathname.startsWith(scope.pathname)) return undefined;
  const name = url.pathname.slice(scope.pathname.length);
  if (name === '' && (request.mode === 'navigate' || url.search === '')) return scope.href;
  if (url.search !== '' || !files.has(name)) return undefined;
  return new URL(name, scope).href;
}

/**
 * Whether the server's answer is worse than what was kept: a proxy in front of Jellyfin answers
 * 502 or 503 while Jellyfin is down, and the kept page then says so in its own words. A 404 is
 * left as it is, since then the plugin itself may be gone.
 */
export function keptIsBetter(status: number): boolean {
  return status >= 500;
}
