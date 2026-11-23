/**
 * Addresses, worked out from where the page is served: <Jellyfin address>/SubtitleMiner/.
 * They are relative to the page, so they hold behind a reverse proxy with a base path.
 */

/** The page's own folder. */
export function pageAddress(base: string): URL {
  return new URL('./', base);
}

/** One of the plugin's endpoints, by its path below /SubtitleMiner/Api/. */
export function apiUrl(base: string, path: string): string {
  return new URL(`Api/${path}`, pageAddress(base)).href;
}

/** One of Jellyfin's own addresses, by its path below the server's address. */
export function jellyfinUrl(base: string, path: string): string {
  return new URL(`../${path}`, pageAddress(base)).href;
}
