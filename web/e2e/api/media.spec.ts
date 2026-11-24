import type { APIRequestContext, APIResponse } from '@playwright/test';

import type { Subtitles, Track } from '../../src/api/types';
import { expect, type Login, signIn, test, tokenHeader } from '../support/fixtures';
import {
  COLOUR_MS,
  COLOURS,
  FIRST_TRACK_EARLY_HZ,
  FIRST_TRACK_LATE_HZ,
  LINES,
  SECOND_TRACK_HZ,
  SPOKEN,
  VIDEO_HEIGHT,
  VIDEO_WIDTH,
} from '../support/media';
import { colourOf, isColour, probe, soundOf } from '../support/probe';
import { API, watchOf } from '../support/watching';

let viewer: Login;
let admin: Login;
let episode: string;
let film: string;

/** The subtitle tracks of episode 1, as the plugin names them: the file next to the video, Japanese, English. */
let tracks: Track[];

function get(request: APIRequestContext, login: Login, path: string): Promise<APIResponse> {
  return request.get(`${API}/${path}`, { headers: tokenHeader(login.token) });
}

async function problem(response: APIResponse, status: number): Promise<string> {
  expect(response.status(), await response.text()).toBe(status);
  return ((await response.json()) as { detail?: string }).detail ?? '';
}

async function file(response: APIResponse, type: string): Promise<Buffer> {
  expect(response.status(), response.status() === 200 ? '' : await response.text()).toBe(200);
  expect(response.headers()['content-type']).toBe(type);
  expect(response.headers()['cache-control']).toBe('no-store');
  expect(response.headers()['x-content-type-options']).toBe('nosniff');
  return response.body();
}

test.beforeAll(async ({ request, jellyfin, player }) => {
  viewer = await signIn(request, jellyfin.viewer);
  admin = await signIn(request, jellyfin.admin);
  episode = jellyfin.media.episode1.id;
  film = jellyfin.media.film.id;

  // The page learns a video's tracks from a watch, so a player opens the episode for a moment.
  const tv = player(jellyfin.viewer, 'Tracks', 'mpv');
  await tv.play(episode);
  const watch = await watchOf(
    request,
    viewer.token,
    (w) => w.Player === 'Tracks' && w.Episode === 1,
    'the episode',
  );
  await tv.stop();
  tracks = watch.Subtitles;
  expect(tracks).toHaveLength(3);
});

test.describe('the subtitles of a video', () => {
  test('are read from the file next to it, whatever its encoding', async ({ request }) => {
    const response = await get(
      request,
      viewer,
      `Items/${episode}/Subtitles?track=${tracks[0]!.Key}`,
    );
    expect(response.status(), await response.text()).toBe(200);
    const subtitles = (await response.json()) as Subtitles;

    // The file is Shift-JIS with Windows line ends.
    expect(subtitles).toMatchObject({ Key: tracks[0]!.Key, Format: 'srt' });
    expect(subtitles.Lines.map((line) => [line.StartMs, line.EndMs, line.Text, line.Kind])).toEqual(
      SPOKEN.map((line) => [line.startMs, line.endMs, line.japanese, 'dialogue']),
    );
    expect(subtitles.Lines.map((line) => line.Id)).toEqual(SPOKEN.map((_, index) => index));
  });

  test('are cut out of the video with their styles, speakers, signs and songs', async ({
    request,
  }) => {
    const japanese = (await (
      await get(request, viewer, `Items/${episode}/Subtitles?track=s0`)
    ).json()) as Subtitles;
    expect(japanese.Format).toBe('ass');
    expect(
      japanese.Lines.map((line) => [line.StartMs, line.EndMs, line.Text, line.Kind, line.Name]),
    ).toEqual(LINES.map((line) => [line.startMs, line.endMs, line.japanese, line.kind, line.name]));
    expect(japanese.Lines.map((line) => line.Style)).toEqual(
      LINES.map((line) => ({ dialogue: 'Default', sign: 'Sign', song: 'OP' })[line.kind]),
    );

    const english = (await (
      await get(request, viewer, `Items/${episode}/Subtitles?track=s1`)
    ).json()) as Subtitles;
    expect(english.Format).toBe('srt');
    expect(english.Lines.map((line) => [line.StartMs, line.EndMs, line.Text])).toEqual(
      SPOKEN.map((line) => [line.startMs, line.endMs, line.english]),
    );
  });

  test('are refused for a track that is not there', async ({ request }) => {
    for (const track of ['s9', 'a0', 'x000000000000', '../../../etc/passwd']) {
      const response = await get(
        request,
        viewer,
        `Items/${episode}/Subtitles?track=${encodeURIComponent(track)}`,
      );
      expect(await problem(response, 404), track).toContain('no such subtitle track');
    }

    expect((await get(request, viewer, `Items/${episode}/Subtitles`)).status()).toBe(400);
    expect((await get(request, viewer, `Items/not-an-id/Subtitles?track=s0`)).status()).toBe(400);
  });
});

