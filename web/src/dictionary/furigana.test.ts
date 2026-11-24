import { describe, expect, it } from 'vitest';

import { furiganaOf } from './furigana';

describe('the furigana of a word', () => {
  it('goes over each run of kanji, the kana between them left as they are', () => {
    expect(furiganaOf('食べる', 'たべる')).toBe('食[た]べる');
    expect(furiganaOf('食べ物', 'たべもの')).toBe('食[た]べ 物[もの]');
    expect(furiganaOf('お茶', 'おちゃ')).toBe('お 茶[ちゃ]');
    expect(furiganaOf('昨日', 'きのう')).toBe('昨日[きのう]');
  });

  it('compares kana however each is written', () => {
    expect(furiganaOf('使いコナす', 'つかいこなす')).toBe('使[つか]いコナす');
    expect(furiganaOf('焼きそば', 'ヤキソバ')).toBe('焼[ヤ]きそば');
  });

  it('goes over the whole word when the reading cannot be shared out', () => {
    expect(furiganaOf('今日は', 'きょうわ')).toBe('今日は[きょうわ]');
  });

  it('is the word alone when it has no kanji or no other reading', () => {
    expect(furiganaOf('ありがとう', 'ありがとう')).toBe('ありがとう');
    expect(furiganaOf('テレビ', 'てれび')).toBe('テレビ');
    expect(furiganaOf('猫', '')).toBe('猫');
  });
});
