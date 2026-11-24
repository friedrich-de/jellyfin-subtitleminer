import { expect, test, tokenHeader } from '../support/fixtures';

/**
 * Jellyfin is often opened at a plain http:// address in the home network, such as
 * http://192.168.1.10:8096. The browser does not trust such an address: crypto.randomUUID, service
 * workers and the like are missing there. These tests open the page at a name that leads to the
 * test server but is not this device, which the browser trusts no more than that.
 */
const NAME = 'jellyfin.test';
const chromium = process.env.SUBTITLE_MINER_E2E_CHROMIUM;

test.use({
  launchOptions: {
    ...(chromium === undefined || chromium === '' ? {} : { executablePath: chromium }),
    args: [`--host-resolver-rules=MAP ${NAME} 127.0.0.1`],
  },
});

test('the page signs in and follows playback at an address the browser does not trust', async ({
  page,
  request,
  baseURL,
  jellyfin,
  player,
}) => {
  const address = new URL('/SubtitleMiner/', baseURL);
  address.hostname = NAME;
  const tv = player(jellyfin.viewer, 'Living room', 'mpv');
  await tv.play(jellyfin.media.episode1.id, { positionMs: 1_500, paused: true });

  await page.goto(address.href);
  expect(await page.evaluate(() => window.isSecureContext)).toBe(false);
  expect(await page.evaluate(() => typeof crypto.randomUUID)).toBe('undefined');

  await page.getByLabel('Name').fill(jellyfin.viewer.name);
  await page.getByLabel('Password').fill(jellyfin.viewer.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  try {
    await expect(page.getByTestId('signed-in-as')).toHaveText('Signed in as viewer');
    await expect(page.locator('.line[data-line="0"]')).toBeVisible();
  } finally {
    // The login is ended, so the other tests find no device of this test in Jellyfin.
    const token = await page.evaluate(() => {
      const stored = JSON.parse(localStorage.getItem('subtitle-miner.session') ?? '{}') as {
        accessToken?: string;
      };
      return stored.accessToken ?? '';
    });
    if (token !== '') await request.post('/Sessions/Logout', { headers: tokenHeader(token) });
  }
});
