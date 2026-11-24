import type { Page } from '@playwright/test';

import { expect, signIn, test, tokenHeader } from '../support/fixtures';
import { signInToJellyfin } from '../support/webClient';

/** The login Jellyfin's web client keeps in this browser. */
async function jellyfinToken(page: Page): Promise<string | null> {
  return page.evaluate(() => {
    const stored = JSON.parse(localStorage.getItem('jellyfin_credentials') ?? '{}') as {
      Servers?: { AccessToken?: string }[];
    };
    return stored.Servers?.[0]?.AccessToken ?? null;
  });
}

const signInHeading = { name: 'Sign in', level: 1 } as const;

test.describe('with a login of Jellyfin in the browser', () => {
  test('the page needs no sign-in of its own', async ({ page, jellyfin }) => {
    await signInToJellyfin(page, jellyfin.viewer);

    await page.goto('/SubtitleMiner/');
    await page.getByTestId('account').click();
    await expect(page.getByTestId('signed-in-as')).toHaveText('Signed in as viewer');
    await expect(page.getByTestId('signed-in-as')).toBeVisible();
    await expect(page.getByRole('heading', signInHeading)).toHaveCount(0);
    await expect(page.getByTestId('versions')).toContainText(
      `Subtitle Miner ${jellyfin.plugin.version}`,
    );
    await expect(page.getByTestId('versions')).toContainText(`Jellyfin ${jellyfin.serverVersion}`);

    // The login belongs to Jellyfin, so this page offers no way to end it.
    await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0);
    expect(await page.evaluate(() => localStorage.getItem('subtitle-miner.session'))).toBeNull();
  });

  test('the page asks for a sign-in once that login has ended', async ({
    page,
    request,
    jellyfin,
  }) => {
    await signInToJellyfin(page, jellyfin.viewer);
    await page.goto('/SubtitleMiner/');
    await expect(page.getByTestId('account')).toHaveText('viewer');

    const token = await jellyfinToken(page);
    expect(token).not.toBeNull();
    const ended = await request.post('/Sessions/Logout', { headers: tokenHeader(token ?? '') });
    expect(ended.status()).toBe(204);

    await page.reload();
    await expect(page.getByRole('heading', signInHeading)).toBeVisible();
    // What Jellyfin's web client stored is its own. The page reads it and never changes it.
    expect(await jellyfinToken(page)).toBe(token);
  });

  test('an open page follows a sign-in made in another tab', async ({
    page,
    context,
    jellyfin,
  }) => {
    await page.goto('/SubtitleMiner/');
    await expect(page.getByRole('heading', signInHeading)).toBeVisible();

    const other = await context.newPage();
    await signInToJellyfin(other, jellyfin.admin);

    await expect(page.getByTestId('signed-in-as')).toHaveText('Signed in as admin');
    await expect(page.getByRole('heading', signInHeading)).toHaveCount(0);
    await other.close();
  });
});

test.describe('without a login of Jellyfin in the browser', () => {
  test('the page signs in with a Jellyfin name and password and keeps the login', async ({
    page,
    request,
    jellyfin,
  }) => {
    await page.goto('/SubtitleMiner/');
    await expect(page.getByRole('heading', signInHeading)).toBeVisible();

    // A wrong password first.
    await page.getByLabel('Name').fill(jellyfin.viewer.name);
    await page.getByLabel('Password').fill('not-the-password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toHaveText('That name or password is not right.');
    await expect(page.getByLabel('Name')).toHaveValue(jellyfin.viewer.name);

    await page.getByLabel('Password').fill(jellyfin.viewer.password);
    await page.getByLabel('Password').press('Enter');
    await expect(page.getByTestId('signed-in-as')).toHaveText('Signed in as viewer');
    await expect(page.getByRole('alert')).toHaveCount(0);

    // Still signed in after the page is opened again, without the form.
    await page.reload();
    await expect(page.getByTestId('signed-in-as')).toHaveText('Signed in as viewer');
    await expect(page.getByRole('heading', signInHeading)).toHaveCount(0);

    // Jellyfin lists the page as a device of its own, under its name.
    const admin = await signIn(request, jellyfin.admin);
    const sessions = (await (
      await request.get('/Sessions', { headers: tokenHeader(admin.token) })
    ).json()) as { Client: string; UserName: string; ApplicationVersion: string }[];
    const ours = sessions.filter((session) => session.Client === 'Subtitle Miner');
    expect(ours).toHaveLength(1);
    expect(ours[0]?.UserName).toBe('viewer');
    expect(ours[0]?.ApplicationVersion).toBe(jellyfin.plugin.version);

    // Signing out ends the login on the server too.
    const token = await page.evaluate(() => {
      const stored = JSON.parse(localStorage.getItem('subtitle-miner.session') ?? '{}') as {
        accessToken?: string;
      };
      return stored.accessToken ?? '';
    });
    expect(token).not.toBe('');
    await page.getByTestId('account').click();
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page.getByRole('heading', signInHeading)).toBeVisible();
    expect(await page.evaluate(() => localStorage.getItem('subtitle-miner.session'))).toBeNull();
    const after = await request.get('/SubtitleMiner/Api/Status', { headers: tokenHeader(token) });
    expect(after.status()).toBe(401);
  });

  test('the sign-in form leads to Jellyfin', async ({ page }) => {
    await page.goto('/SubtitleMiner/');
    const link = page.getByRole('link', { name: 'Open Jellyfin' });
    await expect(link).toBeVisible();
    await link.click();
    await page.waitForURL(/\/web\//);
    await expect(page.locator('#txtManualName')).toBeVisible();
  });
});

test.describe('the addresses of the page', () => {
  test('work without the slash at the end and from the address of version 1', async ({ page }) => {
    for (const address of ['/SubtitleMiner', '/Plugins/JellyfinMiner/', '/Plugins/JellyfinMiner']) {
      await page.goto(address);
      expect(new URL(page.url()).pathname, address).toBe('/SubtitleMiner/');
      await expect(page.getByRole('heading', signInHeading), address).toBeVisible();
    }
  });
});

test.describe('the look of the page', () => {
  test('has the plain dark surfaces, the bundled font and no sideways scrolling', async ({
    page,
  }) => {
    await page.goto('/SubtitleMiner/');
    await expect(page.getByRole('heading', signInHeading)).toBeVisible();
    await page.evaluate(() => document.fonts.ready);

    const look = await page.evaluate(() => {
      const button = document.querySelector<HTMLElement>('button[type="submit"]');
      const input = document.querySelector<HTMLElement>('input');
      return {
        background: getComputedStyle(document.body).backgroundColor,
        font: document.fonts.check('500 15px "Noto Sans"'),
        loaded: [...document.fonts].filter((face) => face.status === 'loaded').length,
        buttonBackground: button ? getComputedStyle(button).backgroundColor : '',
        buttonHeight: button?.getBoundingClientRect().height ?? 0,
        inputHeight: input?.getBoundingClientRect().height ?? 0,
        sideways: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      };
    });
    expect(look.background).toBe('rgb(16, 16, 16)');
    expect(look.buttonBackground).toBe('rgb(0, 164, 220)');
    expect(look.font).toBe(true);
    expect(look.loaded).toBeGreaterThan(0);
    // Big enough for a thumb.
    expect(look.buttonHeight).toBeGreaterThanOrEqual(44);
    expect(look.inputHeight).toBeGreaterThanOrEqual(44);
    expect(look.sideways).toBe(0);
  });
});
