import { describe, expect, it } from 'vitest';

import { MemoryStorage } from '@/testing/fakes';

import { MAX_REMEMBERED, recall, remember, showKeyOf, TRACKS_KEY } from './remembered';
import { line, sampleWatch, track } from './samples';
import {
  agoOf,
  choose,
  chooseMain,
  chooseSecond,
  clockOf,
  hintOf,
  languageTag,
  lastStartedAt,
  linesAt,
  pairLines,
  secondOf,
  pickFor,
  positionOf,
  titleOf,
  trackLabel,
} from './watches';

describe('where playback is', () => {
  it('moves on with the clock while playing, up to the end', () => {
    const watch = sampleWatch({ PositionMs: 10_000, RuntimeMs: 12_000 });

    expect(positionOf(watch, 500, 500)).toBe(10_000);
    expect(positionOf(watch, 500, 1_700)).toBe(11_200);
    expect(positionOf(watch, 500, 9_000)).toBe(12_000);
    expect(positionOf(watch, 500, 100)).toBe(10_000);
  });

  it('stays put while paused or after the end', () => {
    expect(positionOf(sampleWatch({ PositionMs: 10_000, Paused: true }), 0, 5_000)).toBe(10_000);
    expect(positionOf(sampleWatch({ PositionMs: 10_000, Playing: false }), 0, 5_000)).toBe(10_000);
  });
});

describe('the words for a watch', () => {
  it('names an episode by its show and number, a film by its name and year', () => {
    expect(titleOf(sampleWatch())).toBe('Sample Show · S1 E3');
    expect(titleOf(sampleWatch({ Season: undefined }))).toBe('Sample Show · E3');
    const special = sampleWatch({ Season: undefined, Episode: undefined, Name: 'Special' });
    expect(titleOf(special)).toBe('Sample Show · Special');
    const film = sampleWatch({ SeriesName: undefined, Name: 'A Film', Year: 2020 });
    expect(titleOf(film)).toBe('A Film (2020)');
  });

  it('writes times the way a player shows them', () => {
    expect(clockOf(0)).toBe('00:00');
    expect(clockOf(456_999)).toBe('07:36');
    expect(clockOf(3_729_000)).toBe('1:02:09');
    expect(clockOf(-50)).toBe('00:00');
    expect(agoOf(20_000)).toBe('just now');
    expect(agoOf(7 * 60_000)).toBe('7 min ago');
    expect(agoOf(65 * 60_000)).toBe('1 hour ago');
    expect(agoOf(5 * 3_600_000)).toBe('5 hours ago');
  });

  it('names a track by its title, language and kind', () => {
    expect(trackLabel(track('s1', { Language: 'eng', Title: 'English' }))).toBe('English · eng');
    expect(trackLabel(track('x1', { Language: 'jpn', External: true }))).toBe('jpn · file');
    expect(trackLabel(track('s2', { Readable: false }))).toBe(
      'Unknown language · pictures, cannot be read',
    );
  });

  it('gives the lang attribute a tag a browser knows', () => {
    expect(languageTag(track('a', { Language: 'jpn' }))).toBe('ja');
    expect(languageTag(track('a', { Language: 'eng' }))).toBe('en');
    expect(languageTag(track('a', { Language: 'de' }))).toBe('de');
    expect(languageTag(track('a', { Language: 'ger' }))).toBeUndefined();
    expect(languageTag(undefined)).toBeUndefined();
  });
});

describe('which watch is shown', () => {
  const first = sampleWatch({ Id: 'one', Player: 'Living room', Episode: 3 });
  const other = sampleWatch({ Id: 'two', Player: 'Bedroom', Client: 'Jellyfin Web' });
  const over = sampleWatch({ Id: 'old', Playing: false, Episode: 2 });

  it('is what plays first, unless the user chose', () => {
    expect(choose([first, other, over], { kind: 'auto' })?.Id).toBe('one');
    expect(choose([], { kind: 'auto' })).toBeUndefined();
    expect(choose([first, other, over], pickFor(other))?.Id).toBe('two');
    expect(choose([first, other, over], pickFor(over))?.Id).toBe('old');
  });

  it('moves on with the chosen player to its next episode', () => {
    const pick = pickFor(first);
    const next = sampleWatch({ Id: 'next', Player: 'Living room', Episode: 4 });
    const ended = { ...first, Playing: false };

    expect(choose([other, next, ended], pick)?.Id).toBe('next');
    // When it plays nothing any more, its last watch stays.
    expect(choose([other, ended], pick)?.Id).toBe('one');
  });

  it('falls back to the first when the chosen one is gone', () => {
    expect(choose([other], { kind: 'watch', id: 'gone' })?.Id).toBe('two');
  });
});

