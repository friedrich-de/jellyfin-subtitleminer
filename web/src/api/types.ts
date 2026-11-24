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

/** Where a user's cards go. */
export type AnkiWhere = 'off' | 'device' | 'server';

export interface AnkiView {
  Where: AnkiWhere;
  DeviceAddress: string;
  /** The browser sends it, so the page gets it back. */
  DeviceKey: string;
  ServerAddress: string;
  /** Whether a key for the server's AnkiConnect is stored. The key itself stays on the server. */
  ServerKeySet: boolean;
  /** Whether this user may have the server send their cards. */
  ServerAllowed: boolean;
}

/** A user's settings, kept on the server so they follow the user from device to device. */
export interface SettingsView {
  Anki: AnkiView;
  /** What only the page reads, as it saved it. */
  Page?: unknown;
  /** Only administrators get it. */
  Everyone?: { AnkiThroughServer: boolean };
}

/** A change of the settings. What is left out stays as it is. */
export interface SettingsChange {
  Anki?: Partial<Omit<AnkiView, 'ServerKeySet' | 'ServerAllowed'>> & {
    /** Empty takes it away. */
    ServerKey?: string;
  };
  Page?: unknown;
}

/** A file the server put into the user's Anki. */
export interface StoredMedia {
  FileName: string;
}
