// Draws the app's icons as PNG from the SVGs in web/icons/, with the Chromium Playwright brings:
//
//   node dev/render-icons.mjs
//
// The PNGs go to web/public/icons/ and are committed, so a build needs no browser.
import { mkdir, readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const web = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'web');
const { chromium } = createRequire(path.join(web, 'package.json'))('@playwright/test');

const icons = [
  // Where an icon is shown as it is: a launcher, the window, the install prompt.
  { source: 'icon.svg', file: 'icon-192.png', size: 192 },
  { source: 'icon.svg', file: 'icon-512.png', size: 512 },
  // Where the system cuts its own shape out of a full square: Android.
  { source: 'icon-maskable.svg', file: 'icon-maskable-512.png', size: 512 },
  // iOS rounds the corners itself and shows transparency as black.
  { source: 'icon-maskable.svg', file: 'apple-touch-icon.png', size: 180 },
];

const out = path.join(web, 'public', 'icons');
await mkdir(out, { recursive: true });
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  for (const icon of icons) {
    const svg = await readFile(path.join(web, 'icons', icon.source), 'utf8');
    await page.setViewportSize({ width: icon.size, height: icon.size });
    await page.setContent(
      `<style>html,body{margin:0;background:transparent}svg{display:block;width:${icon.size}px;height:${icon.size}px}</style>${svg}`,
    );
    await page.screenshot({ path: path.join(out, icon.file), omitBackground: true });
    console.log(`web/public/icons/${icon.file}`);
  }
} finally {
  await browser.close();
}