test.describe('a video the user may not see', () => {
  test('gives away nothing, not even that it exists', async ({ request }) => {
    // Jellyfin itself does not show the viewer the film.
    const own = await request.get(`/Items/${film}?userId=${viewer.userId}`, {
      headers: tokenHeader(viewer.token),
    });
    expect(own.status()).toBe(404);

    const missing = '0123456789abcdef0123456789abcdef';
    for (const path of ['Subtitles?track=s0', 'Picture?at=1000', 'Audio?start=1000&end=2000']) {
      const hidden = await problem(await get(request, viewer, `Items/${film}/${path}`), 404);
      const unknown = await problem(await get(request, viewer, `Items/${missing}/${path}`), 404);
      expect(hidden, path).toBe('This video is not in your library.');
      expect(unknown, path).toBe(hidden);
    }

    // The administrator has the library it is in.
    const allowed = await get(request, admin, `Items/${film}/Subtitles?track=s1`);
    expect(allowed.status(), await allowed.text()).toBe(200);
    expect(((await allowed.json()) as Subtitles).Lines).toHaveLength(SPOKEN.length);
  });
});

test.describe('a picture cut from a video', () => {
  test('shows the moment asked for', async ({ request }) => {
    for (const at of [2_000, 7_500, 12_400, 19_900, 20_100, 27_000]) {
      const picture = await file(
        await get(request, viewer, `Items/${episode}/Picture?at=${at}`),
        'image/jpeg',
      );
      const colour = COLOURS[Math.floor(at / COLOUR_MS)]!;
      const seen = await colourOf(picture);
      expect(
        isColour(seen, colour.rgb),
        `${at} ms is ${colour.name}, the picture is ${seen.join(', ')}`,
      ).toBe(true);
    }
  });

  test('comes in the size and format asked for', async ({ request }) => {
    // A video narrower than the wanted width keeps its size.
    const full = await probe(
      await file(await get(request, viewer, `Items/${episode}/Picture?at=1000`), 'image/jpeg'),
    );
    expect(full).toMatchObject({
      codec: 'mjpeg',
      width: VIDEO_WIDTH,
      height: VIDEO_HEIGHT,
      frames: 1,
    });

    const small = await probe(
      await file(
        await get(request, viewer, `Items/${episode}/Picture?at=1000&width=160&quality=40`),
        'image/jpeg',
      ),
    );
    expect(small).toMatchObject({ width: 160, height: 90 });

    for (const [format, codec] of [
      ['webp', 'webp'],
      ['avif', 'av1'],
    ] as const) {
      const picture = await file(
        await get(request, viewer, `Items/${episode}/Picture?at=11000&format=${format}&width=200`),
        `image/${format}`,
      );
      expect(await probe(picture), format).toMatchObject({ codec, width: 200, frames: 1 });
      expect(isColour(await colourOf(picture), COLOURS[2]!.rgb), format).toBe(true);
    }
  });

  test('can move for as long as the lines last', async ({ request }) => {
    const picture = await file(
      await get(request, viewer, `Items/${episode}/Picture?at=4000&end=6000&format=webp&width=160`),
      'image/webp',
    );

    // Two seconds at twelve pictures a second, starting in the first colour.
    expect(picture.subarray(0, 4).toString('latin1')).toBe('RIFF');
    expect(picture.includes(Buffer.from('ANIM'))).toBe(true);
    const frames = picture.toString('latin1').split('ANMF').length - 1;
    expect(frames).toBeGreaterThanOrEqual(22);
    expect(frames).toBeLessThanOrEqual(26);
  });

  test('is refused when it cannot be made', async ({ request }) => {
    const cases: [string, string][] = [
      ['Picture?at=31000', 'not within the video'],
      ['Picture?at=-5', 'not within the video'],
      ['Picture?at=1000&format=bmp', 'jpeg, webp or avif'],
      ['Picture?at=4000&end=6000', 'moving picture is made as webp or avif'],
      ['Picture?at=6000&end=4000&format=webp', 'not within the video'],
    ];
    for (const [path, reason] of cases) {
      expect(
        await problem(await get(request, viewer, `Items/${episode}/${path}`), 422),
        path,
      ).toContain(reason);
    }

    expect((await get(request, viewer, `Items/${episode}/Picture`)).status()).toBe(400);
  });
});