describe('which tracks are shown', () => {
  it('prefers a full track in the language being learnt over the one the player shows', () => {
    // The player shows English, the Japanese inside the video is signs only: the file next to it wins.
    expect(chooseMain(sampleWatch(), 'japanese', undefined)?.Key).toBe('x0a1b2c3d4e5f');
  });

  it('takes the track the player shows when it is in that language', () => {
    const watch = sampleWatch({
      SubtitleKey: 's3',
      Subtitles: [...sampleWatch().Subtitles, track('s3', { Language: 'jpn', Title: 'Full' })],
    });

    expect(chooseMain(watch, 'japanese', undefined)?.Key).toBe('s3');
  });

  it('knows the language from a title when the file names none', () => {
    const watch = sampleWatch({
      SubtitleKey: undefined,
      Subtitles: [track('s0', { Title: 'English' }), track('s1', { Title: '日本語' })],
    });

    expect(chooseMain(watch, 'japanese', undefined)?.Key).toBe('s1');
  });

  it('falls back to what the player shows, then to the first that can be read', () => {
    const english = sampleWatch({
      Subtitles: [track('s0', { Language: 'eng' }), track('s1', { Language: 'eng' })],
    });
    expect(chooseMain(english, 'japanese', undefined)?.Key).toBe('s1');
    const nothingShown = sampleWatch({ Subtitles: english.Subtitles, SubtitleKey: undefined });
    expect(chooseMain(nothingShown, 'japanese', undefined)?.Key).toBe('s0');

    const pictures = sampleWatch({ Subtitles: [track('s0', { Readable: false })] });
    expect(chooseMain(pictures, 'japanese', undefined)).toBeUndefined();
  });

  it('keeps the choice for the next episode, whose file next to it has another key', () => {
    const chosen = hintOf(track('s1', { Language: 'eng', Title: 'English' }));
    expect(chooseMain(sampleWatch(), 'japanese', chosen)?.Key).toBe('s1');

    const file = hintOf(track('x999999999999', { Language: 'jpn', External: true }));
    const next = sampleWatch({ SubtitleKey: undefined });
    expect(chooseMain(next, 'japanese', file)?.Key).toBe('x0a1b2c3d4e5f');
  });

  it('shows a second line only once one was chosen, and never the main track twice', () => {
    const watch = sampleWatch();
    const english = hintOf(track('s1', { Language: 'eng', Title: 'English' }));
    const main = chooseMain(watch, 'japanese', undefined);

    expect(chooseSecond(watch, main, undefined)).toBeUndefined();
    expect(chooseSecond(watch, main, 'none')).toBeUndefined();
    expect(chooseSecond(watch, main, english)?.Key).toBe('s1');
    expect(chooseSecond(watch, track('s1'), english)).toBeUndefined();
  });
});

describe('the lines', () => {
  const main = [
    line(0, 1_000, 3_000, 'いち'),
    line(1, 3_000, 5_000, 'に'),
    line(2, 8_000, 9_000, 'さん'),
    line(3, 8_800, 12_000, '看板', { Kind: 'sign' }),
  ];

  it('get the second track’s lines said at the same time', () => {
    const second = [
      line(10, 900, 2_900, 'one'),
      line(11, 2_950, 5_100, 'two'),
      line(12, 4_900, 5_200, 'barely'),
      line(13, 8_000, 9_000, 'three'),
      line(14, 8_100, 9_000, 'three again'),
      line(15, 20_000, 21_000, 'later'),
    ];

    expect(pairLines(main, second).map((shown) => shown.second)).toEqual([
      'one',
      'two',
      'three\nthree again',
      undefined,
    ]);
    expect(pairLines(main, undefined)).toEqual(main);
  });

  it('give a card the second track’s lines once, though one spans several chosen', () => {
    const second = [
      line(10, 900, 5_100, 'one and two'),
      line(11, 7_900, 9_100, 'three'),
      line(12, 20_000, 21_000, 'later'),
    ];

    expect(secondOf(main.slice(0, 2), second)).toEqual(['one and two']);
    expect(secondOf([main[2]!, main[0]!], second)).toEqual(['one and two', 'three']);
    expect(secondOf(main.slice(0, 2), undefined)).toEqual([]);
  });

  it('are found by the moment they are on screen', () => {
    expect(linesAt(main, 999).map((shown) => shown.Id)).toEqual([]);
    expect(linesAt(main, 3_000).map((shown) => shown.Id)).toEqual([1]);
    expect(linesAt(main, 8_900).map((shown) => shown.Id)).toEqual([2, 3]);
    expect(lastStartedAt(main, 500)).toBeUndefined();
    expect(lastStartedAt(main, 6_000)?.Id).toBe(1);
    expect(lastStartedAt(main, 50_000)?.Id).toBe(3);
  });
});

describe('the tracks remembered in this browser', () => {
  it('are kept per show, and per film', () => {
    const storage = new MemoryStorage();
    const english = hintOf(track('s1', { Language: 'eng' }));
    const show = showKeyOf(sampleWatch());
    remember(storage, show, { main: english }, 1);
    remember(storage, show, { second: 'none' }, 2);

    expect(show).toBe('show:Sample Show');
    expect(recall(storage, show)).toEqual({ main: english, second: 'none', at: 2 });
    expect(showKeyOf(sampleWatch({ SeriesName: undefined }))).toBe(
      'item:aaaaaaaabbbbccccddddeeeeeeeeeeee',
    );
    expect(recall(storage, 'show:Other')).toBeUndefined();
  });

  it('forget the oldest and survive what someone else wrote there', () => {
    const storage = new MemoryStorage();
    for (let i = 0; i <= MAX_REMEMBERED; i += 1)
      remember(storage, `show:${i}`, { second: 'none' }, i);

    expect(recall(storage, 'show:0')).toBeUndefined();
    expect(recall(storage, `show:${MAX_REMEMBERED}`)).toBeDefined();

    storage.setItem(TRACKS_KEY, '[1, 2');
    expect(recall(storage, 'show:1')).toBeUndefined();
    remember(storage, 'show:1', { second: 'none' }, 5);
    expect(recall(storage, 'show:1')?.second).toBe('none');
  });
});
