import { setTimeout as sleep } from 'node:timers/promises';

import type { Watch } from '../../src/api/types';
import { expect, signIn, test, tokenHeader } from '../support/fixtures';
import { SHOW, VIDEO_MS } from '../support/media';
import type { PretendPlayer } from '../support/player';
import { API, NewsStream, watchesOf, watchOf } from '../support/watching';

/** A track of a video, as Jellyfin itself lists it. */
interface JellyfinStream {
  Type: string;
  Index: number;
  IsExternal: boolean;
  Language?: string;
}

const on =
  (player: PretendPlayer, more: (watch: Watch) => boolean = () => true) =>
  (watch: Watch): boolean =>
    watch.Player === player.device && more(watch);

test.describe('what is playing', () => {
  test('is told to nobody without a login', async ({ request, jellyfin }) => {
    const item = jellyfin.media.episode1.id;
    for (const path of [
      'Watches',
      'Events',
      `Items/${item}/Subtitles?track=s0`,
      `Items/${item}/Picture?at=1000`,
      `Items/${item}/Audio?start=1000&end=2000`,
    ]) {
      expect((await request.get(`${API}/${path}`)).status(), path).toBe(401);
    }
  });

  test('lists an episode with its name and tracks while a player has it open', async ({
    request,
    jellyfin,
    player,
  }) => {
    const viewer = await signIn(request, jellyfin.viewer);
    const tv = player(jellyfin.viewer, 'Kitchen', 'mpv');

    // Jellyfin numbers the tracks, and counts the subtitle file next to the video first.
    const item = (await (
      await request.get(`/Items/${jellyfin.media.episode1.id}?userId=${viewer.userId}`, {
        headers: tokenHeader(viewer.token),
      })
    ).json()) as { MediaStreams: JellyfinStream[] };
    const english = item.MediaStreams.find(
      (stream) => stream.Type === 'Subtitle' && !stream.IsExternal && stream.Language === 'eng',
    )!;
    const commentary = item.MediaStreams.find(
      (stream) => stream.Type === 'Audio' && stream.Language === 'eng',
    )!;

    await tv.play(jellyfin.media.episode1.id, {
      positionMs: 4_000,
      audioIndex: commentary.Index,
      subtitleIndex: english.Index,
    });
    const opened = Date.now();
    const watch = await watchOf(request, viewer.token, on(tv), 'the episode on the player');

    expect(watch).toMatchObject({
      ItemId: jellyfin.media.episode1.id,
      Playing: true,
      Paused: false,
      Player: 'Kitchen',
      Client: 'mpv',
      Name: jellyfin.media.episode1.name,
      SeriesName: SHOW,
      Season: 1,
      Episode: 1,
      CanCut: true,
      SubtitleKey: 's1',
      AudioKey: 'a1',
    });
    expect(watch.StoppedAgoMs).toBeUndefined();
    expect(Math.abs((watch.RuntimeMs ?? 0) - VIDEO_MS)).toBeLessThan(500);
    expect(
      watch.Subtitles.map((track) => [track.Key.slice(0, 1), track.External, track.Readable]),
    ).toEqual([
      ['x', true, true],
      ['s', false, true],
      ['s', false, true],
    ]);
    expect(watch.Subtitles[0]?.Key).toMatch(/^x[0-9a-f]{12}$/);
    expect(watch.Subtitles[0]).toMatchObject({ Language: 'jpn', Codec: 'subrip' });
    expect(watch.Subtitles[1]).toMatchObject({
      Key: 's0',
      Language: 'jpn',
      Title: 'Japanese',
      Default: true,
    });
    expect(watch.Subtitles[2]).toMatchObject({ Key: 's1', Language: 'eng', Title: 'English' });
    expect(watch.Audio).toMatchObject([
      { Key: 'a0', Language: 'jpn' },
      { Key: 'a1', Language: 'eng', Title: 'Commentary' },
    ]);

    // The position moves on with the clock, without the player saying anything.
    await sleep(1_500);
    const later = await watchOf(request, viewer.token, on(tv), 'the episode');
    const expected = 4_000 + (Date.now() - opened);
    expect(later.PositionMs).toBeGreaterThan(5_000);
    expect(Math.abs(later.PositionMs - expected)).toBeLessThan(1_200);

    // Nobody else sees it.
    const admin = await signIn(request, jellyfin.admin);
    expect((await watchesOf(request, admin.token)).filter(on(tv))).toEqual([]);

    // Paused, it stays where it is.
    await tv.report({ positionMs: 9_000, paused: true });
    await watchOf(
      request,
      viewer.token,
      on(tv, (w) => w.Paused),
      'the pause',
    );
    await sleep(700);
    const paused = await watchOf(request, viewer.token, on(tv), 'the episode');
    expect(paused).toMatchObject({ Playing: true, Paused: true, PositionMs: 9_000 });

    // Closed, it stays listed as watched, where it ended.
    await tv.stop(9_500);
    const over = await watchOf(
      request,
      viewer.token,
      on(tv, (w) => !w.Playing),
      'the end',
    );
    expect(over).toMatchObject({
      Playing: false,
      PositionMs: 9_500,
      Name: jellyfin.media.episode1.name,
    });
    expect(over.StoppedAgoMs).toBeGreaterThanOrEqual(0);
    expect(over.StoppedAgoMs).toBeLessThan(10_000);
  });

  test('follows a player to the next episode and keeps the one before as watched', async ({
    request,
    jellyfin,
    player,
  }) => {
    const viewer = await signIn(request, jellyfin.viewer);
    const tv = player(jellyfin.viewer, 'Bedroom', 'Jellyfin Media Player');

    await tv.play(jellyfin.media.episode1.id, { positionMs: 25_000 });
    await watchOf(
      request,
      viewer.token,
      on(tv, (w) => w.Episode === 1 && w.Playing),
      'episode 1',
    );

    // A player that moves on does not always say that it closed what it played before.
    await tv.play(jellyfin.media.episode2.id);
    const second = await watchOf(
      request,
      viewer.token,
      on(tv, (w) => w.Episode === 2 && w.Playing),
      'episode 2',
    );
    expect(second).toMatchObject({
      Name: jellyfin.media.episode2.name,
      Client: 'Jellyfin Media Player',
    });

    // Episode 2 has no subtitle file next to it.
    expect(second.Subtitles.map((track) => track.Key)).toEqual(['s0', 's1']);
    expect(second.SubtitleKey).toBeUndefined();

    const list = (await watchesOf(request, viewer.token)).filter(on(tv));
    expect(list.map((watch) => [watch.Episode, watch.Playing])).toEqual([
      [2, true],
      [1, false],
    ]);

    // What plays is listed before what is over.
    const all = await watchesOf(request, viewer.token);
    expect(all.findIndex((watch) => !watch.Playing)).toBe(
      all.filter((watch) => watch.Playing).length,
    );
    await tv.stop();
  });

  test('knows a video that was opened paused is paused', async ({ request, jellyfin, player }) => {
    const viewer = await signIn(request, jellyfin.viewer);
    const tv = player(jellyfin.viewer, 'Paused at start', 'mpv');

    await tv.play(jellyfin.media.episode1.id, { positionMs: 7_000, paused: true });
    const watch = await watchOf(request, viewer.token, on(tv), 'the episode');

    expect(watch).toMatchObject({ Playing: true, Paused: true, PositionMs: 7_000 });
  });

  test('never lists a video the user may not see', async ({ request, jellyfin, player }) => {
    const admin = await signIn(request, jellyfin.admin);
    const viewer = await signIn(request, jellyfin.viewer);
    const tv = player(jellyfin.admin, 'Study', 'mpv');

    await tv.play(jellyfin.media.film.id);
    const film = await watchOf(request, admin.token, on(tv), 'the film');
    expect(film).toMatchObject({ Name: jellyfin.media.film.name });
    expect(film.SeriesName).toBeUndefined();
    expect(film.Episode).toBeUndefined();

    const theirs = await watchesOf(request, viewer.token);
    expect(theirs.map((watch) => watch.ItemId)).not.toContain(jellyfin.media.film.id);
    await tv.stop();
  });
});

