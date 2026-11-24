# dictionary-popup, as the page carries it

These files are [dictionary-popup](https://github.com/bee-san/dictionary-popup) at commit
`5704fceaa9712a2f57d2d837222d1b30abf6557b` (version 0.3.1, 2026-11-22): its `src/` folder and its
`LICENSE`. It is [Hachidori](https://github.com/bee-san/hachidori)'s dictionary popup; `hachidori/`
holds Hachidori's own files at the commit `hachidori/source.json` names, and
`hachidori/ATTRIBUTION.md` says whose work they contain. Everything here is under the
GPL-3.0-or-later (`LICENSE`); the icons are Fluent UI System Icons under the MIT licence
(`hachidori/LICENSE.fluent-icons`).

The package is not on npm, and its README suggests copying `src/` into a project, which this is.

## What the page changed

The page's content policy allows styles only from its own files (`style-src 'self'`), and a browser
refuses `<style>` elements under it, also inside a shadow root. So the popup's stylesheets are
constructed (`new CSSStyleSheet()`, `replaceSync`) and adopted by the popup's shadow root instead:

- `index.js`, `createDictionaryPopup`: the base stylesheet and the extra CSS (`base` and `custom`).
- `hachidori/glossary.js`, `applyDictionaryStyles`: each dictionary's own stylesheet. It keeps the
  sheets it adopted for each root and swaps them on the next call, as it removed its elements
  before; each entry it returns has a `textContent` that re-parses its sheet when set, as
  `index.js` does on older browsers.

`index.js` also has one method more, `setResultState(result, state, message)` (typed in
`index.d.ts`): it sets the card state of a result by the result, not by its place, so that Anki's
answer for a word does not land on another word shown there meanwhile, and it updates what Back
returns to. `setCardState` acts on whatever is shown at that index.

Subtitle Miner also added to `index.js` (typed in `index.d.ts`):

- a `scale` option, the size of everything in the popup in percent: it goes to Hachidori's popup
  scale, which `embed-css.js` zooms the popup by (it was `zoom: 1`), and to the view's
  `getPopupScalePercent`, so that Hachidori's own measurements inside the popup stay right;
- `audioSources(result)` and `chosenAudio(result)`: holding the audio button, a right-click,
  Shift-click or Down lists a word's sounds in Hachidori's `.gsm-hoshidicts-audio-menu` styles, and
  choosing one calls `onPlayAudio(result, index, name)`. Escape or a press elsewhere closes the
  list, and `back()` closes it first. Hachidori's content script draws this list in the extension;
  the popup alone had none. `embed-css.js` keeps a hold on the button from selecting text.

- the pictures of definitions it fetched are kept as before, but one that could not be had is
  forgotten, so that it is asked for again the next time it is drawn instead of failing until the
  popup is made again.

Nothing else is changed. `src/dictionary/vendored.test.ts` fails if a `<style>` element
comes back, so that a newer copy gets the same change.
