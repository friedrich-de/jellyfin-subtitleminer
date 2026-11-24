/** One glossary of a word: which dictionary, and what it says (Yomitan's structured content as JSON, or a JSON array of strings). */
export interface Glossary {
  dictionary?: string;
  /** The same as `dictionary`, as LFG's JNI spells it. */
  dictName?: string;
  glossary: string;
  /** The definition's tags, space-separated ("v1 vt"). */
  definitionTags?: string;
  termTags?: string;
  /** The same tags as the dictionary's tag bank has them, which gives each its colour and what it stands for. */
  tags?: { name: string; category?: string; order?: number; score?: number; notes?: string }[];
}

export interface FrequencyGroup {
  dictionary?: string;
  dictName?: string;
  /** Under either name. */
  frequencies?: { value: number; displayValue?: string }[];
  values?: { value: number; display?: string }[];
}

export interface Pitch {
  /** The mora the pitch falls after; 0 when it doesn't fall. */
  position: number;
  /** Yomitan's other way of saying it ("LHL"). */
  pattern?: string;
  nasal?: number[];
  devoice?: number[];
}

export interface PitchGroup {
  dictionary?: string;
  dictName?: string;
  /** Each pitch as Hoshidicts gives it, under either name, or the positions alone. */
  pitches?: Pitch[];
  values?: Pitch[];
  pitchPositions?: number[];
  /** The word in IPA. */
  transcriptions?: string[];
}

/** One lookup result, as Hoshidicts gives it. */
export interface LookupResult {
  /** The text it was found as. */
  matched?: string;
  deinflected?: string;
  /** The inflections undone to get from `matched` to the word, under either name. */
  trace?: { name: string; description?: string }[];
  process?: { name: string; description?: string }[];
  term: {
    expression: string;
    reading?: string;
    /** The word's parts of speech, space-separated ("v1 vt"): a verb's or adjective's accent is 起伏. */
    rules?: string;
    glossaries: Glossary[];
    frequencies?: FrequencyGroup[];
    pitches?: PitchGroup[];
  };
}

/** Hachidori's palettes: its own ("default"), the system's light or dark ("auto"), and the rest of its catalogue. */
export type Theme =
  | "default" | "auto" | "miku" | "catppuccin-mocha" | "solarized-dark" | "dark" | "synthwave" | "halloween" | "forest" | "aqua" | "black" | "luxury"
  | "dracula" | "business" | "night" | "coffee" | "dim" | "sunset" | "abyss" | "girlypop" | "solarized-light" | "light" | "cupcake" | "bumblebee"
  | "emerald" | "corporate" | "retro" | "cyberpunk" | "valentine" | "garden" | "lofi" | "pastel" | "fantasy" | "wireframe" | "cmyk" | "autumn" | "acid"
  | "lemonade" | "winter" | "nord" | "caramellatte" | "silk" | "high-contrast";
export const THEMES: Theme[];

/** The Anki button: add it, it's being added, it's in Anki, it's in Anki and can be added again, Anki hasn't answered yet, it couldn't be added, cards can't be added. */
export type CardState = "add" | "adding" | "in-anki" | "duplicate" | "waiting" | "error" | "unavailable";
export const CARD_STATES: Readonly<Record<CardState, { state: string; icon: string; title: string; busy?: boolean }>>;

/** Hachidori's Design settings for an entry, by its own names. */
export interface Display {
  /** One average of a word's frequencies in place of each. */
  averageFrequency: boolean;
  showFrequencyTagsWithAverage: boolean;
  /** Each frequency's dictionary beside it. */
  showFrequencyDictionaryNames: boolean;
  /** 72,949 as 73K. */
  compactFrequencyNumbers: boolean;
  /** The pitch drawn on the headword's reading. */
  showPitchAccentFurigana: boolean;
  /** The pitch dictionary the headword's pitch comes from; "" for the first that has it. */
  pitchAccentFuriganaDictionary: string;
  pitchAccentFuriganaStyle: "contour" | "overline";
  /** The line of pitch accents under the frequencies, and what each shows. */
  showPitchAccentBadge: boolean;
  showPitchAccentDictionaryNames: boolean;
  showPitchAccentText: boolean;
  showPitchAccentPosition: boolean;
  showPitchAccentGraph: boolean;
  /** 平板, 頭高, 中高, 尾高 and 起伏 each in a colour. */
  showPitchAccentColors: boolean;
  /** Leaves the word's parts of speech off the entry (each definition still has its own). */
  hidePopupGrammarTags: boolean;
  /** "compact" puts a definition's parts on one line. */
  glossaryLayoutMode: "default" | "compact" | "compact-popup-anki";
  /** How many columns the dictionaries' cards are laid out in, 1 to 4. */
  popupColumns: number;
  /** The first few definitions under the headword. */
  showCompactDefinitionSummary: boolean;
  compactDefinitionSummaryCount: number;
  compactDefinitionSummaryDictionary: string;
}
export const DEFAULT_DISPLAY: Readonly<Display>;

