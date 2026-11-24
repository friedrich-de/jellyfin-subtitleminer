/// <reference lib="webworker" />
/**
 * The page's service worker. It keeps this version's page files, so the app opens when the
 * Jellyfin server cannot be reached and says so, and touches nothing else: subtitles, pictures,
 * sound, settings and every other call go to the server as they are and are never kept.
 */
import { cacheName, isStale, keptIsBetter, keyOf } from './rules';

declare const self: ServiceWorkerGlobalScope;

/** Put in by the build: the page's files, relative to the page's address. */
const FILES: readonly string[] = ['__PAGE_FILES__'];

const CACHE = cacheName(__APP_VERSION__);
const scope = new URL(self.registration.scope);
const files = new Set(FILES.filter((name) => name !== './'));

self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE);
      await cache.addAll(FILES.map((name) => new URL(name, scope).href));
      await self.skipWaiting();
    })(),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      for (const name of await caches.keys()) {
        if (isStale(name, CACHE)) await caches.delete(name);
      }
      await self.clients.claim();
    })(),
  );
});

self.addEventListener('fetch', (event) => {
  const key = keyOf(event.request, scope, files);
  if (key === undefined) return;

  // The server first, so a new version shows at once; what was kept only when it cannot be reached.
  event.respondWith(
    (async () => {
      let response: Response;
      try {
        response = await fetch(event.request);
      } catch (error) {
        const kept = await caches.match(key, { cacheName: CACHE });
        if (kept !== undefined) return kept;
        throw error;
      }
      if (!keptIsBetter(response.status)) return response;
      return (await caches.match(key, { cacheName: CACHE })) ?? response;
    })(),
  );
});
