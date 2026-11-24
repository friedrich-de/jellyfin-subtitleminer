import { describe, expect, it } from 'vitest';

import { cacheName, isStale, keptFiles, keptIsBetter, keyOf } from './rules';

const scope = new URL('https://media.example/jellyfin/SubtitleMiner/');
const files = new Set(['assets/index-abc.js', 'icons/icon-192.png', 'manifest.webmanifest']);

function request(url: string, init: { method?: string; mode?: string } = {}) {
  return { url, method: init.method ?? 'GET', mode: init.mode ?? 'cors' };
}

describe('the page’s service worker', () => {
  it('keeps the page and its build, not itself', () => {
    expect(
      keptFiles([
        'index.html',
        'sw.js',
        'assets/index-abc.js',
        'assets/index-abc.js.map',
        'favicon.svg',
      ]),
    ).toEqual(['./', 'assets/index-abc.js', 'favicon.svg']);
  });

  it('answers the page and its files from where they are kept', () => {
    expect(keyOf(request(scope.href, { mode: 'navigate' }), scope, files)).toBe(scope.href);
    expect(keyOf(request(`${scope.href}?from=app`, { mode: 'navigate' }), scope, files)).toBe(
      scope.href,
    );
    expect(keyOf(request(`${scope.href}assets/index-abc.js`), scope, files)).toBe(
      `${scope.href}assets/index-abc.js`,
    );
  });

  it('never touches the plugin’s calls, Jellyfin, another site or another file', () => {
    for (const url of [
      `${scope.href}Api/Settings`,
      `${scope.href}Api/Items/abc/Picture?at=1000`,
      `${scope.href}Api/Events`,
      `${scope.href}assets/index-abc.js?v=2`,
      `${scope.href}assets/other.js`,
      'https://media.example/jellyfin/Users/Me',
      'https://media.example/jellyfin/SubtitleMinerX/assets/index-abc.js',
      'https://elsewhere.example/jellyfin/SubtitleMiner/',
      'http://127.0.0.1:8765/',
    ]) {
      expect(keyOf(request(url), scope, files), url).toBeUndefined();
    }
    expect(keyOf(request(scope.href, { method: 'POST' }), scope, files)).toBeUndefined();
  });

  it('opens what it kept when a proxy says Jellyfin is down, not when the page is gone', () => {
    expect([502, 503, 504, 500].map(keptIsBetter)).toEqual([true, true, true, true]);
    expect([200, 304, 404, 403].map(keptIsBetter)).toEqual([false, false, false, false]);
  });

  it('drops what older versions kept, and nothing of anyone else', () => {
    const current = cacheName('2.0.0.1');
    expect(isStale(cacheName('2.0.0.0'), current)).toBe(true);
    expect(isStale(current, current)).toBe(false);
    expect(isStale('another-app', current)).toBe(false);
  });
});