/** What Hachidori knows of a dictionary. */
export interface DictionaryInfo {
  /** Its name in results. */
  title: string;
  /** The name to show for it. Without one, its title less its date or version. */
  displayName?: string;
  /** Whether its frequencies are ranks or counts. */
  frequencyMode?: "rank-based" | "occurrence-based" | null;
  /** A favourite has a tab of its own. */
  favorite?: boolean;
}

export interface Look {
  theme?: Theme;
  /** How solid the popup's background is, 0 to 100 (default 85). */
  opacity?: number;
  display?: Partial<Display>;
  dictionaries?: DictionaryInfo[];
  /** Dictionaries that share a tab. */
  tabGroups?: { id: string; name: string; dictionaries: string[] }[];
  /** Extra CSS for the popup. */
  css?: string;
}

export interface DictionaryStyle {
  dictionary?: string;
  dictName?: string;
  styles?: string;
  css?: string;
}

export interface RenderOptions {
  /** Each dictionary's own CSS, limited to its entries. */
  styles?: DictionaryStyle[];
  /** Each result's card state; "add" where it's left out. */
  states?: (CardState | undefined)[];
  /** Expressions already in Anki, as a shorter way of saying "in-anki". */
  added?: Iterable<string>;
}

type Soon<T> = T | Promise<T>;

export interface PopupOptions<R extends LookupResult = LookupResult> extends Look {
  /** How many results are drawn at once; the rest follow as the page is idle (default 1). */
  initialResultCount?: number;
  /** The Anki button of a word not added yet. Without it there's no Anki button. */
  onAddCard?: (result: R, index: number) => void;
  /** The Anki button of a word that's in Anki. */
  onOpenCard?: (result: R, index: number) => void;
  /** The audio button. Without it there's no audio button. */
  onPlayAudio?: (result: R, index: number) => void;
  /** Whether a word has audio to play (default: every word). */
  hasAudio?: (result: R) => boolean;
  /**
   * Looks up the words `text` starts with: for a word tapped in a definition,
   * a link to another entry and a kanji of the headword. What it finds is
   * shown in place of what's there, with Back to return. Without it
   * definitions aren't looked up.
   */
  lookup?: (text: string) => Soon<R[] | ({ results: R[] } & RenderOptions) | null | undefined>;
  /** A dictionary's picture: a URL, a Blob, or a promise of either. Without it pictures are left out. */
  media?: (dictionary: string, path: string) => Soon<string | Blob>;
  /** A link out of a definition (default: opens it in a new tab). */
  onExternalLink?: (url: string) => void;
  /** A close button at the top of the popup. Without it there's none. */
  onClose?: () => void;
}

export interface DictionaryPopup<R extends LookupResult = LookupResult> {
  /** The element the popup is in. It's as wide as you make it and as tall as its entries; give it a max-height and the definitions scroll inside. */
  element: HTMLDivElement;
  /** Shows results, the first at the top. With none, the popup is hidden. */
  render(results: R[], options?: RenderOptions): void;
  /** Shows a sentence in place of entries, as Hachidori says "No definition found." */
  notice(message: string): void;
  /** Sets one result's card state. `message` is what the button says of it, in place of the usual words. */
  setCardState(index: number, state: CardState, message?: string): void;
  setAdded(expressions: Iterable<string>): void;
  /** Moves to an entry, as Hachidori's next and previous entry keys do. */
  setCurrent(index: number): void;
  /** Goes back from a word looked up in a definition. False when there's nothing to go back to. */
  back(): boolean;
  setTheme(look: Look): void;
  /** Empties the popup, and hides it. */
  clear(): void;
  /** Takes the popup out of the page. */
  destroy(): void;
}

export function createDictionaryPopup<R extends LookupResult = LookupResult>(container: HTMLElement, options?: PopupOptions<R>): DictionaryPopup<R>;

/** A result as Hachidori's popup reads it: every list there, the inflections undone as its `trace`. */
export function toHachidori(result: LookupResult, index?: number): LookupResult & { trace: { name: string; description: string }[] };

/** Hachidori's own modules and stylesheets, for a host that wants more of them than the popup: render/popup.js, render/glossary.js, render/reader.css and icons.css. */
export const HDPopup: Record<string, any>;
export const HDGlossary: Record<string, any>;
export const READER_CSS: string;
export const ICONS_CSS: string;
/** What this package changes of Hachidori's stylesheet, for a popup that sits in a page's own element. */
export const EMBED_CSS: string;