test.describe('sound cut from a video', () => {
  test('is the stretch and the track asked for', async ({ request }) => {
    const cases: [string, string, number, number][] = [
      ['Audio?start=2000&end=4500', 'audio/mpeg', FIRST_TRACK_EARLY_HZ, 2.5],
      ['Audio?start=16000&end=18000', 'audio/mpeg', FIRST_TRACK_LATE_HZ, 2],
      ['Audio?start=16000&end=18000&track=a0', 'audio/mpeg', FIRST_TRACK_LATE_HZ, 2],
      ['Audio?start=2000&end=3500&track=a1', 'audio/mpeg', SECOND_TRACK_HZ, 1.5],
      ['Audio?start=2000&end=4000&format=opus&bitrate=64', 'audio/ogg', FIRST_TRACK_EARLY_HZ, 2],
      ['Audio?start=20000&end=23000&normalize=true', 'audio/mpeg', FIRST_TRACK_LATE_HZ, 3],
      [
        'Audio?start=20000&end=23000&normalize=true&format=opus',
        'audio/ogg',
        FIRST_TRACK_LATE_HZ,
        3,
      ],
    ];
    for (const [path, type, hz, seconds] of cases) {
      const sound = await soundOf(
        await file(await get(request, viewer, `Items/${episode}/${path}`), type),
      );
      expect(Math.abs(sound.hz - hz), `${path} hums at ${sound.hz} Hz`).toBeLessThan(12);
      expect(Math.abs(sound.seconds - seconds), `${path} lasts ${sound.seconds} s`).toBeLessThan(
        0.2,
      );
    }
  });

  test('has two channels and the format asked for', async ({ request }) => {
    const mp3 = await probe(
      await file(
        await get(request, viewer, `Items/${episode}/Audio?start=1000&end=2000`),
        'audio/mpeg',
      ),
    );
    expect(mp3).toMatchObject({ format: 'mp3', codec: 'mp3', channels: 2 });

    const opus = await probe(
      await file(
        await get(request, viewer, `Items/${episode}/Audio?start=1000&end=2000&format=opus`),
        'audio/ogg',
      ),
    );
    expect(opus).toMatchObject({ format: 'ogg', codec: 'opus', channels: 2 });
  });

  test('is refused when it cannot be made', async ({ request }) => {
    const cases: [string, number, string][] = [
      ['Audio?start=31000&end=32000', 422, 'not within the video'],
      ['Audio?start=3000&end=2000', 422, 'not within the video'],
      ['Audio?start=1000&end=2000&format=flac', 422, 'mp3 or opus'],
      ['Audio?start=1000&end=2000&track=a2', 404, 'no such audio track'],
      ['Audio?start=1000&end=2000&track=s0', 404, 'no such audio track'],
    ];
    for (const [path, status, reason] of cases) {
      expect(
        await problem(await get(request, viewer, `Items/${episode}/${path}`), status),
        path,
      ).toContain(reason);
    }

    expect((await get(request, viewer, `Items/${episode}/Audio?start=1000`)).status()).toBe(400);
  });
});

test.describe('many cuts at once', () => {
  test('wait their turn, and one user cannot have more than six in the works', async ({
    request,
  }) => {
    const moving = (at: number): Promise<APIResponse> =>
      get(
        request,
        viewer,
        `Items/${episode}/Picture?at=${at}&end=${at + 8000}&format=avif&width=320`,
      );
    const answers = await Promise.all(
      [0, 2, 4, 6, 8, 10, 12, 14, 16, 18].map((s) => moving(s * 1000)),
    );
    const statuses = answers.map((answer) => answer.status());

    expect(statuses.filter((status) => status === 200).length).toBeGreaterThanOrEqual(6);
    expect(statuses.filter((status) => status === 429).length).toBeGreaterThanOrEqual(1);
    expect(statuses.filter((status) => status !== 200 && status !== 429)).toEqual([]);
    const refused = answers.find((answer) => answer.status() === 429)!;
    expect(refused.headers()['retry-after']).toBe('2');
    expect(await problem(refused, 429)).toContain('Try again');

    // Another user is not held up by it, and afterwards there is room again.
    await file(await get(request, admin, `Items/${episode}/Picture?at=1000`), 'image/jpeg');
    await file(await get(request, viewer, `Items/${episode}/Picture?at=1000`), 'image/jpeg');
  });
});
