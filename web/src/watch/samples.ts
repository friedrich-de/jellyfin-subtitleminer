import type { Line, Track, Watch } from '@/api/types';

/** Tracks and watches for tests, shaped like the plugin's answers. */

export function track(key: string, overrides: Partial<Track> = {}): Track {
  return {
    Key: key,
    Codec: 'ass',
    External: key.startsWith('x'),
    Default: false,
    Forced: false,
    Readable: true,
    ...overrides,
  };
}

/** Changes to a sample. A field set to undefined is left out, as the plugin leaves it out. */
export type Changes<T> = { [K in keyof T]?: T[K] | undefined };

/** Episode 3 of a show on the living room's mpv: a Japanese file, Japanese and English inside. */
export function sampleWatch(overrides: Changes<Watch> = {}): Watch {
  const watch: Record<string, unknown> = {
    Id: 'watch-1',
    ItemId: 'aaaaaaaabbbbccccddddeeeeeeeeeeee',
    MediaSourceId: 'aaaaaaaabbbbccccddddeeeeeeeeeeee',
    Playing: true,
    Paused: false,
    PositionMs: 4_500,
    RuntimeMs: 1_420_000,
    Player: 'Living room',
    Client: 'mpv',
    Name: 'The Promise',
    SeriesName: 'Sample Show',
    Season: 1,
    Episode: 3,
    SubtitleKey: 's1',
    AudioKey: 'a0',
    CanCut: true,
    Subtitles: [
      track('x0a1b2c3d4e5f', { Language: 'jpn', Codec: 'subrip' }),
      track('s0', { Language: 'jpn', Title: 'Signs', Forced: true }),
      track('s1', { Language: 'eng', Title: 'English', Default: true }),
      track('s2', { Language: 'jpn', Codec: 'PGSSUB', Readable: false }),
    ],
    Audio: [track('a0', { Language: 'jpn', Codec: 'aac', External: false })],
    ...overrides,
  };
  for (const [name, value] of Object.entries(watch)) {
    if (value === undefined) delete watch[name];
  }

  return watch as unknown as Watch;
}

export function line(
  id: number,
  startMs: number,
  endMs: number,
  text: string,
  more: Partial<Line> = {},
): Line {
  return { Id: id, StartMs: startMs, EndMs: endMs, Text: text, Kind: 'dialogue', ...more };
}
