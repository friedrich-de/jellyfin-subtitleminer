import { expect, test } from '../support/fixtures';

const PAGE = '/SubtitleMiner/';

/** The width and height a PNG says it has. */
function pngSize(file: Buffer): string {
  expect(file.subarray(1, 4).toString('latin1')).toBe('PNG');
  return `${file.readUInt32BE(16)}x${file.readUInt32BE(20)}`;
}

interface Manifest {
  id: string;
  start_url: string;
  scope: string;
  display: string;
  icons: { src: string; sizes: string; type: string; purpose: string }[];
}

test.describe('the page as an app', () => {
  test('has a manifest whose icons are there, in the sizes it says', async ({ request }) => {
    const html = await (await request.get(PAGE)).text();
    expect(html).toContain('<link rel="manifest" href="./manifest.webmanifest" />');

    const response = await request.get(`${PAGE}manifest.webmanifest`);
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toBe('application/manifest+json; charset=utf-8');
    const manifest = (await response.json()) as Manifest;

    // Relative, so the app is the page's address wherever Jellyfin is, also below a path.
    expect(manifest).toMatchObject({
      id: './',
      start_url: './',
      scope: './',
      display: 'standalone',
    });
    expect(manifest.icons.map((icon) => icon.purpose).sort()).toEqual(['any', 'any', 'maskable']);
    for (const icon of manifest.icons) {
      const file = await request.get(`${PAGE}${icon.src}`);
      expect(file.status(), icon.src).toBe(200);
      expect(file.headers()['content-type']).toBe(icon.type);
      expect(pngSize(await file.body()), icon.src).toBe(icon.sizes);
    }

    const touch = await request.get(`${PAGE}icons/apple-touch-icon.png`);
    expect(pngSize(await touch.body())).toBe('180x180');
  });

  test('has a worker that keeps only files of the page, all of them there', async ({ request }) => {
    const response = await request.get(`${PAGE}sw.js`);
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toBe('text/javascript; charset=utf-8');
    // A new version of the plugin brings a new worker, which the browser must find.
    expect(response.headers()['cache-control']).toBe('no-cache');
    expect(response.headers()['content-security-policy']).toContain("default-src 'none'");

    const code = await response.text();
    const list = /\[("\.\/"(?:,"[^"]+")*)\]/.exec(code)?.[1];
    expect(list, 'the list of kept files').toBeDefined();
    const files = JSON.parse(`[${list}]`) as string[];
    expect(files).toContain('manifest.webmanifest');
    expect(files.some((file) => file.startsWith('assets/') && file.endsWith('.js'))).toBe(true);
    for (const file of files) {
      expect(file, 'nothing of the API').not.toMatch(/^Api\//i);
      const kept = await request.get(`${PAGE}${file === './' ? '' : file}`);
      expect(kept.status(), file).toBe(200);
    }
  });
});
