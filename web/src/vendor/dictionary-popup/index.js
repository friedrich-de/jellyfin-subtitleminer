// @ts-nocheck: plain JavaScript; src/index.d.ts says what it exports.
// dictionary-popup: Hachidori's dictionary popup for any web page. A looked-up
// word as Hachidori (bee-san/hachidori) shows it: the headword with its reading
// and pitch over it, small buttons to add its card and hear it, a tab for each
// dictionary, how common it is, its pitch accent, the inflections undone to
// find it, then each dictionary's definitions in that dictionary's own styles.
//
// The popup itself is Hachidori's own code and stylesheet, copied as they are
// into src/hachidori (scripts/sync-from-hachidori.mjs). This file is what the
// extension's content script is to them: it gives the popup an element with a
// shadow root, hands it results, puts the Anki button in, and tells the host
// when a button is pressed or a word in a definition is tapped.
// SPDX-License-Identifier: GPL-3.0-or-later
import "./hachidori/external-links.js";
import HDGlossary from "./hachidori/glossary.js";
import HDPopup from "./hachidori/popup.js";
import READER_CSS from "./hachidori/reader-css.js";
import ICONS_CSS from "./hachidori/icons-css.js";
import { EMBED_CSS } from "./embed-css.js";
import { customProperties, missing, olderDictionarySheet, olderSheet, plainColours, settle } from "./legacy.js";

export { HDGlossary, HDPopup, READER_CSS, ICONS_CSS, EMBED_CSS };

/** Hachidori's palettes: its own ("default"), the system's light or dark ("auto"), and the rest of its catalogue. */
export const THEMES = [
  "default", "auto", "miku", "catppuccin-mocha", "solarized-dark", "dark", "synthwave", "halloween", "forest", "aqua", "black", "luxury",
  "dracula", "business", "night", "coffee", "dim", "sunset", "abyss", "girlypop", "solarized-light", "light", "cupcake", "bumblebee",
  "emerald", "corporate", "retro", "cyberpunk", "valentine", "garden", "lofi", "pastel", "fantasy", "wireframe", "cmyk", "autumn", "acid",
  "lemonade", "winter", "nord", "caramellatte", "silk", "high-contrast",
];

/** How Hachidori shows an entry until its Design settings are changed (reader-options.js). */
export const DEFAULT_DISPLAY = Object.freeze({
  averageFrequency: false,
  showFrequencyTagsWithAverage: false,
  showFrequencyDictionaryNames: false,
  compactFrequencyNumbers: false,
  showPitchAccentFurigana: true,
  pitchAccentFuriganaDictionary: "",
  pitchAccentFuriganaStyle: "contour",
  showPitchAccentBadge: true,
  showPitchAccentDictionaryNames: true,
  showPitchAccentText: true,
  showPitchAccentPosition: true,
  showPitchAccentGraph: false,
  showPitchAccentColors: false,
  hidePopupGrammarTags: true,
  glossaryLayoutMode: "default",
  popupColumns: 1,
  showCompactDefinitionSummary: false,
  compactDefinitionSummaryCount: 3,
  compactDefinitionSummaryDictionary: "",
});

/**
 * The Anki button's states: the name a host says, then Hachidori's own state
 * (which picks the icon and colour), what the button says, and whether it's
 * waiting on something.
 */
export const CARD_STATES = Object.freeze({
  add: { state: "ready", icon: "add", title: "Mine to Anki" },
  adding: { state: "mining", icon: "arrow-sync", title: "Adding note", busy: true },
  "in-anki": { state: "success", icon: "book-search", title: "Note added" },
  duplicate: { state: "add-duplicate", icon: "document-add", title: "Add duplicate to Anki" },
  waiting: { state: "checking", icon: "arrow-clockwise", title: "Checking Anki card status", busy: true },
  error: { state: "error", icon: "error-circle", title: "Could not add note" },
  unavailable: { state: "unavailable", icon: "subtract", title: "Anki mining is unavailable" },
});

/** How many popups have been made, so each one's ids are its own. */
let made = 0;

/** How much of a definition is looked up from the character that was tapped. */
const NESTED_LOOKUP_LENGTH = 40;

