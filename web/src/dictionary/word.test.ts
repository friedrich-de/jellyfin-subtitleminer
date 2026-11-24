import { describe, expect, it } from 'vitest';

import type { LookupResult } from '@/api/types';

import { definitionEntries, meaningHtml } from './glossaryHtml';
import { wordOf } from './word';

function result(overrides: Partial<LookupResult['term']> = {}): LookupResult {
  return {
    matched: '食べられなかった',
    deinflected: '食べる',
    trace: [{ name: '-た' }],
    term: {
      expression: '食べる',
      reading: 'たべる',
      rules: 'v1',
      glossaries: [],
      frequencies: [],
      pitches: [],
      ...overrides,
    },
  };
}

function glossary(dictionary: string, entries: unknown[]): LookupResult['term']['glossaries'][0] {
  return {
    dictionary,
    glossary: JSON.stringify(entries),
    definitionTags: '',
    termTags: '',
    tags: [],
  };
}

describe('a definition written for a card', () => {
  it('keeps the structure, the dictionary marks and the styles of structured content', () => {
    const html = definitionEntries(
      JSON.stringify([
        {
          type: 'structured-content',
          content: [
            { tag: 'span', data: { content: 'part-of-speech' }, content: 'verb' },
            {
              tag: 'ul',
              style: { marginLeft: 1, fontWeight: 'bold', listStyleType: 'circle' },
              content: [
                { tag: 'li', lang: 'en', content: ['to eat', { tag: 'br' }, 'to live on'] },
              ],
            },
            {
              tag: 'table',
              content: { tag: 'tr', content: { tag: 'td', colSpan: 2, content: 'x' } },
            },
          ],
        },
      ]),
    );
    expect(html).toEqual([
      '<span data-sc-content="part-of-speech">verb</span>' +
        '<ul style="font-weight: bold; margin-left: 1em; list-style-type: circle"><li lang="en">to eat<br>to live on</li></ul>' +
        '<table><tr><td colspan="2">x</td></tr></table>',
    ]);
  });

  it('leaves out pictures, links into the dictionary, unknown tags and unsafe styles', () => {
    const html = definitionEntries(
      JSON.stringify([
        {
          type: 'structured-content',
          content: [
            { tag: 'img', path: 'img/bowl.png' },
            { tag: 'a', href: '?query=食う', content: '食う' },
            { tag: 'a', href: 'https://example.org/"x', content: 'more' },
            { tag: 'script', content: 'alert(1)' },
            { tag: 'span', style: { background: 'url(https://example.org/x)', color: 'red;x' } },
            { tag: 'span', data: { 'x"y': 'z', note: '<b>"' }, content: '<b>' },
          ],
        },
        { type: 'image', path: 'img/bowl.png' },
        ['食う', ['v5']],
      ]),
    );
    expect(html).toEqual([
      '<span>食う</span><a href="https://example.org/&quot;x">more</a><span></span>' +
        '<span data-sc-note="&lt;b&gt;&quot;">&lt;b&gt;</span>',
    ]);
  });

  it('reads plain text, the text type and text that is not JSON', () => {
    expect(definitionEntries('["to eat", {"type": "text", "text": "a\\nb"}, ""]')).toEqual([
      'to eat',
      'a<br>b',
    ]);
    expect(definitionEntries('<not json>')).toEqual(['&lt;not json&gt;']);
  });
});

describe('the meaning on a card', () => {
  it('is the definitions of the first dictionary only', () => {
    const word = result({
      glossaries: [
        glossary('Words', ['to eat']),
        glossary('Other', ['to consume']),
        glossary('Words', ['to live on', 'to make a living']),
      ],
    });
    expect(meaningHtml(word)).toBe(
      '<div class="yomitan-glossary" style="text-align: left"><ol><li>to eat</li>' +
        '<li><ul><li>to live on</li><li>to make a living</li></ul></li></ol></div>',
    );
  });

  it('is one definition as it is, and nothing without one', () => {
    expect(meaningHtml(result({ glossaries: [glossary('Words', ['to eat'])] }))).toBe(
      '<div class="yomitan-glossary" style="text-align: left">to eat</div>',
    );
    expect(meaningHtml(result())).toBe('');
  });
});

describe('a word for a card', () => {
  it('has the word, its reading, furigana, frequency, pitch and place in the sentence', () => {
    const word = wordOf(
      result({
        glossaries: [glossary('Words', ['to eat'])],
        frequencies: [
          { dictionary: 'Empty', frequencies: [] },
          {
            dictionary: 'Freq',
            frequencies: [{ value: 512.4, displayValue: '512㋕' }, { value: 9 }],
          },
        ],
        pitches: [
          { dictionary: 'Pitch', pitches: [{ position: 2 }, { position: 0 }, { position: 2 }] },
        ],
      }),
      3,
    );
    expect(word).toEqual({
      expression: '食べる',
      reading: 'たべる',
      furigana: '食[た]べる',
      meaning: '<div class="yomitan-glossary" style="text-align: left">to eat</div>',
      frequency: '512',
      pitch: '2, 0',
      pitchCategories: 'kifuku,heiban',
      at: { start: 3, length: 8 },
    });
  });

  it('has no frequency or pitch where no dictionary gives one', () => {
    const word = wordOf(result(), 0);
    expect([word.frequency, word.pitch, word.pitchCategories]).toEqual(['', '', '']);
  });

  it('names the pitch patterns as Yomitan does, for Lapis to colour the word by', () => {
    const categories = (expression: string, reading: string, rules: string, positions: number[]) =>
      wordOf(
        result({
          expression,
          reading,
          rules,
          pitches: [{ dictionary: 'Pitch', pitches: positions.map((position) => ({ position })) }],
        }),
        0,
      ).pitchCategories;

    expect(categories('箸', 'はし', '', [1])).toBe('atamadaka');
    expect(categories('橋', 'はし', '', [2])).toBe('odaka');
    expect(categories('お菓子', 'おかし', '', [2])).toBe('nakadaka');
    expect(categories('桜', 'さくら', '', [0])).toBe('heiban');
    // A small ゃ is part of the mora before it, a small っ a mora of its own.
    expect(categories('ちゃっかり', 'ちゃっかり', '', [3, 4])).toBe('nakadaka,odaka');
    // A verb or an adjective whose pitch falls is kifuku, wherever it falls.
    expect(categories('食べる', 'たべる', 'v1', [2])).toBe('kifuku');
    expect(categories('高い', 'たかい', 'adj-i', [2, 0])).toBe('kifuku,heiban');
    // A noun that takes する is named as a noun.
    expect(categories('勉強', 'べんきょう', 'vs n', [0])).toBe('heiban');
    expect(categories('料理', 'りょうり', 'vs n', [1])).toBe('atamadaka');
  });
});
