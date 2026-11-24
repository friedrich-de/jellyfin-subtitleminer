import type * as Popup from '@/vendor/dictionary-popup/index.js';

import type { Box } from './caret';

export type { CardState, DictionaryPopup, PopupOptions } from '@/vendor/dictionary-popup/index.js';

/** A word to look up: the text from the tapped character on, and where that character is. */
export interface WordRequest {
  text: string;
  box: Box;
}

let loading: Promise<typeof Popup> | undefined;

/**
 * The dictionary popup's code, loaded the first time a word is looked up: it is most of the
 * page's size, and many never look a word up.
 */
export function loadPopup(): Promise<typeof Popup> {
  loading ??= import('@/vendor/dictionary-popup/index.js').catch((error: unknown) => {
    loading = undefined;
    throw error;
  });
  return loading;
}