/** Which of the results it was given a result the popup hands back is: the popup copies a result to show one dictionary's part of it. */
const INDEX = Symbol("dictionary-popup result");

const nameOf = (group) => String(group.dictionary ?? group.dictName ?? "");

/**
 * A result as Hachidori's popup reads it, which is as Hachidori's engine gives
 * it: the inflections undone are its `trace`, and every list is there, empty
 * or not. LFG's spellings (dictName, values, pitchPositions) are taken too.
 */
export function toHachidori(result, index = 0) {
  const term = result.term ?? {};
  return {
    ...result,
    [INDEX]: index,
    matched: String(result.matched ?? term.expression ?? ""),
    deinflected: String(result.deinflected ?? term.expression ?? ""),
    trace: (result.trace ?? result.process ?? []).map((step) => ({ name: String(step.name ?? ""), description: String(step.description ?? "") })),
    term: {
      ...term,
      expression: String(term.expression ?? ""),
      reading: String(term.reading ?? ""),
      rules: String(term.rules ?? ""),
      glossaries: (term.glossaries ?? []).map((g) => ({ ...g, dictionary: nameOf(g), glossary: String(g.glossary ?? ""), definitionTags: String(g.definitionTags ?? ""), termTags: String(g.termTags ?? "") })),
      frequencies: (term.frequencies ?? []).map((group) => ({
        ...group,
        dictionary: nameOf(group),
        frequencies: (group.frequencies ?? group.values ?? []).map((f) => ({ ...f, value: Number(f.value), displayValue: String(f.displayValue ?? f.display ?? f.value) })),
      })),
      pitches: (term.pitches ?? []).map((group) => ({
        ...group,
        dictionary: nameOf(group),
        transcriptions: group.transcriptions ?? [],
        pitches: (group.pitches ?? group.values ?? (group.pitchPositions ?? []).map((position) => ({ position }))).map((pitch) => ({
          ...pitch,
          position: Number(pitch.position) || 0,
          pattern: String(pitch.pattern ?? ""),
          nasal: pitch.nasal ?? [],
          devoice: pitch.devoice ?? [],
        })),
      })),
    },
  };
}

/** The text from a point in a definition to the end of its block, furigana left out, and no longer than a lookup needs. */
function textFrom(node, offset, within) {
  const block = (n) => {
    for (let e = n.parentElement; e && e !== within; e = e.parentElement) {
      const display = e.ownerDocument.defaultView.getComputedStyle(e).display;
      if (!display.startsWith("inline") && display !== "ruby" && display !== "ruby-base" && display !== "contents") return e;
    }
    return within;
  };
  const home = block(node);
  const walker = node.ownerDocument.createTreeWalker(home, 4 /* NodeFilter.SHOW_TEXT */, {
    acceptNode: (n) => (n.parentElement?.closest("rt, rp") ? 2 /* FILTER_REJECT */ : 1 /* FILTER_ACCEPT */),
  });
  walker.currentNode = node;
  let text = node.data.slice(offset);
  while (text.length < NESTED_LOOKUP_LENGTH) {
    const next = walker.nextNode();
    if (!next || block(next) !== home) break;
    text += next.data;
  }
  return text.slice(0, NESTED_LOOKUP_LENGTH);
}

