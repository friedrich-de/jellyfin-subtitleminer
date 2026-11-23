import { defineConfig, devices } from '@playwright/test';

// End-to-end tests against a real Jellyfin server with the packaged plugin installed.
// Start them with dev/e2e.sh, which downloads the server, packs the plugin and runs this.
const chromium = process.env.SUBTITLE_MINER_E2E_CHROMIUM;
const launchOptions = chromium === undefined || chromium === '' ? {} : { executablePath: chromium };

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/support/globalSetup.ts',
  // One server for all tests, so they run one after the other.
  fullyParallel: false,
  workers: 1,
  forbidOnly: process.env.CI !== undefined,
  retries: 0,
  reporter: [['list']],
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    trace: 'off',
    screenshot: 'only-on-failure',
    launchOptions,
  },
  projects: [
    { name: 'api', testMatch: 'api/**/*.spec.ts' },
    {
      name: 'desktop',
      testMatch: 'browser/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },
    {
      name: 'phone',
      testMatch: 'browser/**/*.spec.ts',
      use: { ...devices['Pixel 7'] },
    },
  ],
});
