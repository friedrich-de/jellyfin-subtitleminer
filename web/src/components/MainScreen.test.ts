import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Line, Watch } from '@/api/types';
import type { Session } from '@/auth/signIn';
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

/** The plugin behind the page: a stream of news the test writes to, and the item endpoints. */
function plugin(lines: Record<string, Line[] | Response> = {}) {
  const streams: EventStream[] = [];
  const server = fakeRoutes((request: RoutedRequest) => {
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
    expect([sound.query.get('start'), sound.query.get('end'), sound.query.get('track')]).toEqual([
      '1000',
      '13000',
      'a0',
    ]);
    expect(screen.get('[data-testid="preview-picture"] img').attributes('src')).toMatch(/^blob:/);
    expect(screen.get('[data-testid="sound"]').attributes('src')).toMatch(/^blob:/);
    expect(screen.get('[data-testid="play-sound"]').attributes('disabled')).toBeUndefined();

    // One line less: the old cut is thrown away and a new one made.
    await checks[0]!.trigger('click');
    await settle(300);
    expect(server.asked('Audio').at(-1)?.query.get('start')).toBe('4000');
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