test.describe('the stream of news', () => {
  test('sends the list at once, and again when the player starts, pauses, jumps, changes track or stops', async ({
    request,
    jellyfin,
    baseURL,
    player,
  }) => {
    const viewer = await signIn(request, jellyfin.viewer);
    const tv = player(jellyfin.viewer, 'Hall', 'mpv');
    const stream = await NewsStream.open(baseURL!, viewer.token);
    try {
      expect(stream.status).toBe(200);
      expect(stream.contentType).toBe('text/event-stream');
      await expect.poll(() => stream.lists.length, { message: 'the first list' }).toBe(1);

      let from = stream.lists.length;
      await tv.play(jellyfin.media.episode2.id, { positionMs: 2_000 });
      const started = await stream.after(
        from,
        on(tv, (w) => w.Playing),
        'the start',
      );
      expect(started).toMatchObject({ Episode: 2, Paused: false });

      // Steady playback is no news: the page moves the position with its own clock.
      from = stream.lists.length;
      await sleep(1_000);
      await tv.report({ positionMs: 3_000 });
      await sleep(1_000);
      await tv.report({ positionMs: 4_000 });
      await sleep(500);
      expect(stream.lists.length - from).toBe(0);

      from = stream.lists.length;
      await tv.report({ positionMs: 5_000, paused: true });
      const paused = await stream.after(
        from,
        on(tv, (w) => w.Paused),
        'the pause',
      );
      expect(paused.PositionMs).toBe(5_000);

      from = stream.lists.length;
      await tv.report({ positionMs: 20_000, paused: false });
      const jumped = await stream.after(
        from,
        on(tv, (w) => !w.Paused && w.PositionMs >= 20_000),
        'the jump',
      );
      expect(jumped.PositionMs).toBeLessThan(21_500);

      // Episode 2 has no subtitle file, so Jellyfin counts: picture 0, sound 1 and 2, subtitles 3 and 4.
      expect(jumped.Subtitles.map((track) => track.Key)).toEqual(['s0', 's1']);
      expect(jumped.SubtitleKey).toBeUndefined();
      from = stream.lists.length;
      await tv.report({ positionMs: 21_000, subtitleIndex: 4 });
      await stream.after(
        from,
        on(tv, (w) => w.SubtitleKey === 's1'),
        'the other track',
      );

      from = stream.lists.length;
      await tv.stop(22_000);
      const over = await stream.after(
        from,
        on(tv, (w) => !w.Playing),
        'the end',
      );
      expect(over).toMatchObject({ PositionMs: 22_000 });
    } finally {
      await stream.close();
    }
  });

  test('is each user’s own, and shows signs of life while nothing happens', async ({
    request,
    jellyfin,
    baseURL,
    player,
  }) => {
    test.setTimeout(60_000);
    const admin = await signIn(request, jellyfin.admin);
    const tv = player(jellyfin.viewer, 'Attic', 'mpv');
    const stream = await NewsStream.open(baseURL!, admin.token);
    try {
      await expect.poll(() => stream.lists.length).toBe(1);

      // Another user's player tells this stream nothing.
      await tv.play(jellyfin.media.episode1.id);
      await tv.report({ positionMs: 10_000, paused: true });
      await tv.stop();
      await sleep(1_000);
      expect(stream.lists).toHaveLength(1);

      // Within its 15 seconds the server writes a comment, which keeps the connection from being cut.
      const arrivals = stream.arrivals;
      await expect
        .poll(() => stream.arrivals, { message: 'a sign of life', timeout: 20_000 })
        .toBeGreaterThan(arrivals);
      expect(stream.lists).toHaveLength(1);
    } finally {
      await stream.close();
    }
  });
});
