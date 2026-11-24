import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { DuplicateCardError } from '@/anki/cards';
import { createPluginApi } from '@/api/plugin';
import type { DictionaryView, LookupResult } from '@/api/types';
import type { WordRequest } from '@/dictionary/popup';
import { BASE, fakeRoutes, json, type RoutedRequest } from '@/testing/fakes';

import WordPopup from './WordPopup.vue';

/** The dictionary popup as the page sees it: what it was told to show, and its buttons. */
const drawn = vi.hoisted(() => ({
  options: undefined as Record<string, unknown> | undefined,
  render: vi.fn(),
  clear: vi.fn(),
  setCardState: vi.fn(),
  setResultState: vi.fn(),
  back: vi.fn(() => false),
  setTheme: vi.fn(),
  destroy: vi.fn(),
}));

vi.mock('@/dictionary/popup', () => ({
  loadPopup: () =>
    Promise.resolve({
      createDictionaryPopup: (_container: HTMLElement, options: Record<string, unknown>) => {
        drawn.options = options;
        return drawn;
      },
    }),
}));

function result(expression: string, matched = expression): LookupResult {
  return {
    matched,
    deinflected: expression,
    trace: [],
    term: { expression, reading: '', rules: '', glossaries: [], frequencies: [], pitches: [] },
  };
}

const WORDS: DictionaryView = {
  Id: 'words-1',
  Title: 'Words',
  Revision: '1',
  Enabled: true,
  Terms: 4,
  Frequencies: 0,
  Pitches: 0,
  Styles: '.gloss { font-weight: bold; }',
  Bytes: 1_000,
  Imported: '2026-11-24T01:00:00Z',
};

function server(found: Record<string, LookupResult[]>) {
  return fakeRoutes((request: RoutedRequest) => {
    if (request.path === 'Dictionaries/Lookup') {
      return json({ results: found[request.query.get('text') ?? ''] ?? [] });
    }
    if (request.path === 'Dictionaries/words-1/Media') {
      return new Response(new Blob(['png'], { type: 'image/png' }));
    }
    return undefined;
  });
}

const request = (text: string): WordRequest => ({
  text,
  box: { left: 100, top: 200, right: 122, bottom: 230 },
});

let popup: VueWrapper | undefined;

function open(
  backend: ReturnType<typeof server>,
  props: Partial<InstanceType<typeof WordPopup>['$props']> = {},
) {
  popup = mount(WordPopup, {
    props: {
      api: createPluginApi(backend.fetch, BASE, 'token-1'),
      dictionaries: [WORDS],
      request: null,
      addCard: undefined,
      noCards: 'Choose where your cards go in the settings to make cards of words.',
      inDeck: undefined,
      ...props,
    },
    attachTo: document.body,
  });
  return popup;
}

