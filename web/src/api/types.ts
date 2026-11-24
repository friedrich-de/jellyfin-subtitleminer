/**
 * The plugin's answers, as its endpoints below /SubtitleMiner/Api/ send them. Jellyfin writes
 * names with a capital and leaves out what has no value.
 */

/** A subtitle or audio track of a video. */
export interface Track {
  /** The name the plugin knows the track by. It stays the same when Jellyfin renumbers the tracks. */
  Key: string;
  Language?: string;
  Title?: string;
  Codec: string;
  /** A file next to the video. */
  External: boolean;
  Default: boolean;
  Forced: boolean;
  /** Whether the plugin can read the lines. Tracks made of pictures cannot be read. */
  Readable: boolean;
}

/** A video on a player: playing now, or watched in the last hours. */
export interface Watch {
  Id: string;
  ItemId: string;
  MediaSourceId: string;
  Playing: boolean;
  Paused: boolean;
  /** Where playback was at the moment the server sent this, in milliseconds. */
  PositionMs: number;
  RuntimeMs?: number;
  StoppedAgoMs?: number;
  /** The device's name. */
  Player: string;
  /** The app's name. */
  Client: string;
  Name: string;
  SeriesName?: string;
  Season?: number;
  Episode?: number;
  Year?: number;
  /** The track the player shows, when it shows one. */
  SubtitleKey?: string;
  AudioKey?: string;
  /** Whether pictures and sound can be cut from the video. */
  CanCut: boolean;
  Subtitles: Track[];
  Audio: Track[];
}

export interface WatchList {
  Watches: Watch[];
}

export type LineKind = 'dialogue' | 'sign' | 'song';

/** One subtitle line. Times in milliseconds. */
export interface Line {
  Id: number;
  StartMs: number;
  EndMs: number;
  /** The text, with a line feed between its rows. */
  Text: string;
  Style?: string;
  /** The speaker, where the track names one. */
  Name?: string;
  Kind: LineKind;
}

export interface Subtitles {
  Key: string;
  Format: string;
  Lines: Line[];
}
