import type { Watch } from '@/api/types';

import type { TrackHint } from './watches';

/** Where the page keeps the tracks chosen per show, in this browser. */
export const TRACKS_KEY = 'subtitle-miner.tracks';

/** The most shows remembered. The ones chosen longest ago are forgotten first. */
export const MAX_REMEMBERED = 200;

export interface Remembered {
  main?: TrackHint;
  /** "none": the user turned the second line off. */
  second?: TrackHint | 'none';
  /** When it was chosen, for forgetting the oldest. */
  at: number;
}

/** What the choice is kept under: the show for an episode, so the next episode finds it. */
export function showKeyOf(watch: Pick<Watch, 'SeriesName' | 'ItemId'>): string {
  return watch.SeriesName === undefined ? `item:${watch.ItemId}` : `show:${watch.SeriesName}`;
}

function readAll(storage: Storage): Record<string, Remembered> {
  try {
    const raw = storage.getItem(TRACKS_KEY);
    const parsed = raw === null ? null : (JSON.parse(raw) as unknown);
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, Remembered>)
      : {};
  } catch {
    return {};
  }
}

export function recall(storage: Storage, showKey: string): Remembered | undefined {
  const found = readAll(storage)[showKey];
  return found !== undefined && typeof found === 'object' ? found : undefined;
}

export function remember(
  storage: Storage,
  showKey: string,
  change: Partial<Omit<Remembered, 'at'>>,
  now: number,
): void {
  const all = readAll(storage);
  all[showKey] = { ...all[showKey], ...change, at: now };
  const kept = Object.entries(all)
    .sort((a, b) => (b[1].at ?? 0) - (a[1].at ?? 0))
    .slice(0, MAX_REMEMBERED);
  try {
    storage.setItem(TRACKS_KEY, JSON.stringify(Object.fromEntries(kept)));
  } catch {
    // Storage full or switched off: the choice holds until the page is closed.
  }
}