describe('the word popup', () => {
  afterEach(() => {
    popup?.unmount();
    popup = undefined;
    vi.clearAllMocks();
  });

  it('looks the word up and shows what it found, the dictionaries’ styles with it', async () => {
    const backend = server({ 食べられなかった: [result('食べる', '食べられなかった')] });
    const shown = open(backend);
    await shown.setProps({ request: request('食べられなかった') });
    await flushPromises();

    expect(backend.sent.map((sent) => sent.path)).toEqual(['Dictionaries/Lookup']);
    expect(shown.emitted('found')).toEqual([[8]]);
    expect(drawn.render).toHaveBeenCalledWith([result('食べる', '食べられなかった')], {
      styles: [{ dictionary: 'Words', styles: '.gloss { font-weight: bold; }' }],
      states: ['unavailable'],
    });
    // Without Anki the popup says how to make cards.
    expect(shown.text()).toContain('Choose where your cards go');

    // A picture of a definition comes from the server with the login, as a blob.
    const media = drawn.options!.media as (dictionary: string, path: string) => Promise<Blob>;
    await expect(media('Words', 'img/bowl.png')).resolves.toBeInstanceOf(Blob);
    expect(backend.sent.at(-1)!.headers.authorization).toBe('MediaBrowser Token="token-1"');
  });

  it('fetches the dictionaries again for a word from one added since, for its pictures and styles', async () => {
    // Imported after the page last asked for the list, as an import still running when the
    // settings were closed.
    const MARKS: DictionaryView = {
      ...WORDS,
      Id: 'marks-1',
      Title: 'Marks',
      Styles: '.mark { color: teal; }',
      FrequencyMode: 'rank-based',
    };
    const word = result('丈夫');
    word.term.glossaries = [
      { dictionary: 'Marks', glossary: '[]', definitionTags: '', termTags: '', tags: [] },
    ];
    const backend = fakeRoutes((asked) => {
      if (asked.path === 'Dictionaries/Lookup') {
        return json({ results: asked.query.get('text') === '丈夫' ? [word] : [result('食べる')] });
      }
      if (asked.path === 'Dictionaries/marks-1/Media') {
        return new Response(new Blob(['<svg/>'], { type: 'image/svg+xml' }));
      }
      return undefined;
    });
    // As the page does: its list, fetched again, reaches the popup.
    const reload = vi.fn(async () => {
      await popup!.setProps({ dictionaries: [WORDS, MARKS] });
    });
    const shown = open(backend, { reload });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();
    expect(reload).not.toHaveBeenCalled();
    const first = drawn.options!;

    await shown.setProps({ request: request('丈夫') });
    await flushPromises();
    expect(reload).toHaveBeenCalledTimes(1);
    // The popup is made again, knowing the new dictionary, and draws the word with its styles.
    expect(drawn.destroy).toHaveBeenCalledTimes(1);
    expect(drawn.options).not.toBe(first);
    expect(drawn.options!.dictionaries).toEqual([
      { title: 'Words', frequencyMode: null },
      { title: 'Marks', frequencyMode: 'rank-based' },
    ]);
    expect(drawn.render).toHaveBeenLastCalledWith([word], {
      styles: [
        { dictionary: 'Words', styles: '.gloss { font-weight: bold; }' },
        { dictionary: 'Marks', styles: '.mark { color: teal; }' },
      ],
      states: ['unavailable'],
    });

    // Its pictures come from it.
    const media = drawn.options!.media as (dictionary: string, path: string) => Promise<Blob>;
    await expect(media('Marks', 'gaiji/文語.svg')).resolves.toBeInstanceOf(Blob);
    const asked = backend.sent.at(-1)!;
    expect(asked.path).toBe('Dictionaries/marks-1/Media');
    expect(asked.query.get('path')).toBe('gaiji/文語.svg');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('asks for the list again for a picture of a dictionary it does not know, once at a time', async () => {
    const backend = server({ 食べる: [result('食べる')] });
    let answer: () => void = () => undefined;
    const reload = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          answer = resolve;
        }),
    );
    const shown = open(backend, { reload });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();

    // A word in a definition may come from a dictionary the page has not heard of; the server
    // still has none of that name.
    const media = drawn.options!.media as (dictionary: string, path: string) => Promise<Blob>;
    const asked = [media('Gone', 'a.png'), media('Gone', 'b.png')];
    await flushPromises();
    expect(reload).toHaveBeenCalledTimes(1);
    answer();
    for (const picture of asked) await expect(picture).rejects.toThrow('No such dictionary.');
    expect(backend.sent.map((sent) => sent.path)).toEqual(['Dictionaries/Lookup']);
  });

  it('says so when no word starts there', async () => {
    const shown = open(server({}));
    await shown.setProps({ request: request('、') });
    await flushPromises();

    expect(shown.emitted('found')).toEqual([[0]]);
    expect(shown.get('[data-testid="word-none"]').text()).toBe(
      'No word in the dictionaries starts here.',
    );
  });

  it('marks the words Anki has, and makes a card of a word', async () => {
    const found = [result('食べる'), result('食')];
    const addCard = vi.fn((word: LookupResult) =>
      word.term.expression === '食'
        ? Promise.reject(new DuplicateCardError())
        : Promise.resolve('Made a new card, "食べる".'),
    );
    const shown = open(server({ 食べる: found }), {
      addCard,
      inDeck: () => Promise.resolve([false, true]),
    });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();

    expect(drawn.render).toHaveBeenCalledWith(
      found,
      expect.objectContaining({ states: ['waiting', 'waiting'] }),
    );
    // The popup hands back the results it was given.
    const given = drawn.render.mock.calls[0]![0] as LookupResult[];
    expect(drawn.setResultState.mock.calls).toEqual([
      [given[0], 'add'],
      [given[1], 'in-anki'],
    ]);

    const onAddCard = drawn.options!.onAddCard as (word: LookupResult, index: number) => void;
    onAddCard(given[0]!, 0);
    await flushPromises();
    expect(addCard).toHaveBeenCalledWith(given[0], true, '', undefined);
    expect(drawn.setResultState).toHaveBeenLastCalledWith(
      given[0],
      'in-anki',
      'Made a new card, "食べる".',
    );
    expect(shown.get('[data-testid="word-status"]').text()).toBe('Made a new card, "食べる".');

    // The deck has that one: the popup says so, and it is no error.
    onAddCard(given[1]!, 1);
    await flushPromises();
    expect(drawn.setResultState).toHaveBeenLastCalledWith(
      given[1],
      'in-anki',
      'This deck has a card like this already.',
    );
    // Never by place, which another word may have taken by the time Anki answers.
    expect(drawn.setCardState).not.toHaveBeenCalled();
    expect(shown.get('[data-testid="word-status"]').attributes('role')).toBe('status');
  });

  it('tells a word looked up in a definition apart from one of the line', async () => {
    const addCard = vi.fn(() => Promise.resolve('Made a new card, "食う".'));
    const backend = server({ 食べる: [result('食べる')], 食う: [result('食う')] });
    const shown = open(backend, { addCard });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();

    const lookup = drawn.options!.lookup as (text: string) => Promise<{ results: LookupResult[] }>;
    const nested = await lookup('食う');
    (drawn.options!.onAddCard as (word: LookupResult, index: number) => void)(
      nested.results[0]!,
      0,
    );
    await flushPromises();
    expect(addCard).toHaveBeenCalledWith(nested.results[0], false, '', undefined);
  });

  it("plays a word's sound from the server, while it has an audio source", async () => {
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
    const asked: string[] = [];
    const backend = fakeRoutes((sent: RoutedRequest) => {
      if (sent.path === 'Dictionaries/Lookup') {
        return json({
          results: [{ ...result('食べる'), term: { ...result('食べる').term, reading: 'たべる' } }],
        });
      }
      if (sent.path === 'WordAudio') {
        asked.push(`${sent.query.get('term')}|${sent.query.get('reading')}`);
        return sent.query.get('term') === '食べる'
          ? new Response(new Blob(['mp3'], { type: 'audio/mpeg' }))
          : json({ detail: 'The audio source has no sound for this word.' }, 404);
      }
      return undefined;
    });
    const shown = open(backend, { wordAudio: false });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();

    const hasAudio = drawn.options!.hasAudio as (word: LookupResult) => boolean;
    const onPlayAudio = drawn.options!.onPlayAudio as (word: LookupResult, index: number) => void;
    const given = drawn.render.mock.calls[0]![0] as LookupResult[];
    expect(hasAudio(given[0]!)).toBe(false);
    await shown.setProps({ wordAudio: true });
    expect(hasAudio(given[0]!)).toBe(true);

    onPlayAudio(given[0]!, 0);
    await flushPromises();
    expect(asked).toEqual(['食べる|たべる']);
    expect(play).toHaveBeenCalledOnce();

    onPlayAudio(result('猫'), 0);
    await flushPromises();
    expect(shown.get('[data-testid="word-status"]').text()).toBe(
      'No sound of 猫: The audio source has no sound for this word.',
    );
    play.mockRestore();
  });

  it("lists a word's sounds to choose one, which plays from then on and goes on its card", async () => {
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
    const asked: string[] = [];
    const eat = { ...result('食べる'), term: { ...result('食べる').term, reading: 'たべる' } };
    const backend = fakeRoutes((sent: RoutedRequest) => {
      if (sent.path === 'Dictionaries/Lookup') return json({ results: [eat] });
      if (sent.path === 'WordAudio/Sources') {
        return json({ Sources: ['nhk16', 'jpod', 'forvo (2)'] });
      }
      if (sent.path === 'WordAudio') {
        asked.push(sent.query.get('source') ?? '(first)');
        return new Response(new Blob(['mp3'], { type: 'audio/mpeg' }));
      }
      return undefined;
    });
    const addCard = vi.fn(() => Promise.resolve('Made a new card, "食べる".'));
    const shown = open(backend, { wordAudio: true, addCard });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();

    const options = drawn.options!;
    const given = (drawn.render.mock.calls[0]![0] as LookupResult[])[0]!;
    const sources = options.audioSources as (word: LookupResult) => Promise<string[]>;
    const chosen = options.chosenAudio as (word: LookupResult) => string | undefined;
    const onPlayAudio = options.onPlayAudio as (
      word: LookupResult,
      index: number,
      source?: string,
    ) => void;
    await expect(sources(given)).resolves.toEqual(['nhk16', 'jpod', 'forvo (2)']);
    expect(chosen(given)).toBeUndefined();

    onPlayAudio(given, 0);
    await flushPromises();
    onPlayAudio(given, 0, 'jpod');
    await flushPromises();
    expect(chosen(given)).toBe('jpod');
    // A plain press now plays the sound chosen, as in Yomitan.
    onPlayAudio(given, 0);
    await flushPromises();
    expect(asked).toEqual(['(first)', 'jpod', 'jpod']);

    (options.onAddCard as (word: LookupResult, index: number) => void)(given, 0);
    await flushPromises();
    expect(addCard).toHaveBeenCalledWith(given, true, '', 'jpod');
    play.mockRestore();
  });

  it('shows the text as large as the user chose, without tags or compact if they chose so', async () => {
    const shown = open(server({ 食べる: [result('食べる')] }), {
      look: { textSize: 80, hideTags: false, compact: false },
    });
    await shown.setProps({ request: request('食べる') });
    await flushPromises();
    expect(drawn.options).toMatchObject({
      scale: 80,
      display: { glossaryLayoutMode: 'default' },
    });
    expect(drawn.options!.css).not.toContain('part-of-speech-info');

    await shown.setProps({ look: { textSize: 115, hideTags: true, compact: true } });
    const look = drawn.setTheme.mock.calls.at(-1)![0] as { scale: number; css: string };
    expect(look).toMatchObject({ scale: 115, display: { glossaryLayoutMode: 'compact' } });
    expect(look.css).toContain('[data-sc-content="part-of-speech-info"]');
    expect(look.css).toContain('overscroll-behavior: contain');
  });

  it('closes on Escape, and on a tap outside it but not on another word', async () => {
    const shown = open(server({ 食べる: [result('食べる')] }));
    await shown.setProps({ request: request('食べる') });
    await flushPromises();

    const word = document.createElement('p');
    word.dataset.wordText = '';
    document.body.append(word);
    word.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(shown.emitted('close')).toBeUndefined();

    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(shown.emitted('close')).toHaveLength(1);

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(shown.emitted('close')).toHaveLength(2);
    word.remove();
  });
});
