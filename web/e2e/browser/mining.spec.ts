import type { Page } from '@playwright/test';

import { expect, test } from '../support/fixtures';
import { COLOURS, SPOKEN } from '../support/media';
import { isColour } from '../support/probe';
import { signInToJellyfin } from '../support/webClient';

/** The Japanese lines the page shows for episode 1: everything but the sign. */
const SHOWN = [
  '昨日の話、覚えてる？',
  'もちろん。\n約束したからね。',
  'じゃあ、明日の朝に行こう。',
  '夜空に星が降る',
  '早起きは苦手なんだけど…',
  '大丈夫、起こしてあげる。',
  'ありがとう。',
];

async function shownLines(page: Page): Promise<string[]> {
  return page
    .locator('.line .said')
    .evaluateAll((lines) => lines.map((said) => (said as HTMLElement).innerText));
}

/** The average colour of the preview's picture, read in the page. */
async function previewColour(page: Page): Promise<[number, number, number]> {
  return page
    .getByTestId('preview-picture')
    .locator('img')
    .evaluate(async (img: HTMLImageElement) => {
      await img.decode();
      const canvas = document.createElement('canvas');
      canvas.width = 1;
      canvas.height = 1;
      const context = canvas.getContext('2d')!;
      context.drawImage(img, 0, 0, 1, 1);
      const [red, green, blue] = context.getImageData(0, 0, 1, 1).data;
      return [red!, green!, blue!] as [number, number, number];
    });
}

