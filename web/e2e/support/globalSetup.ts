import { JellyfinServer } from './server';
import { STATE_VARIABLE } from './state';

/**
 * Starts the test server before the first test and stops it after the last one.
 * The tests learn about it through an environment variable, which Playwright hands to its workers.
 */
export default async function globalSetup(): Promise<() => Promise<void>> {
  const jellyfin = process.env.SUBTITLE_MINER_E2E_JELLYFIN;
  const packageDir = process.env.SUBTITLE_MINER_E2E_PACKAGE_DIR;
  if (jellyfin === undefined || jellyfin === '' || packageDir === undefined || packageDir === '') {
    throw new Error(
      'Set SUBTITLE_MINER_E2E_JELLYFIN and SUBTITLE_MINER_E2E_PACKAGE_DIR, or run dev/e2e.sh, which does both.',
    );
  }

  const server = await JellyfinServer.start({
    jellyfin,
    packageDir,
    web: process.env.SUBTITLE_MINER_E2E_JELLYFIN_WEB || undefined,
    ffmpeg: process.env.SUBTITLE_MINER_E2E_FFMPEG || undefined,
    keep: process.env.SUBTITLE_MINER_E2E_KEEP === '1',
  });
  process.env[STATE_VARIABLE] = JSON.stringify(server.state);
  return () => server.stop();
}
