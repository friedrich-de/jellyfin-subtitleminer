import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Line, SettingsView, Watch } from '@/api/types';
import type { Session } from '@/auth/signIn';
import { type Collection, createCollection } from '@/testing/ankiCollection';
import {
  env,
  eventStream,
  type EventStream,
  fakeRoutes,
  json,
  MemoryStorage,
  problem,
  type RoutedRequest,
  status,
} from '@/testing/fakes';
import { TRACKS_KEY } from '@/watch/remembered';
import { line, sampleWatch } from '@/watch/samples';

import MainScreen from './MainScreen.vue';

const JAPANESE: Line[] = [
  line(0, 1_000, 3_500, '昨日の話、覚えてる？'),
  line(1, 4_000, 6_000, 'もちろん。\n約束したからね。'),
  line(2, 6_500, 9_000, '駅前商店街', { Kind: 'sign', Style: 'Sign' }),
  line(3, 10_000, 13_000, 'じゃあ、明日の朝に行こう。'),
  line(4, 14_000, 17_000, '夜空に星が降る', { Kind: 'song' }),
];

const ENGLISH: Line[] = [
  line(0, 1_000, 3_500, 'Do you remember?'),
  line(1, 4_000, 6_000, 'Of course.'),
  line(2, 10_000, 13_000, "Let's go tomorrow."),
];

const session: Session = { token: 'token-1', source: 'jellyfin', status: status() };

/** Cards go to an AnkiConnect on a server, which the plugin calls with the key. */
function serverSettings(page?: unknown): SettingsView {
  return {
    Anki: {
      Where: 'server',
      DeviceAddress: 'http://127.0.0.1:8765',
      DeviceKey: '',
      ServerAddress: 'https://anki.example.org',
      ServerKeySet: true,
      ServerAllowed: true,
    },
    ...(page === undefined ? {} : { Page: page }),
  };
}

/** The plugin behind the page: a stream of news the test writes to, and the item endpoints. */
function plugin(
  lines: Record<string, Line[] | Response> = {},
  user: { settings?: SettingsView; anki?: Collection } = {},
) {
  const streams: EventStream[] = [];
  const server = fakeRoutes((request: RoutedRequest) => {
    if (request.path === 'Settings' && user.settings !== undefined) return json(user.settings);

    // The plugin passes the call on to the user's AnkiConnect, with the key.
    if (request.path === 'Anki' && user.anki !== undefined) {
      const call = request.body as { Action: string; Params?: unknown };
      return json(user.anki.answer({ action: call.Action, version: 6, params: call.Params }));
    }

    const toAnki = /^Items\/([^/]+)\/(Picture|Audio)\/Anki$/.exec(request.path);
    if (toAnki !== null && user.anki !== undefined) {
      const name = `subtitle-miner_${toAnki[2] === 'Picture' ? 'picture.jpg' : 'sound.mp3'}`;
      user.anki.handle('storeMediaFile', { filename: name, data: btoa(toAnki[2]!) });
      return json({ FileName: name });
    }

    if (request.path === 'Events') {
      const stream = eventStream();
      request.signal?.addEventListener('abort', () =>
        stream.fail(new DOMException('Aborted', 'AbortError')),
      );
      streams.push(stream);
      return stream.response;
    }

    const subtitles = /^Items\/([^/]+)\/Subtitles$/.exec(request.path);
    if (subtitles !== null) {
      const key = request.query.get('track') ?? '';
      const found = lines[key] ?? (key.startsWith('x') ? JAPANESE : undefined);
      if (found instanceof Response) return found;
      return found === undefined
        ? problem(404, 'This video has no such subtitle track.')
        : json({ Key: key, Format: 'ass', Lines: found });
    }

    if (/^Items\/[^/]+\/Picture$/.test(request.path)) {
      return new Response(new Blob(['picture'], { type: 'image/jpeg' }));
    }

    if (/^Items\/[^/]+\/Audio$/.test(request.path)) {
      return new Response(new Blob(['sound'], { type: 'audio/mpeg' }));
    }

    return undefined;
  });
  return {
    ...server,
    streams,
    send: (watches: Watch[]): void => streams.at(-1)!.send(watches),
    asked: (path: string) => server.sent.filter((request) => request.path.endsWith(path)),
  };
}

