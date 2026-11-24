import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath, URL } from 'node:url';

import vue from '@vitejs/plugin-vue';
import type { Plugin } from 'vite';
import { defineConfig } from 'vitest/config';

import { keptFiles } from './src/worker/rules.ts';

// The plugin's version, so the page can say which build it is.
const props = readFileSync(new URL('../Directory.Build.props', import.meta.url), 'utf8');
const version = /<Version>([^<]+)<\/Version>/.exec(props)?.[1] ?? '0.0.0.0';

// For "npm run dev": the address of a Jellyfin server that has the plugin installed.
const jellyfin = process.env.JELLYFIN_URL ?? 'http://localhost:8096';

/** The files of public/, which the build copies as they are, with forward slashes. */
function publicFiles(): string[] {
  const root = fileURLToPath(new URL('./public', import.meta.url));
  return readdirSync(root, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => `${entry.parentPath}/${entry.name}`.slice(root.length + 1).replace(/\\/g, '/'));
}

/** Puts the list of the page's files into the service worker, which keeps them for offline. */
function workerFiles(): Plugin {
  return {
    name: 'subtitle-miner-worker-files',
    apply: 'build',
    generateBundle(_options, bundle) {
      const worker = bundle['sw.js'];
      if (worker?.type !== 'chunk') throw new Error('The build made no sw.js.');
      const files = keptFiles([...Object.keys(bundle), ...publicFiles()]);
      // The minifier may quote it with any of the three.
      const placeholder = /\[\s*(["'`])__PAGE_FILES__\1\s*\]/;
      if (!placeholder.test(worker.code)) throw new Error('sw.js has no place for its files.');
      worker.code = worker.code.replace(placeholder, JSON.stringify(files));
    },
  };
}

export default defineConfig({
  // The page is served at <Jellyfin address>/SubtitleMiner/, wherever that is, so its files are linked relatively.
  base: './',
  plugins: [vue(), workerFiles()],
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
    rollupOptions: {
      // The service worker is built on its own, at a fixed name next to the page, so that it may
      // look after the page's whole address.
      input: {
        index: fileURLToPath(new URL('./index.html', import.meta.url)),
        sw: fileURLToPath(new URL('./src/worker/sw.ts', import.meta.url)),
      },
      output: {
        entryFileNames: (chunk) => (chunk.name === 'sw' ? 'sw.js' : 'assets/[name]-[hash].js'),
      },
    },
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