/** The text node and offset of the character at a point inside a shadow root. */
function characterAt(root, x, y) {
  const document = root.ownerDocument ?? root.host.ownerDocument;
  const range = document.createRange();
  const holds = (node, at) => {
    if (at < 0 || at >= node.data.length) return false;
    range.setStart(node, at);
    range.setEnd(node, at + 1);
    return [...range.getClientRects()].some((box) => x >= box.left && x <= box.right && y >= box.top && y <= box.bottom);
  };
  let node = null;
  let offset = 0;
  try {
    const caret = document.caretPositionFromPoint?.(x, y, { shadowRoots: [root] });
    if (caret) [node, offset] = [caret.offsetNode, caret.offset];
  } catch {
    // An older browser's caretPositionFromPoint takes no shadow roots.
  }
  if (node?.nodeType === 3 && node.getRootNode() === root) {
    // The caret falls between characters: a tap on a character's right half is still that character.
    if (holds(node, offset)) return { node, offset };
    if (holds(node, offset - 1)) return { node, offset: offset - 1 };
    return null;
  }
  // Before Chrome 128 nothing gives a caret inside a shadow root: find the
  // element there, and which character of its text the point is on.
  const element = root.elementFromPoint?.(x, y);
  if (!element) return null;
  const walker = document.createTreeWalker(element, 4 /* NodeFilter.SHOW_TEXT */);
  for (let text = walker.nextNode(); text; text = walker.nextNode()) {
    range.selectNodeContents(text);
    const box = range.getBoundingClientRect();
    if (y < box.top || y > box.bottom || x < box.left || x > box.right) continue;
    for (let at = 0; at < text.data.length; at += 1) if (holds(text, at)) return { node: text, offset: at };
  }
  return null;
}

/**
 * Puts Hachidori's dictionary popup in `container` and returns its handle.
 *
 * Options:
 *   theme       one of THEMES: "default" (Hachidori's own, dark), "auto", "light", "dark" …
 *   opacity     how solid the popup's background is, 0 to 100 (default 85, as Hachidori's).
 *   display     Hachidori's Design settings for an entry, over DEFAULT_DISPLAY.
 *   dictionaries  [{title, displayName, frequencyMode}], what Hachidori knows of each dictionary: the name to show
 *               for it and whether its frequencies are ranks ("rank-based") or counts ("occurrence-based").
 *   tabGroups   [{id, name, dictionaries: [title]}], dictionaries that share a tab.
 *   css         extra CSS for the popup.
 *   initialResultCount  how many results are drawn at once; the rest follow as the page is idle (default 1).
 *   onAddCard(result, index)     the Anki button of a word not added yet. Without it there's no Anki button.
 *   onOpenCard(result, index)    the Anki button of a word that's in Anki.
 *   onPlayAudio(result, index, source)  the audio button. Without it there's no audio button. `source` is the
 *                                sound chosen from audioSources, or undefined for a plain press.
 *   hasAudio(result)             whether a word has audio to play (default: every word).
 *   audioSources(result)         (Subtitle Miner) the names of a word's sounds, or a promise of them. With it, holding
 *                                the audio button (or right-click, Shift-click, Down) lists them to choose one, as in
 *                                Yomitan.
 *   chosenAudio(result)          (Subtitle Miner) the name of the sound chosen for a word, marked in that list.
 *   scale                        (Subtitle Miner) the size of everything in the popup, in percent (default 100).
 *   lookup(text)                 looks up the words `text` starts with, for a word tapped in a definition, a link
 *                                to another entry and a kanji of the headword: results, or {results, styles}, or
 *                                a promise of either. Without it definitions aren't looked up.
 *   media(dictionary, path)      a dictionary's picture: a URL, a Blob, or a promise of either. Without it
 *                                pictures are left out.
 *   onExternalLink(url)          a link out of a definition (default: opens it in a new tab).
 *   onClose()                    a close button at the top of the popup. Without it there's none.
 */