let page: VueWrapper | undefined;

/** The addresses of pictures and sounds the page let go of. */
let revoked: string[] = [];

async function open(server: ReturnType<typeof plugin>, storage = new MemoryStorage()) {
  page = mount(MainScreen, {
    props: { env: env(server.fetch, storage), session },
    attachTo: document.body,
  });
  await flushPromises();
  return page;
}

async function settle(ms = 0): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await flushPromises();
}

const texts = (wrapper: VueWrapper): string[] =>
  wrapper.findAll('.line .said').map((said) => said.text());

describe('the main screen', () => {
  beforeEach(() => {
    vi.useFakeTimers({
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'performance', 'Date'],
    });
    let next = 0;
    revoked = [];
    vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:test/${(next += 1)}`);
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation((url) => revoked.push(url));
  });

  afterEach(() => {
    page?.unmount();
    page = undefined;
    vi.useRealTimers();
  });

  it('says that nothing plays, and names the user to play as', async () => {
    const server = plugin();
    const screen = await open(server);
    expect(screen.text()).toContain('Looking for what you watch');

    server.send([]);
    await settle();

    expect(screen.get('[data-testid="nothing-playing"]').text()).toContain('signed in as mika');
    expect(server.sent[0]?.headers.authorization).toBe('MediaBrowser Token="token-1"');
  });

  it('shows the episode that plays, with its Japanese lines and the line being said', async () => {
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch({ PositionMs: 4_500 })]);
    await settle();

    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Show · S1 E3');
    expect(screen.get('[data-testid="where"]').text()).toBe('Living room (mpv) · 00:04 of 23:40');

    // The player shows English, but the page reads the Japanese file next to the video.
    const asked = server.asked('Subtitles')[0]!;
    expect(asked.query.get('track')).toBe('x0a1b2c3d4e5f');
    expect(asked.query.get('source')).toBe(sampleWatch().MediaSourceId);
    expect(screen.get<HTMLSelectElement>('[data-testid="pick-subtitles"]').element.value).toBe(
      'x0a1b2c3d4e5f',
    );

    // Signs are hidden while there is dialogue; the song stays.
    expect(texts(screen)).toEqual([
      '昨日の話、覚えてる？',
      'もちろん。\n約束したからね。',
      'じゃあ、明日の朝に行こう。',
      '夜空に星が降る',
    ]);
    expect(screen.get('.line .said').attributes('lang')).toBe('ja');
    expect(screen.get('[aria-current="time"] .said').text()).toBe('もちろん。\n約束したからね。');

    // Time goes on without a word from the server.
    await settle(6_000);
    expect(screen.get('[data-testid="where"]').text()).toContain('00:10 of 23:40');
    expect(screen.get('[aria-current="time"] .said').text()).toBe('じゃあ、明日の朝に行こう。');
  });

  it('stops the clock on a pause and says when a watch ended', async () => {
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch({ PositionMs: 4_500, Paused: true })]);
    await settle(5_000);

    expect(screen.get('[data-testid="where"]').text()).toBe(
      'Living room (mpv) · 00:04 of 23:40 · paused',
    );

    server.send([sampleWatch({ Playing: false, PositionMs: 20_000, StoppedAgoMs: 7 * 60_000 })]);
    await settle();
    expect(screen.get('[data-testid="where"]').text()).toBe(
      'Living room (mpv) · 00:20 of 23:40 · ended 7 min ago',
    );
    expect(screen.find('[aria-current="time"]').exists()).toBe(false);
  });

  it('shows the signs when asked to', async () => {
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();

    const signs = screen
      .findAll('[role="switch"]')
      .find((button) => button.text() === 'Signs (1)')!;
    await signs.trigger('click');

    expect(texts(screen)).toContain('駅前商店街');
    expect(screen.get('.line.quiet .kind').text()).toBe('Sign');
  });

  it('puts the meaning under each line and remembers both tracks for the next episode', async () => {
    const storage = new MemoryStorage();
    const server = plugin({ s1: ENGLISH });
    const screen = await open(server, storage);
    server.send([sampleWatch()]);
    await settle();

    await screen.get('[data-testid="pick-second"]').setValue('s1');
    await settle();
    expect(screen.findAll('.line .second').map((second) => second.text())).toEqual([
      'Do you remember?',
      'Of course.',
      "Let's go tomorrow.",
    ]);
    expect(screen.get('.line .second').attributes('lang')).toBe('en');
    expect(JSON.parse(storage.getItem(TRACKS_KEY)!)).toMatchObject({
      'show:Sample Show': { second: { key: 's1', language: 'eng', title: 'English' } },
    });

    // The next episode: its file next to it has another key, and the choice still holds.
    const next = sampleWatch({
      Id: 'watch-2',
      ItemId: 'ffffffffbbbbccccddddeeeeeeeeeeee',
      Episode: 4,
      Subtitles: [
        { ...sampleWatch().Subtitles[0]!, Key: 'x999999999999' },
        ...sampleWatch().Subtitles.slice(1),
      ],
    });
    server.send([next]);
    await settle();

    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Show · S1 E4');
    expect(server.asked('Subtitles').map((request) => request.query.get('track'))).toEqual([
      'x0a1b2c3d4e5f',
      's1',
      'x999999999999',
      's1',
    ]);
    expect(screen.findAll('.line .second')).toHaveLength(3);
  });

  it('moves on with the chosen player and keeps a watched episode open when one was chosen', async () => {
    const server = plugin();
    const screen = await open(server);
    const kitchen = sampleWatch({ Id: 'kitchen', Player: 'Kitchen', Episode: 7 });
    const living = sampleWatch();
    server.send([kitchen, living]);
    await settle();
    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Show · S1 E7');

    await screen.get('[data-testid="pick-watch"]').setValue('watch-1');
    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Show · S1 E3');

    // The living room moves on to the next episode, and the page with it.
    const next = sampleWatch({
      Id: 'watch-2',
      ItemId: 'ffffffffbbbbccccddddeeeeeeeeeeee',
      Episode: 4,
    });
    server.send([kitchen, next, { ...living, Playing: false, StoppedAgoMs: 0 }]);
    await settle();
    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Show · S1 E4');

    // Choosing the episode that ended keeps it, though the player plays on.
    await screen.get('[data-testid="pick-watch"]').setValue('watch-1');
    server.send([kitchen, next, { ...living, Playing: false, StoppedAgoMs: 1_000 }]);
    await settle();
    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Show · S1 E3');
  });

  it('selects lines one by one and as a range, and previews the picture and the sound', async () => {
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch({ AudioKey: 'a0' })]);
    await settle();

    const checks = screen.findAll('.line .check');
    await checks[0]!.trigger('click');
    await checks[2]!.trigger('click', { shiftKey: true });
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('3 lines selected');
    expect(screen.findAll('[aria-checked="true"].check')).toHaveLength(3);
    expect(screen.text()).toContain('00:01 to 00:13, 12.0 seconds');

    // Nothing is cut until the choice settles.
    expect(server.asked('Picture')).toHaveLength(0);
    await settle(300);
    const picture = server.asked('Picture')[0]!;
    expect(picture.query.get('at')).toBe('2250');
    expect(picture.query.get('width')).toBe('640');
    const sound = server.asked('Audio')[0]!;
    // A quarter of a second more on each side, as the settings have it by default.
    expect([sound.query.get('start'), sound.query.get('end'), sound.query.get('track')]).toEqual([
      '750',
      '13250',
      'a0',
    ]);
    expect(screen.get('[data-testid="preview-picture"] img').attributes('src')).toMatch(/^blob:/);
    expect(screen.get('[data-testid="sound"]').attributes('src')).toMatch(/^blob:/);
    expect(screen.get('[data-testid="play-sound"]').attributes('disabled')).toBeUndefined();

    // One line less: the old cut is thrown away and a new one made.
    await checks[0]!.trigger('click');
    await settle(300);
    expect(server.asked('Audio').at(-1)?.query.get('start')).toBe('3750');
    expect(revoked).toContain('blob:test/1');

    await screen
      .findAll('button')
      .find((button) => button.text() === 'Clear')!
      .trigger('click');
    expect(screen.find('[data-testid="selected-count"]').exists()).toBe(false);
  });

  it('says why nothing can be cut, and refuses more than a minute of sound', async () => {
    const long = [line(0, 1_000, 40_000, 'ながい'), line(1, 40_000, 70_000, 'もっと')];
    const server = plugin({ x0a1b2c3d4e5f: long });
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();

    for (const check of screen.findAll('.line .check')) await check.trigger('click');
    await settle(300);
    expect(screen.text()).toContain('More than a minute. Choose fewer lines.');
    expect(server.asked('Audio')).toHaveLength(0);

    server.send([sampleWatch({ CanCut: false })]);
    await settle(300);
    expect(screen.get('[data-testid="preview-picture"]').text()).toContain(
      'cannot read this video',
    );
  });

  it('says what went wrong when a track cannot be read, and tries again when asked', async () => {
    let refuse = true;
    const server = plugin({
      get x0a1b2c3d4e5f(): Line[] | Response {
        return refuse ? problem(422, 'The subtitle track could not be read.') : JAPANESE;
      },
    });
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();
    expect(screen.get('[role="alert"]').text()).toContain('The subtitle track could not be read.');

    refuse = false;
    await screen
      .findAll('button')
      .find((button) => button.text() === 'Try again')!
      .trigger('click');
    await settle();
    expect(texts(screen)).toHaveLength(4);
  });

  it('explains a video whose subtitles are pictures only', async () => {
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch({ Subtitles: [sampleWatch().Subtitles[3]!] })]);
    await settle();

    expect(screen.text()).toContain('Its subtitles are pictures, not text');
  });

  it('stops following when the user scrolls, and follows again when asked', async () => {
    const scrolled: string[] = [];
    vi.spyOn(HTMLElement.prototype, 'scrollIntoView').mockImplementation(function (
      this: HTMLElement,
    ) {
      scrolled.push(this.dataset.line ?? '');
    });
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch({ PositionMs: 4_500 })]);
    await settle();
    expect(scrolled.at(-1)).toBe('1');

    await screen.get('main').trigger('wheel');
    const follow = screen
      .findAll('[role="switch"]')
      .find((button) => button.text() === 'Follow playback')!;
    expect(follow.attributes('aria-checked')).toBe('false');
    await settle(6_000);
    expect(scrolled.at(-1)).toBe('1');

    await follow.trigger('click');
    await settle();
    expect(scrolled.at(-1)).toBe('3');
  });

  it('shows that the connection broke and keeps what it had meanwhile', async () => {
    const server = plugin();
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();

    server.streams[0]!.close();
    await settle();
    expect(screen.get('.banner').text()).toContain('Trying again');
    expect(texts(screen)).toHaveLength(4);

    await settle(1_000);
    server.send([sampleWatch()]);
    await settle();
    expect(screen.find('.banner').exists()).toBe(false);
  });

  it('adds the lines, picture and sound to the card Yomitan just made, and takes it back', async () => {
    const anki = createCollection();
    anki.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: {
        Expression: '約束',
        Sentence: '<b>約束</b>したからね。',
        MainDefinition: 'promise',
      },
    });
    const server = plugin({}, { settings: serverSettings(), anki });
    const screen = await open(server);
    server.send([sampleWatch({ AudioKey: 'a0' })]);
    await settle();

    await screen.findAll('.line .check')[1]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-to-newest"]').trigger('click');
    await settle();

    // Lapis's fields are guessed by their names; the word Yomitan looked up stays bold.
    const note = [...anki.notes.values()][0]!;
    expect(note.fields).toMatchObject({
      Expression: '約束',
      MainDefinition: 'promise',
      Sentence: 'もちろん。<br><b>約束</b>したからね。',
      Picture: '<img src="subtitle-miner_picture.jpg">',
      SentenceAudio: '[sound:subtitle-miner_sound.mp3]',
      MiscInfo: 'Sample Show · S1 E3 · 00:04',
    });
    expect([...anki.media.keys()]).toEqual([
      'subtitle-miner_picture.jpg',
      'subtitle-miner_sound.mp3',
    ]);
    // The sound is cut with the quarter second before and after it, as previewed.
    const cut = server.asked('Audio/Anki')[0]!;
    expect([cut.query.get('start'), cut.query.get('end')]).toEqual(['3750', '6250']);
    expect(screen.get('[data-testid="sending"]').text()).toContain('Added to "約束".');

    await screen.get('[data-testid="undo"]').trigger('click');
    await settle();
    expect(note.fields.Sentence).toBe('<b>約束</b>したからね。');
    expect(note.fields.Picture).toBe('');
    expect(screen.get('[data-testid="sending"]').text()).toContain('The card is as it was.');
  });

  it('makes a new card in the chosen deck, and refuses a card made too long ago', async () => {
    // In the afternoon, so that the card of an hour ago was made today.
    vi.setSystemTime(new Date(2026, 10, 24, 15, 0));
    const anki = createCollection();
    anki.makeNote({
      id: Date.now() - 60 * 60_000,
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '昨日' },
    });
    const page = {
      anki: {
        deck: 'Mining',
        noteType: 'Lapis',
        fields: { Expression: 'sentence', Sentence: 'sentence', Picture: 'picture' },
        tags: ['mined'],
        newestWithinMinutes: 15,
      },
    };
    const server = plugin({}, { settings: serverSettings(page), anki });
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();

    await screen.findAll('.line .check')[0]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-to-newest"]').trigger('click');
    await settle();
    expect(screen.get('[data-testid="sending"]').text()).toContain(
      'Your newest card was made 60 minutes ago',
    );
    expect(anki.calls.map((call) => call.action)).not.toContain('updateNoteFields');

    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();
    const added = [...anki.notes.values()].at(-1)!;
    expect(added).toMatchObject({
      deck: 'Mining',
      modelName: 'Lapis',
      tags: ['mined'],
      fields: { Expression: '昨日の話、覚えてる？', Sentence: '昨日の話、覚えてる？' },
    });
    expect(added.fields.Picture).toBe('<img src="subtitle-miner_picture.jpg">');
    // Nothing asks for the sound, so none is cut.
    expect(server.asked('Audio/Anki')).toHaveLength(0);
    expect(screen.get('[data-testid="sending"]').text()).toContain(
      'Made a new card, "昨日の話、覚えてる？".',
    );
    expect(screen.find('[data-testid="undo"]').exists()).toBe(false);

    // The same card again would be a duplicate in the deck.
    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();
    expect(screen.get('[data-testid="sending"]').text()).toContain(
      'This deck has a card like this already.',
    );
  });

  it('gives a new card the meaning once, though it spans two chosen lines', async () => {
    const anki = createCollection();
    const page = {
      anki: {
        deck: 'Mining',
        noteType: 'Basic',
        fields: { Front: 'sentence', Back: 'translation' },
      },
    };
    const both = [line(0, 1_000, 6_000, 'Do you remember? Of course.')];
    const server = plugin({ s1: both }, { settings: serverSettings(page), anki });
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();
    await screen.get('[data-testid="pick-second"]').setValue('s1');
    await settle();

    const checks = screen.findAll('.line .check');
    await checks[0]!.trigger('click');
    await checks[1]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();

    expect([...anki.notes.values()][0]!.fields).toEqual({
      Front: '昨日の話、覚えてる？<br>もちろん。<br>約束したからね。',
      Back: 'Do you remember? Of course.',
    });
  });

  it('offers to set up Anki when cards go nowhere, and opens the settings', async () => {
    const settings = {
      ...serverSettings(),
      Anki: { ...serverSettings().Anki, Where: 'off' as const },
    };
    const server = plugin({}, { settings });
    const screen = await open(server);
    server.send([sampleWatch()]);
    await settle();

    await screen.findAll('.line .check')[0]!.trigger('click');
    expect(screen.find('[data-testid="add-to-newest"]').exists()).toBe(false);
    await screen.get('[data-testid="set-up-anki"]').trigger('click');
    await settle();
    expect(screen.find('[data-testid="where-off"]').exists()).toBe(true);
    expect(screen.get('[data-testid="open-settings"]').attributes('aria-pressed')).toBe('true');

    await screen.get('[data-testid="settings-back"]').trigger('click');
    expect(screen.findAll('.line .check')).not.toHaveLength(0);
  });

  it('tells the app when the login has ended', async () => {
    const server = fakeRoutes(() => new Response(null, { status: 401 }));
    page = mount(MainScreen, { props: { env: env(server.fetch), session } });
    await settle();

    expect(page.emitted('signedOut')).toHaveLength(1);
  });

  it('closes the connection when the page is closed', async () => {
    const server = plugin();
    const screen = await open(server);
    screen.unmount();
    page = undefined;

    expect(server.sent[0]!.signal?.aborted).toBe(true);
  });
});