test.describe('the main screen', () => {
  test('follows what a player plays, line by line', async ({ page, jellyfin, player }) => {
    const tv = player(jellyfin.viewer, 'Living room', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 4_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');

    await expect(page.getByTestId('title')).toHaveText(`${jellyfin.media.show} · S1 E1`);
    await expect(page.getByTestId('where')).toHaveText(
      '00:04 of 00:30 · paused · Living room (mpv)',
    );
    await expect(page.locator('.line')).toHaveCount(SHOWN.length);
    expect(await shownLines(page)).toEqual(SHOWN);
    await expect(page.locator('[aria-current="time"] .said')).toHaveText(SHOWN[1]!);
    await expect(page.locator('.line .said').first()).toHaveAttribute('lang', 'ja');

    // The player jumps on: the page follows within moments, with no reload.
    await tv.report({ positionMs: 11_000, paused: true });
    await expect(page.locator('[aria-current="time"] .said')).toHaveText(SHOWN[2]!);
    await expect(page.getByTestId('where')).toContainText('00:11 of 00:30');

    // Playing, the clock runs on in the page.
    await tv.report({ positionMs: 17_500, paused: false });
    await expect(page.locator('[aria-current="time"] .said')).toHaveText(SHOWN[4]!, {
      timeout: 5_000,
    });

    // The player goes on to the next episode, and the page with it.
    await tv.play(jellyfin.media.episode2.id, { positionMs: 1_500, paused: true });
    await expect(page.getByTestId('title')).toHaveText(`${jellyfin.media.show} · S1 E2`);
    await expect(page.locator('[aria-current="time"] .said')).toHaveText(SHOWN[0]!);

    // The one before is still there to choose.
    const options = await page.getByTestId('pick-watch').locator('option').allTextContents();
    expect(options.some((option) => option.includes('S1 E1 · watched'))).toBe(true);
  });

  test('shows the signs and the meaning under each line when asked', async ({
    page,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'Study', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line')).toHaveCount(SHOWN.length);

    await page.getByRole('switch', { name: 'Signs (1)' }).click();
    await expect(page.locator('.line')).toHaveCount(SHOWN.length + 1);
    await expect(page.locator('.line.quiet .said').first()).toHaveText('駅前商店街');
    await page.getByRole('switch', { name: 'Signs (1)' }).click();

    await page.getByTestId('pick-second').selectOption('s1');
    await expect(page.locator('.line .second')).toHaveCount(SPOKEN.length);
    await expect(page.locator('.line .second').first()).toHaveText(SPOKEN[0]!.english);
    await expect(page.locator('.line .second').first()).toHaveAttribute('lang', 'en');

    // The other episode of the show gets the same choice.
    await tv.play(jellyfin.media.episode2.id, { positionMs: 1_500, paused: true });
    await expect(page.getByTestId('title')).toContainText('S1 E2');
    await expect(page.locator('.line .second')).toHaveCount(SPOKEN.length);

    // The file next to episode 1 is plain lines, Shift-JIS on disk.
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await expect(page.getByTestId('title')).toContainText('S1 E1');
    const file = page.getByTestId('pick-subtitles').locator('option', { hasText: 'file' });
    await page.getByTestId('pick-subtitles').selectOption(await file.getAttribute('value'));
    await expect(page.locator('.line')).toHaveCount(SPOKEN.length);
    expect(await shownLines(page)).toEqual(SPOKEN.map((line) => line.japanese));
  });

  test('previews the picture and the sound of the chosen lines', async ({
    page,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'Desk', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line')).toHaveCount(SHOWN.length);

    await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
    await page.getByRole('checkbox', { name: 'Line at 00:04' }).click();
    await expect(page.getByTestId('selected-count')).toHaveText('2 lines selected');
    await expect(page.getByText('00:01 to 00:06, 5.0 seconds')).toBeVisible();

    // The picture from the middle of the first line, 2.25 s in: the first colour.
    await expect(page.getByTestId('preview-picture').locator('img')).toBeVisible();
    expect(isColour(await previewColour(page), COLOURS[0]!.rgb)).toBe(true);

    // The sound of both lines and a quarter second on each side. The API tests check that it is
    // the track the player plays.
    const sound = page.getByTestId('sound');
    await expect(sound).toHaveAttribute('src', /^blob:/);
    await expect
      .poll(() => sound.evaluate((audio: HTMLAudioElement) => audio.duration), {
        message: 'the sound is five and a half seconds long',
      })
      .toBeGreaterThan(5.3);
    expect(await sound.evaluate((audio: HTMLAudioElement) => audio.duration)).toBeLessThan(5.8);
    await expect(page.getByTestId('play-sound')).toBeEnabled();

    // A range with the shift key, then nothing.
    await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
    await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
    await page.getByRole('checkbox', { name: 'Line at 00:10' }).click({ modifiers: ['Shift'] });
    await expect(page.getByTestId('selected-count')).toHaveText('3 lines selected');

    // A line apart from them starts anew: the lines of a card are always next to each other.
    await page.getByRole('checkbox', { name: 'Line at 00:22' }).click();
    await expect(page.getByTestId('selected-count')).toHaveText('1 line selected');
    await expect(page.getByRole('checkbox', { name: 'Line at 00:01' })).not.toBeChecked();
    await page.getByRole('button', { name: 'Clear' }).click();
    await expect(page.getByTestId('selected-count')).toHaveCount(0);
  });

  test('goes back to the line playing after the user scrolled away', async ({
    page,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'Den', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 19_000, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('[aria-current="time"] .said')).toHaveText(SHOWN[4]!);
    await expect(page.getByTestId('go-to-playing')).toHaveCount(0);

    await page.locator('.line').first().hover();
    await page.mouse.wheel(0, -2_000);
    const back = page.getByTestId('go-to-playing');
    await expect(back).toBeVisible();
    await expect(page.getByRole('switch', { name: 'Follow playback' })).toHaveAttribute(
      'aria-checked',
      'false',
    );

    await back.click();
    await expect(back).toHaveCount(0);
    await expect(page.locator('[aria-current="time"]')).toBeInViewport();
    await expect(page.getByRole('switch', { name: 'Follow playback' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });

  test('fits the screen and needs no sideways scrolling', async ({ page, jellyfin, player }) => {
    const tv = player(jellyfin.viewer, 'Phone test', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line')).toHaveCount(SHOWN.length);
    await page.getByRole('checkbox', { name: 'Line at 00:01' }).click();
    await expect(page.getByTestId('preview-picture').locator('img')).toBeVisible();

    const look = await page.evaluate(() => {
      const box = (selector: string): DOMRect =>
        document.querySelector(selector)!.getBoundingClientRect();
      return {
        sideways: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        checkWidth: box('.line .check').width,
        selectHeight: box('select').height,
        barBottom: box('.bottom').bottom,
        height: window.innerHeight,
        current: getComputedStyle(document.querySelector('.line.current')!).backgroundColor,
      };
    });
    expect(look.sideways).toBe(0);
    expect(look.checkWidth).toBeGreaterThanOrEqual(44);
    expect(look.selectHeight).toBeGreaterThanOrEqual(44);
    // The bar with the chosen lines stays at the bottom of the screen.
    expect(Math.abs(look.barBottom - look.height)).toBeLessThan(2);
    // The line being said is marked in the accent's colour, mixed into the background.
    expect(look.current).not.toBe('rgba(0, 0, 0, 0)');
  });
});
