// What Hachidori's popup needs changed to sit in a page's own element instead
// of floating over a page beside the word: in the extension it's a fixed box of
// a set size that the content script places. Here it's as wide as its element
// and as tall as its entries, and no taller than the element may be.
// SPDX-License-Identifier: GPL-3.0-or-later
export const EMBED_CSS = `
:host {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}
:host([hidden]) {
  display: none;
}
.gsm-hoshidicts-popup {
  position: relative;
  z-index: auto;
  /* Subtitle Miner: the popup's own scale (the scale option), not the page's zoom. */
  zoom: var(--gsm-hoshidicts-popup-scale, 100%);
  flex: 0 1 auto;
  width: 100%;
  height: auto;
  min-height: 0;
}
/* Subtitle Miner: holding the audio button opens its list of sounds, not the browser's menu or a selection. */
.gsm-hoshidicts-audio-button {
  -webkit-touch-callout: none;
  -webkit-user-select: none;
  user-select: none;
}
/* The definitions give way before the headword does. */
.gsm-hoshidicts-popup > .gsm-hoshidicts-result-chrome {
  max-height: none;
  flex-shrink: 1;
}
.gsm-hoshidicts-popup > .gsm-hoshidicts-content-scroll {
  flex: 0 1000 auto;
}
/* A notice's header holds only the Note button, which isn't here: without it the header is an empty band. */
.gsm-hoshidicts-result-chrome:has(> .gsm-hoshidicts-primary-header > .gsm-hoshidicts-entry-actions:only-child) {
  display: none;
}
`;