export function createDictionaryPopup(container, options = {}) {
  options = { ...options };
  const document = container.ownerDocument;
  const window = document.defaultView;
  const host = document.createElement("div");
  host.className = "dictionary-popup";
  const root = host.attachShadow({ mode: "open" });
  // Subtitle Miner: constructed stylesheets in place of <style> elements, here and in
  // glossary.js's applyDictionaryStyles, since a page whose policy has no 'unsafe-inline'
  // for styles refuses <style> elements, also inside a shadow root.
  const base = new window.CSSStyleSheet();
  // What this browser can't read of the stylesheets (legacy.js): nothing, on a current one.
  const lacks = missing(window);
  const baseText = lacks.colours ? olderSheet(`${READER_CSS}\n${ICONS_CSS}\n${EMBED_CSS}`) : `${READER_CSS}\n${ICONS_CSS}\n${EMBED_CSS}`;
  base.replaceSync(baseText);
  const coloured = lacks.colours ? customProperties(baseText) : [];
  let settled = [];
  /** The dictionaries' styles last given, and what they were scoped from. */
  let lastStyles = null;
  let styled = null;
  let generation = 0;
  const custom = new window.CSSStyleSheet();
  const popup = document.createElement("div");
  popup.className = "gsm-hoshidicts-popup";
  popup.hidden = true;
  root.adoptedStyleSheets = [base, custom];
  root.append(popup);
  container.appendChild(host);

  const appearance = HDPopup.createPopupAppearance(host);
  const display = () => ({ ...DEFAULT_DISPLAY, ...options.display });
  // Subtitle Miner: the popup's size, as Hachidori's popup scale (embed-css.js zooms by it).
  const scale = () => (Number.isFinite(options.scale) ? Math.min(200, Math.max(50, options.scale)) : 100);

  function applyLook() {
    appearance.update({
      ...display(),
      popupTheme: THEMES.includes(options.theme) ? options.theme : "default",
      popupOpacityPercent: Number.isFinite(options.opacity) ? Math.min(100, Math.max(0, options.opacity)) : 85,
      // The popup is as wide as its element and as tall as its entries (embed-css.js).
      popupWidthPx: 560,
      popupHeightPx: 420,
      popupScalePercent: scale(),
      showPopupAudioButton: typeof options.onPlayAudio === "function",
      // Hachidori's own notes, a dictionary of the reader's, aren't part of this.
      personalDictionaryEnabled: false,
    });
    custom.replaceSync(options.css ?? "");
    settleColours();
  }

  /** On an older browser, works out the palette's colours for it. They hang on the element being in the page. */
  function settleColours() {
    if (!lacks.colours || !host.isConnected) return;
    settled = settle(popup, coloured, settled);
    // A dictionary's stylesheet may use the palette's colours too.
    if (lastStyles) applyStyles(lastStyles);
  }
  applyLook();

  /** What's shown, and what was shown before each word tapped in a definition. */
  let shown = null;
  const history = [];
  let asked = 0;
  /** Which result each audio button plays. */
  const heard = new WeakMap();
  const pictures = new Map();

  const view = HDPopup.createPopupView({
    document,
    window,
    popup,
    appendExpressionRuby: HDGlossary.appendExpressionRuby,
    createPronunciationPitchAccent: HDGlossary.createPronunciationPitchAccent,
    appendTextOnlyGlossary: HDGlossary.appendTextOnlyGlossary,
    appendStructuredImage: HDGlossary.appendStructuredImage,
    parseTagList: HDGlossary.parseTagList,
    getPopupColumns: () => display().popupColumns,
    getImageHoverPreview: () => "off",
    getPopupScalePercent: scale,
    initialResultCount: Number.isInteger(options.initialResultCount) ? options.initialResultCount : undefined,
    idPrefix: `dictionary-popup-${(made += 1)}`,
    positionPopup() {},
    sourceHighlightEnabled: false,
    onKanjiClick: (character) => void follow(character),
    onResultsRendered: bindActions,
    onResultsExpanded: bindActions,
  });

  /** The Anki button of one result, as Hachidori's content script draws it. */
  function paintCard(button, name) {
    const said = CARD_STATES[name] ?? CARD_STATES.add;
    const opens = name === "in-anki";
    button.dataset.state = said.state;
    button.dataset.cardState = name;
    button.dataset.action = opens ? "view" : "add";
    button.title = opens && typeof options.onOpenCard === "function" ? "View note in Anki" : said.title;
    button.setAttribute("aria-label", button.title);
    button.setAttribute("aria-busy", String(said.busy === true));
    button.disabled = said.busy === true || name === "unavailable" || (opens && typeof options.onOpenCard !== "function");
    const icon = document.createElement("span");
    icon.className = "gsm-hoshidicts-mine-icon hd-icon";
    icon.setAttribute("aria-hidden", "true");
    icon.dataset.icon = said.icon;
    button.replaceChildren(icon);
  }

  function cardButtons() {
    return [...popup.querySelectorAll(".gsm-hoshidicts-mine-button[data-result-index]")];
  }

  /**
   * Subtitle Miner: the list of a word's sounds, as Yomitan shows it when its audio button is held: an overlay below
   * the button, in Hachidori's styles for it. Choosing one plays it, and the page keeps it for the word's card.
   */
  let menu = null;

  function closeAudioMenu() {
    if (!menu) return false;
    menu.element.remove();
    menu.button.setAttribute("aria-expanded", "false");
    menu = null;
    return true;
  }

  async function openAudioMenu(button, result, at) {
    closeAudioMenu();
    const element = document.createElement("div");
    element.className = "gsm-hoshidicts-audio-menu";
    element.setAttribute("role", "dialog");
    element.setAttribute("aria-label", `Sounds of ${result.term?.expression ?? ""}`);
    const heading = document.createElement("p");
    heading.className = "gsm-hoshidicts-audio-menu-heading";
    heading.textContent = "Sounds";
    const status = document.createElement("p");
    status.className = "gsm-hoshidicts-audio-menu-status";
    status.setAttribute("role", "status");
    status.textContent = "Looking for sounds…";
    element.append(heading, status);
    popup.append(element);
    const mine = { element, button };
    menu = mine;
    button.setAttribute("aria-expanded", "true");
    // The popup is zoomed by its scale: lengths inside it are its own, the page's rectangles are not.
    const factor = 100 / scale();
    const place = () => {
      const [box, from] = [popup.getBoundingClientRect(), button.getBoundingClientRect()];
      element.style.top = `${Math.max(6, (from.bottom - box.top) * factor + 4)}px`;
      element.style.right = `${Math.max(6, (box.right - from.right) * factor)}px`;
    };
    place();
    let names;
    try {
      names = await options.audioSources(result);
    } catch (error) {
      if (menu === mine) status.textContent = error instanceof Error && error.message ? error.message : "The sounds could not be listed.";
      return;
    }
    if (menu !== mine) return;
    if (!Array.isArray(names) || names.length === 0) {
      status.textContent = "The audio source has no sounds for this word.";
      return;
    }
    status.remove();
    const chosen = options.chosenAudio?.(result);
    for (const name of names) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "gsm-hoshidicts-audio-menu-item";
      item.textContent = name === chosen ? `✓ ${name}` : name;
      if (name === chosen) item.setAttribute("aria-current", "true");
      item.addEventListener("click", () => {
        closeAudioMenu();
        button.focus({ preventScroll: true });
        options.onPlayAudio?.(result, at, name);
      });
      element.append(item);
    }
    place();
    (element.querySelector("[aria-current]") ?? element.querySelector("button"))?.focus({ preventScroll: true });
  }

  // A press anywhere else in the popup closes the list, and Escape closes it before the popup.
  root.addEventListener("pointerdown", (event) => {
    if (menu && !menu.element.contains(event.target) && !menu.button.contains(event.target)) closeAudioMenu();
  });
  root.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && menu) {
      const button = menu.button;
      closeAudioMenu();
      button.focus({ preventScroll: true });
      event.stopPropagation();
    }
  });

  /** Puts each result's Anki button in, and listens to its audio button. The popup calls this as it draws results. */
  function bindActions({ audioButtons = [], miningActions = [] }) {
    if (!shown) return;
    for (const item of miningActions) {
      const index = item.result?.[INDEX];
      if (!Number.isInteger(index) || typeof options.onAddCard !== "function" || !item.actions) continue;
      // The first result's row is kept from one drawing to the next, with its button.
      let button = item.actions.querySelector(":scope > .gsm-hoshidicts-mine-button");
      if (!button) {
        button = document.createElement("button");
        button.type = "button";
        button.className = "gsm-hoshidicts-mine-button";
        button.addEventListener("click", () => {
          const at = Number(button.dataset.resultIndex);
          const [state, result] = [shown?.states[at], shown?.given[at]];
          if (!result) return;
          if (state === "in-anki") options.onOpenCard?.(result, at);
          else if (state === "add" || state === "duplicate" || state === "error") options.onAddCard?.(result, at);
        });
        const leading = item.actions.firstElementChild;
        if (leading?.matches(".gsm-hoshidicts-popup-close, .gsm-hoshidicts-kanji-back")) leading.after(button);
        else item.actions.prepend(button);
      }
      if (button.dataset.resultIndex !== String(index) || button.dataset.cardState !== shown.states[index]) {
        button.dataset.resultIndex = String(index);
        paintCard(button, shown.states[index]);
      }
    }
    for (const item of audioButtons) {
      const index = item.result?.[INDEX];
      if (!Number.isInteger(index) || !item.button) continue;
      const control = item.button.closest(".gsm-hoshidicts-audio-control");
      if (control) control.hidden = typeof options.hasAudio === "function" && !options.hasAudio(shown.given[index]);
      if (heard.has(item.button)) {
        heard.set(item.button, index);
        continue;
      }
      heard.set(item.button, index);
      const button = item.button;
      const choosing = typeof options.audioSources === "function";
      // Hachidori's button also opens a list of pronunciations: here only with audioSources, as Yomitan's does.
      button.title = choosing ? "Play pronunciation; hold, right-click or Shift-click for other sounds" : "Play pronunciation";
      if (!choosing) {
        button.removeAttribute("aria-haspopup");
        button.removeAttribute("aria-expanded");
      }
      const open = () => {
        const at = heard.get(button);
        if (choosing && shown?.given[at]) void openAudioMenu(button, shown.given[at], at);
      };
      // A hold opens the list; the click that ends it then plays nothing.
      let holding = 0;
      let opened = false;
      button.addEventListener("pointerdown", (event) => {
        opened = false;
        window.clearTimeout(holding);
        if (!choosing || event.button !== 0) return;
        holding = window.setTimeout(() => {
          opened = true;
          open();
        }, 500);
      });
      for (const ended of ["pointerup", "pointerleave", "pointercancel"]) {
        button.addEventListener(ended, () => window.clearTimeout(holding));
      }
      button.addEventListener("contextmenu", (event) => {
        if (!choosing) return;
        event.preventDefault();
        window.clearTimeout(holding);
        if (!opened) {
          opened = true;
          open();
        }
      });
      button.addEventListener("keydown", (event) => {
        if (choosing && event.key === "ArrowDown") {
          event.preventDefault();
          open();
        }
      });
      button.addEventListener("click", (event) => {
        if (opened) {
          opened = false;
          return;
        }
        if (choosing && event.shiftKey) {
          open();
          return;
        }
        closeAudioMenu();
        const at = heard.get(button);
        if (shown?.given[at]) options.onPlayAudio?.(shown.given[at], at);
      });
    }
  }

  function applyStyles(styles) {
    lastStyles = styles;
    let list = (styles ?? []).map((s) => ({ dictionary: s.dictionary ?? s.dictName ?? "", styles: s.styles ?? s.css ?? "" }));
    const older = lacks.colours || lacks.nesting;
    // On an older browser a stylesheet's colours are worked out from the palette, so they change with it.
    const key = (older ? `${host.dataset.hoshidictsTheme}\u0002` : "") + list.map((s) => `${s.dictionary}\u0000${s.styles}`).join("\u0001");
    // Scoping a stylesheet is most of the time a lookup takes to show, and stylesheets rarely change.
    if (key === styled) return;
    styled = key;
    if (!older) {
      HDGlossary.applyDictionaryStyles(document, root, (generation += 1), list);
      return;
    }
    const computed = window.getComputedStyle(popup);
    const scheme = computed.getPropertyValue("--hoshidicts-palette-color-scheme").trim() === "light" ? "light" : "dark";
    const lookup = (name) => computed.getPropertyValue(name);
    // Before Hachidori reads a stylesheet, what the browser would drop from it is written plainly; and after, the
    // colours Hachidori wraps in color-mix() to keep them colours (the popup's own --text-color and the like) are too.
    list = list.map((s) => ({ ...s, styles: olderDictionarySheet(s.styles, { scheme, lookup, ...lacks }) }));
    const applied = HDGlossary.applyDictionaryStyles(document, root, (generation += 1), list);
    if (lacks.colours) for (const style of applied) style.textContent = plainColours(style.textContent, scheme, lookup);
  }

  /** A dictionary's picture as a blob: or data: URL, the only kinds the popup shows. */
  async function picture({ dictionary, path }) {
    const key = `${dictionary}\u0000${path}`;
    if (!pictures.has(key)) {
      const asked = Promise.resolve(options.media(dictionary, path)).then(async (found) => {
        if (found instanceof window.Blob) return window.URL.createObjectURL(found);
        if (typeof found !== "string" || !found) throw new Error("dictionary image is unavailable");
        if (/^(?:blob|data):/u.test(found)) return found;
        const response = await window.fetch(found);
        if (!response.ok) throw new Error("dictionary image is unavailable");
        return window.URL.createObjectURL(await response.blob());
      });
      pictures.set(key, asked);
      // Subtitle Miner: a picture that could not be had is asked for again the next time it is drawn.
      asked.catch(() => {
        if (pictures.get(key) === asked) pictures.delete(key);
      });
    }
    return pictures.get(key);
  }

  function draw(restore = null) {
    closeAudioMenu();
    const { results, styles } = shown;
    settleColours();
    if (styles) applyStyles(styles);
    popup.hidden = false;
    const first = results[0];
    view.renderResults(results, { query: first.matched ?? first.term.expression, sentence: "", matchOffset: 0, sourceElements: [], sourceText: "", sourceOffset: 0 }, {
      ...HDPopup.metadataOptions(display()),
      showCompactDefinitionSummary: display().showCompactDefinitionSummary,
      compactDefinitionSummaryCount: display().compactDefinitionSummaryCount,
      compactDefinitionSummaryDictionary: display().compactDefinitionSummaryDictionary,
      dictionaryPresentation: options.dictionaries ?? [],
      dictionaryTabGroups: options.tabGroups ?? [],
      generation,
      resolveMedia: typeof options.media === "function" ? picture : undefined,
      onInternalLink: ({ query }) => void follow(query),
      onExternalLink: ({ url }) => (typeof options.onExternalLink === "function" ? options.onExternalLink(url) : window.open(url, "_blank", "noopener")),
      onBack: history.length ? back : undefined,
      onClose: !history.length && typeof options.onClose === "function" ? () => options.onClose() : undefined,
      onDictionaryTabSelected(selection) {
        if (shown) shown.tab = selection;
      },
      selectedDictionaryTab: restore?.tab,
      expandAll: restore?.expandAll,
      restoreScrollTop: restore?.restoreScrollTop,
      disclosures: restore?.disclosures,
    });
  }

  function show(results, { styles, states = [], added = [] } = {}) {
    const given = [...results];
    const inAnki = new Set(added);
    shown = {
      given,
      results: given.map(toHachidori),
      styles,
      states: given.map((result, i) => (states[i] in CARD_STATES ? states[i] : inAnki.has(result.term?.expression) ? "in-anki" : "add")),
      tab: null,
    };
    draw();
  }

  /** Looks up a word of a definition, and shows it in place of what's there, with Back to return. */
  async function follow(text) {
    if (typeof options.lookup !== "function" || !shown || !text) return;
    const [from, mine] = [shown, (asked += 1)];
    let found;
    try {
      found = await options.lookup(text);
    } catch (error) {
      console.warn("dictionary-popup: a word in a definition could not be looked up", error);
      return;
    }
    const results = Array.isArray(found) ? found : found?.results;
    if (mine !== asked || shown !== from || !Array.isArray(results) || !results.length) return;
    history.push({ ...from, restore: { ...view.captureTermView(), tab: from.tab } });
    show(results, { styles: found?.styles, states: found?.states, added: found?.added });
    popup.querySelector(".gsm-hoshidicts-kanji-back")?.focus({ preventScroll: true });
  }

  function back() {
    const last = history.pop();
    if (!last) return false;
    asked += 1;
    const { restore, ...was } = last;
    shown = was;
    draw(restore);
    return true;
  }

  // A tap on a word in a definition looks it up, as a click or hover does in Hachidori.
  view.scrollElement.addEventListener("click", (event) => {
    if (typeof options.lookup !== "function" || event.defaultPrevented || event.button !== 0) return;
    const target = event.target;
    if (!(target instanceof window.Element) || !target.closest(".gsm-hoshidicts-glossary-content")) return;
    if (target.closest("a, button, summary, input, textarea, select, img, .gloss-image-link")) return;
    if (!root.getSelection?.()?.isCollapsed && root.getSelection?.()) return;
    const at = characterAt(root, event.clientX, event.clientY);
    if (!at) return;
    const text = textFrom(at.node, at.offset, view.scrollElement).trimEnd();
    if (text && !/^[\s\p{P}\p{S}]/u.test(text)) void follow(text);
  });

  return {
    /** The element the popup is in. It's as wide as you make it and as tall as its entries; give it a max-height and the definitions scroll inside. */
    element: host,

    /**
     * Shows `results` as Hoshidicts gives them, the first at the top.
     *   styles   [{dictionary, styles}], each dictionary's own CSS, limited to its entries.
     *   states   each result's card state ("add", "adding", "in-anki", "duplicate", "waiting", "error", "unavailable"); "add" where it's left out.
     *   added    expressions already in Anki, as a shorter way of saying "in-anki".
     */
    render(results, given = {}) {
      history.length = 0;
      asked += 1;
      if (!results?.length) {
        this.clear();
        if (given.styles) applyStyles(given.styles);
        return;
      }
      show(results, given);
    },

    /** Shows a sentence in place of entries, as Hachidori says "No definition found." */
    notice(message) {
      history.length = 0;
      asked += 1;
      shown = null;
      popup.hidden = false;
      view.renderNotice(String(message), null, {});
    },

    /** Sets one result's card state. `message` is what the button says of it, in place of the usual words. */
    setCardState(index, state, message = "") {
      if (!shown || index < 0 || index >= shown.states.length || !(state in CARD_STATES)) return;
      shown.states[index] = state;
      for (const button of cardButtons()) {
        if (Number(button.dataset.resultIndex) !== index) continue;
        paintCard(button, state);
        if (message) {
          button.title = message;
          button.setAttribute("aria-label", message);
        }
      }
    },

    /**
     * (Subtitle Miner) Sets the card state of a result it was given, by the result rather than its place:
     * on screen only while it is shown, and kept for Back. Nothing happens once it is gone.
     */
    setResultState(result, state, message = "") {
      if (!(state in CARD_STATES)) return;
      for (const entry of history) {
        entry.given.forEach((given, i) => {
          if (given === result) entry.states[i] = state;
        });
      }
      shown?.given.forEach((given, i) => {
        if (given === result) this.setCardState(i, state, message);
      });
    },

    /** Marks these expressions as in Anki. */
    setAdded(expressions) {
      if (!shown) return;
      const inAnki = new Set(expressions);
      shown.given.forEach((result, i) => {
        if (inAnki.has(result.term?.expression)) this.setCardState(i, "in-anki");
      });
    },

    /** Moves to an entry of those shown, as Hachidori's next and previous entry keys do. */
    setCurrent(index) {
      if (!shown || index < 0 || index >= shown.results.length) return;
      if (index === 0) view.focusEntry("first");
      else view.focusEntry({ offset: index - view.currentEntryIndex() });
    },

    /** Goes back from a word looked up in a definition, or closes the list of sounds. False when there's nothing to go back to. */
    back: () => closeAudioMenu() || back(),

    /** Changes the theme, opacity, display settings, dictionaries' names or extra CSS. */
    setTheme(change) {
      Object.assign(options, change);
      applyLook();
      if (shown && (change.display || change.dictionaries || change.tabGroups)) draw({ ...view.captureTermView(), tab: shown.tab });
    },

    /** Empties the popup, and hides it. */
    clear() {
      closeAudioMenu();
      history.length = 0;
      asked += 1;
      shown = null;
      view.clear();
      popup.hidden = true;
    },

    /** Takes the popup out of the page. */
    destroy() {
      asked += 1;
      shown = null;
      view.destroy();
      appearance.destroy();
      for (const url of pictures.values()) url.then((u) => u.startsWith("blob:") && window.URL.revokeObjectURL(u)).catch(() => {});
      pictures.clear();
      host.remove();
    },
  };
}
