import type { LookupResult } from '@/api/types';

/**
 * A word's definitions as HTML for a card's field, the way Yomitan puts them there: Yomitan's
 * structured content written out with the dictionary's `data-sc-` marks, so that a note type's
 * styles for them work, and its styles inline. What could run or load something is left out:
 * pictures, links into the dictionary, and any tag or style the format does not name.
 */

type Content = string | Content[] | Tagged | null | undefined;

/** An element of structured content. */
interface Tagged {
  tag?: unknown;
  content?: Content;
  data?: unknown;
  style?: unknown;
  lang?: unknown;
  title?: unknown;
  href?: unknown;
  colSpan?: unknown;
  rowSpan?: unknown;
}

const TAGS = new Set([
  'br',
  'ruby',
  'rt',
  'rp',
  'table',
  'thead',
  'tbody',
  'tfoot',
  'tr',
  'td',
  'th',
  'span',
  'div',
  'ol',
  'ul',
  'li',
  'details',
  'summary',
  'a',
]);

/** Yomitan's style names, as CSS names them. Margins and paddings in numbers are in em. */
const STYLES: Record<string, string> = {
  fontStyle: 'font-style',
  fontWeight: 'font-weight',
  fontSize: 'font-size',
  color: 'color',
  background: 'background',
  backgroundColor: 'background-color',
  textDecorationLine: 'text-decoration-line',
  textDecorationStyle: 'text-decoration-style',
  textDecorationColor: 'text-decoration-color',
  borderColor: 'border-color',
  borderStyle: 'border-style',
  borderRadius: 'border-radius',
  borderWidth: 'border-width',
  clipPath: 'clip-path',
  verticalAlign: 'vertical-align',
  textAlign: 'text-align',
  textEmphasis: 'text-emphasis',
  textShadow: 'text-shadow',
  margin: 'margin',
  marginTop: 'margin-top',
  marginLeft: 'margin-left',
  marginRight: 'margin-right',
  marginBottom: 'margin-bottom',
  padding: 'padding',
  paddingTop: 'padding-top',
  paddingLeft: 'padding-left',
  paddingRight: 'padding-right',
  paddingBottom: 'padding-bottom',
  wordBreak: 'word-break',
  whiteSpace: 'white-space',
  cursor: 'cursor',
  listStyleType: 'list-style-type',
};

/** Only plain values: nothing that loads a file, closes the attribute or starts another rule. */
const SAFE_VALUE = /^[\w\s#%.,+\-()/]*$/u;

function escape(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function styleOf(style: unknown): string {
  if (!isRecord(style)) return '';
  const declarations: string[] = [];
  for (const [name, property] of Object.entries(STYLES)) {
    let value = style[name];
    if (Array.isArray(value)) value = value.filter((part) => typeof part === 'string').join(' ');
    if (typeof value === 'number' && Number.isFinite(value) && /^(margin|padding)/.test(name)) {
      value = `${value}em`;
    }
    if (typeof value !== 'string' || value === '' || !SAFE_VALUE.test(value)) continue;
    if (/url\s*\(|expression\s*\(/i.test(value)) continue;
    declarations.push(`${property}: ${value}`);
  }
  return declarations.join('; ');
}

function attributesOf(element: Tagged, tag: string): string {
  const attributes: string[] = [];
  if (isRecord(element.data)) {
    for (const [key, value] of Object.entries(element.data)) {
      if (typeof value !== 'string' || !/^[a-zA-Z][\w-]{0,63}$/.test(key)) continue;
      const name = key.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
      attributes.push(`data-sc-${name}="${escape(value)}"`);
    }
  }
  if (
    typeof element.lang === 'string' &&
    /^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$/.test(element.lang)
  ) {
    attributes.push(`lang="${element.lang}"`);
  }
  if (typeof element.title === 'string' && element.title !== '') {
    attributes.push(`title="${escape(element.title)}"`);
  }
  if (tag === 'td' || tag === 'th') {
    for (const [name, value] of [
      ['colspan', element.colSpan],
      ['rowspan', element.rowSpan],
    ] as const) {
      if (typeof value === 'number' && Number.isInteger(value) && value > 1 && value <= 1000) {
        attributes.push(`${name}="${value}"`);
      }
    }
  }
  if (tag === 'a' && typeof element.href === 'string' && /^https?:\/\//i.test(element.href)) {
    attributes.push(`href="${escape(element.href)}"`);
  }
  const style = styleOf(element.style);
  if (style !== '') attributes.push(`style="${escape(style)}"`);
  return attributes.length === 0 ? '' : ` ${attributes.join(' ')}`;
}

function contentHtml(content: Content, depth = 0): string {
  if (depth > 64 || content === null || content === undefined) return '';
  if (typeof content === 'string') return escape(content).replace(/\r?\n/g, '<br>');
  if (Array.isArray(content)) return content.map((part) => contentHtml(part, depth + 1)).join('');
  if (!isRecord(content) || typeof content.tag !== 'string') return '';
  const tag = content.tag;
  if (tag === 'br') return '<br>';
  if (!TAGS.has(tag)) return '';
  const inner = contentHtml(content.content as Content, depth + 1);
  // A link into the dictionary leads nowhere on a card: its text stays, as a span.
  const href = typeof content.href === 'string' ? content.href : '';
  const written = tag === 'a' && !/^https?:\/\//i.test(href) ? 'span' : tag;
  return `<${written}${attributesOf(content, written)}>${inner}</${written}>`;
}

/** One entry of a definition: text, structured content, or what is not shown (a picture, a deinflection). */
function entryHtml(entry: unknown): string {
  if (typeof entry === 'string') return escape(entry).replace(/\r?\n/g, '<br>');
  if (!isRecord(entry)) return '';
  if (entry.type === 'text' && typeof entry.text === 'string') {
    return escape(entry.text).replace(/\r?\n/g, '<br>');
  }
  if (entry.type === 'structured-content') return contentHtml(entry.content as Content);
  return '';
}

/** The entries of one definition, as HTML each, the empty ones left out. */
export function definitionEntries(glossary: string): string[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(glossary);
  } catch {
    return glossary.trim() === '' ? [] : [escape(glossary)];
  }
  const entries = Array.isArray(parsed) ? parsed : [parsed];
  return entries.map(entryHtml).filter((html) => html.trim() !== '');
}

function list(tag: 'ol' | 'ul', items: string[]): string {
  return `<${tag}>${items.map((item) => `<li>${item}</li>`).join('')}</${tag}>`;
}

/**
 * The word's definitions in its first dictionary: one definition as it is, several as a
 * numbered list, and several entries of one definition as a list of their own.
 */
export function meaningHtml(result: LookupResult): string {
  const first = result.term.glossaries[0]?.dictionary;
  if (first === undefined) return '';
  const definitions = result.term.glossaries
    .filter((glossary) => glossary.dictionary === first)
    .map((glossary) => definitionEntries(glossary.glossary))
    .filter((entries) => entries.length > 0)
    .map((entries) => (entries.length === 1 ? entries[0]! : list('ul', entries)));
  if (definitions.length === 0) return '';
  const body = definitions.length === 1 ? definitions[0]! : list('ol', definitions);
  return `<div class="yomitan-glossary" style="text-align: left">${body}</div>`;
}
