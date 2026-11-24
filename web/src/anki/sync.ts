import type { AnkiConnect } from './connect';
import { AnkiError } from './connect';

/**
 * What a server AnkiConnect answers a sync with. Desktop Anki's AnkiConnect answers nothing: it
 * only starts the sync.
 */
interface SyncResult {
  collection?: { outcome?: string; local_data_replaced?: boolean };
}

/**
 * Has Anki sync with AnkiWeb now, rather than when it next would by itself, and says in words how
 * it went. A failure is thrown as an AnkiError, in words for the user.
 */
export async function syncAnki(anki: AnkiConnect): Promise<string> {
  let result: SyncResult | null;
  try {
    result = await anki.invoke<SyncResult | null>('sync');
  } catch (error) {
    throw explainSync(error);
  }
  if (result === null || typeof result !== 'object') {
    return anki.where === 'device'
      ? 'Anki on this device is syncing with AnkiWeb.'
      : 'Anki synced with AnkiWeb.';
  }
  switch (result.collection?.outcome) {
    case 'no_changes':
      return 'Anki synced: AnkiWeb and your Anki already had the same cards.';
    case 'downloaded':
      return "Anki synced, taking AnkiWeb's whole collection: it had no changes of its own.";
    default:
      return 'Anki synced with AnkiWeb.';
  }
}

function explainSync(error: unknown): unknown {
  if (!(error instanceof AnkiError)) return error;
  const said = error.message;
  if (/already in progress/i.test(said)) {
    return new AnkiError('Anki is syncing with AnkiWeb already. Your cards go with that sync.');
  }
  if (/requires a full sync/i.test(said)) {
    return new AnkiError(
      'AnkiWeb asks for a full sync, and your Anki has changes that are not on AnkiWeb yet, so it did not sync. Nothing was replaced.',
    );
  }
  if (/collection is busy/i.test(said)) {
    return new AnkiError('Anki is busy with something else. Try again in a moment.');
  }
  return error;
}
