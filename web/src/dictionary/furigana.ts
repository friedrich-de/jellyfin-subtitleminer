/** Hiragana, katakana and the long vowel mark: what a reading is written in. */
const KANA = /^[ぁ-ゖゝ-ゟァ-ヺー-ヿ]$/u;

function isKana(character: string): boolean {
  return KANA.test(character);
}

/** Katakana as hiragana, so that a word's kana and its reading compare however each is written. */
function hiragana(text: string): string {
  return text.replace(/[ァ-ヶ]/gu, (character) =>
    String.fromCodePoint(character.codePointAt(0)! - 0x60),
  );
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The word in runs of kana and runs of the rest. */
function runs(expression: string): { text: string; kana: boolean }[] {
  const found: { text: string; kana: boolean }[] = [];
  for (const character of expression) {
    const kana = isKana(character);
    const last = found.at(-1);
    if (last?.kana === kana) last.text += character;
    else found.push({ text: character, kana });
  }
  return found;
}

/**
 * The word with its reading in Anki's furigana: "食[た]べ 物[もの]", the reading over each
 * run of kanji, as Anki's {{furigana:}} draws it. Where the reading cannot be shared out among
 * the runs, it goes over the whole word.
 */
export function furiganaOf(expression: string, reading: string): string {
  if (reading === '' || reading === expression) return expression;
  const parts = runs(expression);
  if (!parts.some((part) => !part.kana)) return expression;

  const pattern = new RegExp(
    `^${parts.map((part) => (part.kana ? `(${escapeRegExp(hiragana(part.text))})` : '(.+?)')).join('')}$`,
    'u',
  );
  const match = pattern.exec(hiragana(reading));
  if (match === null) return `${expression}[${reading}]`;

  // Hiragana and katakana are the same length, so the groups' places hold in the reading as given.
  let at = 0;
  let written = '';
  parts.forEach((part, index) => {
    const length = match[index + 1]!.length;
    const said = reading.slice(at, at + length);
    at += length;
    if (part.kana) {
      written += part.text;
      return;
    }
    written += `${written === '' ? '' : ' '}${part.text}[${said}]`;
  });
  return written;
}
