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

/** A dictionary on the server. */
export interface DictionaryView {
  /** The name the calls that change it use. */
  Id: string;
  /** Its name in lookups. */
  Title: string;
  Revision: string;
  /** Whether lookups use it. */
  Enabled: boolean;
  Terms: number;
  Frequencies: number;
  /** Pitch accents and pronunciations. */
  Pitches: number;
  FrequencyMode?: 'rank-based' | 'occurrence-based';
  Author?: string;
  Url?: string;
  Attribution?: string;
  /** Where a newer revision can be downloaded. */
  DownloadUrl?: string;
  /** Its stylesheet, which the popup keeps to its definitions. */
  Styles: string;
  /** The size of its file on the server. */
  Bytes: number;
  Imported: string;
}

export type ImportState = 'idle' | 'downloading' | 'importing' | 'done' | 'failed';

/** What the import that runs, or ran last, does, and what waits behind it. */
export interface DictionaryImport {
  State: ImportState;
  /** A file's name or an address. */
  Source?: string;
  /** From 0 to 1. */
  Progress: number;
  Title?: string;
  Error?: string;
  /** How many more dictionaries wait to be imported after this one. */
  Waiting: number;
  /** What the last imports came to, the oldest first. */
  Finished: DictionaryImportOutcome[];
}

/** What an import came to: the dictionary's title when it went in, or why it did not. */
export interface DictionaryImportOutcome {
  /** Counted up from 1 since the server started: a page tells by it which ones it has not shown. */
  Number: number;
  /** A file's name or an address. */
  Source: string;
  Title?: string;
  Error?: string;
}

export interface DictionariesView {
  /** In the order lookups read them. */
  Dictionaries: DictionaryView[];
  Import: DictionaryImport;
  /** Whether the user may add, arrange and remove them: administrators may. */
  MayManage: boolean;
  /** Where words' sounds come from. Missing from a plugin older than 2.0. */
  WordAudio?: WordAudioView;
}

/** Where words' sounds come from. Its address stays on the server: it often holds a key. */
export interface WordAudioView {
  /** Whether an administrator set an audio source. */
  Set: boolean;
  /** The source's host, for administrators only. */
  Host?: string;
}

/** The sounds the audio source has for a word, by name; their addresses stay on the server. */
export interface WordAudioSourcesView {
  Sources: string[];
}

/** The audio source as an administrator set it, and what trying it with 食べる found. */
export interface WordAudioSaved {
  WordAudio: WordAudioView;
  Tried: 'found' | 'notFound' | 'failed' | 'none';
}

/**
 * What a lookup finds, as the dictionary popup reads it. Unlike the plugin's other answers, its
 * names start small.
 */
export interface LookupResult {
  /** The part of the text the word was found as. */
  matched: string;
  deinflected: string;
  /** The inflections undone, in the order they were undone. */
  trace: { name: string; description?: string }[];
  term: {
    expression: string;
    reading: string;
    /** Its parts of speech, with spaces between them ("v1 vt"). */
    rules: string;
    glossaries: {
      dictionary: string;
      /** Yomitan's glossary of one definition, as JSON. */
      glossary: string;
      definitionTags: string;
      termTags: string;
      tags: { name: string; category: string; order: number; notes: string; score: number }[];
    }[];
    frequencies: {
      dictionary: string;
      frequencies: { value: number; displayValue?: string }[];
    }[];
    pitches: {
      dictionary: string;
      pitches: { position: number; pattern?: string; nasal?: number[]; devoice?: number[] }[];
      transcriptions?: string[];
    }[];
  };
}

export interface Lookup {
  results: LookupResult[];
}
