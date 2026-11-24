import type { Line, Track, Watch } from '@/api/types';

/** Where playback is now: the server's position moved on by the time since it was sent. */
export function positionOf(watch: Watch, receivedAt: number, now: number): number {
  if (!watch.Playing || watch.Paused) return watch.PositionMs;
  const moved = watch.PositionMs + Math.max(0, now - receivedAt);
  return watch.RuntimeMs === undefined ? moved : Math.min(moved, watch.RuntimeMs);
}

/** A watch's name as people say it: "Sample Show · S1 E3", or a film's name and year. */
export function titleOf(watch: Watch): string {
  if (watch.SeriesName !== undefined) {
    const number = [
      watch.Season === undefined ? '' : `S${watch.Season}`,
      watch.Episode === undefined ? '' : `E${watch.Episode}`,
    ]
      .filter((part) => part !== '')
      .join(' ');
    return number === ''
      ? `${watch.SeriesName} · ${watch.Name}`
      : `${watch.SeriesName} · ${number}`;
  }

  return watch.Year === undefined ? watch.Name : `${watch.Name} (${watch.Year})`;
}

/** "7 min ago", for a watch that is over. */
export function agoOf(ms: number): string {
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  return hours === 1 ? '1 hour ago' : `${hours} hours ago`;
}

/** 07:36, or 1:02:09 past the first hour. */
export function clockOf(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  const two = (value: number): string => String(value).padStart(2, '0');
  const hours = Math.floor(seconds / 3600);
  const rest = `${two(Math.floor(seconds / 60) % 60)}:${two(seconds % 60)}`;
  return hours > 0 ? `${hours}:${rest}` : rest;
}

/**
 * Which watch the page shows.
 * - auto: what plays, else what was watched last.
 * - device: whatever this player plays, so the page moves on with it to the next episode.
 * - watch: this one, playing or not.
 */
export type Pick =
  | { kind: 'auto' }
  | { kind: 'device'; player: string; client: string; id: string }
  | { kind: 'watch'; id: string };

export function pickFor(watch: Watch): Pick {
  return watch.Playing
    ? { kind: 'device', player: watch.Player, client: watch.Client, id: watch.Id }
    : { kind: 'watch', id: watch.Id };
}

export function choose(watches: readonly Watch[], pick: Pick): Watch | undefined {
  if (pick.kind === 'device') {
    const playing = watches.find(
      (watch) => watch.Playing && watch.Player === pick.player && watch.Client === pick.client,
    );
    return playing ?? watches.find((watch) => watch.Id === pick.id) ?? watches[0];
  }

  if (pick.kind === 'watch') return watches.find((watch) => watch.Id === pick.id) ?? watches[0];
  return watches[0];
}

/** The two- and three-letter codes of a language, and how titles name it. */
const LANGUAGES: Record<string, { codes: string[]; names: RegExp; tag: string }> = {
  japanese: { codes: ['ja', 'jp', 'jpn'], names: /japanese|日本語|\bjp\b/i, tag: 'ja' },
  english: { codes: ['en', 'eng'], names: /english/i, tag: 'en' },
};

/** The language of a track as a tag for the lang attribute, where it is known. */
export function languageTag(track: Track | undefined): string | undefined {
  if (track?.Language === undefined) return undefined;
  const code = track.Language.toLowerCase();
  for (const language of Object.values(LANGUAGES)) {
    if (language.codes.includes(code)) return language.tag;
  }

  return /^[a-z]{2}$/.test(code) ? code : undefined;
}

function speaks(track: Track, language: string): boolean {
  const known = LANGUAGES[language];
  if (known === undefined) return false;
  if (track.Language !== undefined && known.codes.includes(track.Language.toLowerCase())) {
    return true;
  }

  return track.Language === undefined && track.Title !== undefined && known.names.test(track.Title);
}

/** A track of signs or songs only, by what its title says. */
function isPartial(track: Track): boolean {
  return track.Forced || /sign|song|forced|karaoke|\bop\b|\bed\b|看板|歌/i.test(track.Title ?? '');
}

/** The name a track is shown by: "Japanese · file next to the video". */
export function trackLabel(track: Track): string {
  const language = track.Language ?? 'Unknown language';
  const parts = [track.Title !== undefined && track.Title !== '' ? track.Title : language];
  if (track.Title !== undefined && track.Title !== '' && track.Language !== undefined) {
    parts.push(track.Language);
  }

  if (track.External) parts.push('file');
  if (!track.Readable) parts.push('pictures, cannot be read');
  return parts.join(' · ');
}

/** What the page remembers of a chosen track, so it finds the same one in the next episode. */
export interface TrackHint {
  key: string;
  external: boolean;
  language?: string;
  title?: string;
}

