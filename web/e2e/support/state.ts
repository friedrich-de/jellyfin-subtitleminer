import type { ServerState } from './server';

/** The environment variable the setup describes the running test server in. */
export const STATE_VARIABLE = 'SUBTITLE_MINER_E2E_STATE';

export function readState(): ServerState {
  const text = process.env[STATE_VARIABLE];
  if (text === undefined) throw new Error('The test server was not started. Run dev/e2e.sh.');
  return JSON.parse(text) as ServerState;
}
