import { readFileSync } from 'node:fs';
import { fileURLToPath, URL } from 'node:url';

import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vitest/config';

// The plugin's version, so the page can say which build it is.
const props = readFileSync(new URL('../Directory.Build.props', import.meta.url), 'utf8');
const version = /<Version>([^<]+)<\/Version>/.exec(props)?.[1] ?? '0.0.0.0';

// For "npm run dev": the address of a Jellyfin server that has the plugin installed.
const jellyfin = process.env.JELLYFIN_URL ?? 'http://localhost:8096';

export default defineConfig({
  // The page is served at <Jellyfin address>/SubtitleMiner/, wherever that is, so its files are linked relatively.
  base: './',
  plugins: [vue()],
  define: {
    __APP_VERSION__: JSON.stringify(version),
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    target: 'es2022',
    // No inline scripts or styles anywhere: the page's content policy would refuse them.
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
  },
  server: {
    proxy: {
      '/Api': { target: `${jellyfin}/SubtitleMiner`, changeOrigin: true },
      '/Users': { target: jellyfin, changeOrigin: true },
      '/Sessions': { target: jellyfin, changeOrigin: true },
    },
  },
  test: {
    environment: 'happy-dom',
    include: ['src/**/*.test.ts'],
    restoreMocks: true,
  },
});