export function hintOf(track: Track): TrackHint {
  return {
    key: track.Key,
    external: track.External,
    ...(track.Language === undefined ? {} : { language: track.Language }),
    ...(track.Title === undefined ? {} : { title: track.Title }),
  };
}

/** The track a hint points to: the same key, else one like it. The next episode's file has another key. */
export function findHinted(
  tracks: readonly Track[],
  hint: TrackHint | undefined,
): Track | undefined {
  if (hint === undefined) return undefined;
  const readable = tracks.filter((track) => track.Readable);
  return (
    readable.find((track) => track.Key === hint.key) ??
    readable.find(
      (track) =>
        track.External === hint.external &&
        track.Language === hint.language &&
        track.Title === hint.title,
    )
  );
}

/**
 * The track whose lines the page shows: the one chosen for this show before, else a full track
 * in the language being learnt, else what the player shows, else the first that can be read.
 */
export function chooseMain(
  watch: Watch,
  language: string,
  hint: TrackHint | undefined,
): Track | undefined {
  const readable = watch.Subtitles.filter((track) => track.Readable);
  const hinted = findHinted(readable, hint);
  if (hinted !== undefined) return hinted;

  const inLanguage = readable.filter((track) => speaks(track, language));
  const shown = readable.find((track) => track.Key === watch.SubtitleKey);
  return (
    inLanguage.find((track) => !isPartial(track) && track.Key === watch.SubtitleKey) ??
    inLanguage.find((track) => !isPartial(track) && track.Default) ??
    inLanguage.find((track) => !isPartial(track)) ??
    inLanguage[0] ??
    shown ??
    readable[0]
  );
}

/**
 * The track shown as a second line under each line, for its meaning: the one chosen for this show
 * before, else none. "none" in the hint means the user turned it off.
 */
export function chooseSecond(
  watch: Watch,
  main: Track | undefined,
  hint: TrackHint | 'none' | undefined,
): Track | undefined {
  if (hint === undefined || hint === 'none') return undefined;
  const found = findHinted(watch.Subtitles, hint);
  return found?.Key === main?.Key ? undefined : found;
}

/** A line with the line of the second track that is said at the same time. */
export interface ShownLine extends Line {
  second?: string;
}

/**
 * Puts the second track's lines under the first's: each line gets the lines of the second track
 * that overlap it for more than half of the shorter of the two.
 */
export function pairLines(main: readonly Line[], second: readonly Line[] | undefined): ShownLine[] {
  if (second === undefined || second.length === 0) return [...main];
  const sorted = [...second].sort((a, b) => a.StartMs - b.StartMs);
  let from = 0;
  return main.map((line) => {
    while (from < sorted.length && sorted[from]!.EndMs <= line.StartMs) from += 1;
    const texts: string[] = [];
    for (let i = from; i < sorted.length && sorted[i]!.StartMs < line.EndMs; i += 1) {
      const other = sorted[i]!;
      if (saidTogether(line, other) && !texts.includes(other.Text)) texts.push(other.Text);
    }

    return texts.length === 0 ? { ...line } : { ...line, second: texts.join('\n') };
  });
}

/** Whether a line of the second track is said with a line: for more than half the shorter one. */
function saidTogether(line: Line, other: Line): boolean {
  const overlap = Math.min(line.EndMs, other.EndMs) - Math.max(line.StartMs, other.StartMs);
  const shorter = Math.min(line.EndMs - line.StartMs, other.EndMs - other.StartMs);
  return overlap > shorter / 2;
}

/**
 * The second track's lines said with any of the chosen lines, each once and in the order said:
 * the meaning of all of them for a card. One line of the second track can span several chosen ones.
 */
export function secondOf(chosen: readonly Line[], second: readonly Line[] | undefined): string[] {
  if (second === undefined) return [];
  const texts: string[] = [];
  for (const other of [...second].sort((a, b) => a.StartMs - b.StartMs)) {
    if (chosen.some((line) => saidTogether(line, other)) && !texts.includes(other.Text)) {
      texts.push(other.Text);
    }
  }
  return texts;
}

/** The lines on screen at a moment. */
export function linesAt(lines: readonly Line[], ms: number): Line[] {
  return lines.filter((line) => line.StartMs <= ms && ms < line.EndMs);
}

/** The last line that started by a moment: where the page scrolls to while it follows. */
export function lastStartedAt(lines: readonly Line[], ms: number): Line | undefined {
  let low = 0;
  let high = lines.length - 1;
  let found: Line | undefined;
  while (low <= high) {
    const middle = (low + high) >> 1;
    const line = lines[middle]!;
    if (line.StartMs <= ms) {
      found = line;
      low = middle + 1;
    } else {
      high = middle - 1;
    }
  }

  return found;
}
