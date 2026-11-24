import { expect, test } from '../support/fixtures';
import { signInToJellyfin } from '../support/webClient';

test.describe('the installed app', () => {
  test('can be installed, and opens without the server to say it cannot reach it', async ({
    page,
    context,
    jellyfin,
    player,
  }) => {
    const tv = player(jellyfin.viewer, 'App test', 'mpv');
    await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.locator('.line').first()).toBeVisible();

    // The worker is there and has kept the page's files.
    const scope = await page.evaluate(async () => (await navigator.serviceWorker.ready).scope);
    expect(scope).toBe(`${jellyfin.baseUrl}/SubtitleMiner/`);

    // Chromium finds nothing that keeps the page from being installed.
    const cdp = await context.newCDPSession(page);
    const { installabilityErrors } = (await cdp.send('Page.getInstallabilityErrors')) as {
      installabilityErrors: { errorId: string }[];
    };
    expect(installabilityErrors).toEqual([]);
    const { errors, url } = (await cdp.send('Page.getAppManifest')) as {
      errors: unknown[];
      url: string;
    };
    expect(errors).toEqual([]);
    expect(url).toBe(`${jellyfin.baseUrl}/SubtitleMiner/manifest.webmanifest`);

    // What the worker keeps: the page's files and nothing the plugin answered for this user. The
    // caches of Jellyfin's web client share the address and are left alone.
    const ours = (await page.evaluate(() => caches.keys())).filter((name) =>
      name.startsWith('subtitle-miner-page-'),
    );
    expect(ours).toEqual([`subtitle-miner-page-${jellyfin.plugin.version}`]);
    const kept = await page.evaluate(async (name) => {
      const requests = await (await caches.open(name)).keys();
      return requests.map((request) => request.url);
    }, ours[0]!);
    expect(kept).toContain(`${jellyfin.baseUrl}/SubtitleMiner/`);
    expect(kept.filter((kept) => !kept.startsWith(`${jellyfin.baseUrl}/SubtitleMiner/`))).toEqual(
      [],
    );
    expect(kept.filter((kept) => kept.includes('/Api/'))).toEqual([]);

    // Without the server the page still opens, and says what is wrong.
    await page.reload();
    await expect(page.locator('.line').first()).toBeVisible();
    expect(await page.evaluate(() => navigator.serviceWorker.controller !== null)).toBe(true);
    await context.setOffline(true);
    await page.reload();
    await expect(
      page.getByRole('heading', { name: 'The server could not be reached' }),
    ).toBeVisible();

    await context.setOffline(false);
    await page.getByRole('button', { name: 'Try again' }).click();
    await expect(page.locator('.line').first()).toBeVisible();
  });
});
