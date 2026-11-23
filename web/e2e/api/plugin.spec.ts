import { expect, signIn, test, tokenHeader } from '../support/fixtures';

test.describe('the installed plugin', () => {
  test('is active in the version that was packaged', async ({ request, jellyfin }) => {
    const admin = await signIn(request, jellyfin.admin);
    const response = await request.get('/Plugins', { headers: tokenHeader(admin.token) });
    expect(response.status()).toBe(200);
    const plugins = (await response.json()) as {
      Name: string;
      Id: string;
      Version: string;
      Status: string;
    }[];
    const ours = plugins.filter((plugin) => plugin.Name === 'Subtitle Miner');
    expect(ours).toHaveLength(1);
    expect(ours[0]?.Status).toBe('Active');
    expect(ours[0]?.Version).toBe(jellyfin.plugin.version);
    expect(ours[0]?.Id.replaceAll('-', '')).toBe(jellyfin.plugin.guid.replaceAll('-', ''));
  });

  test('has a screen in the dashboard that leads to the page', async ({ request, jellyfin }) => {
    const admin = await signIn(request, jellyfin.admin);
    const pages = await request.get('/web/ConfigurationPages?enableInMainMenu=true', {
      headers: tokenHeader(admin.token),
    });
    expect(pages.status()).toBe(200);
    const listed = (await pages.json()) as {
      Name: string;
      DisplayName?: string;
      PluginId: string;
    }[];
    expect(listed.map((page) => page.Name)).toContain('SubtitleMiner');

    const screen = await request.get('/web/ConfigurationPage?name=SubtitleMiner', {
      headers: tokenHeader(admin.token),
    });
    expect(screen.status()).toBe(200);
    expect(await screen.text()).toContain("ApiClient.getUrl('SubtitleMiner/')");
  });
});

test.describe('the page and its files', () => {
  test('are served without a login, with a strict content policy', async ({ request }) => {
    const response = await request.get('/SubtitleMiner/');
    expect(response.status()).toBe(200);
    const headers = response.headers();
    expect(headers['content-type']).toBe('text/html; charset=utf-8');
    expect(headers['cache-control']).toBe('no-cache');
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['referrer-policy']).toBe('no-referrer');
    const policy = headers['content-security-policy'] ?? '';
    expect(policy).toContain("default-src 'none'");
    expect(policy).toContain("script-src 'self'");
    expect(policy).toContain("style-src 'self'");
    expect(policy).toContain("frame-ancestors 'none'");
    expect(policy).not.toContain('unsafe');

    const html = await response.text();
    expect(html).toContain('<title>Subtitle Miner</title>');
    expect(html).not.toMatch(/<script(?![^>]*\bsrc=)[^>]*>/);
    expect(html).not.toContain('<style');
  });

  test('link their files relatively, and those files may be kept for good', async ({ request }) => {
    const html = await (await request.get('/SubtitleMiner/')).text();
    const links = [...html.matchAll(/(?:src|href)="([^"]+)"/g)].map((match) => match[1] ?? '');
    expect(links.length).toBeGreaterThan(1);
    for (const link of links) expect(link).toMatch(/^\.\//);

    const script = links.find((link) => /^\.\/assets\/.+\.js$/.test(link));
    const style = links.find((link) => /^\.\/assets\/.+\.css$/.test(link));
    expect(script, 'the page links a script').toBeDefined();
    expect(style, 'the page links a style sheet').toBeDefined();

    for (const [link, type] of [
      [script, 'text/javascript; charset=utf-8'],
      [style, 'text/css; charset=utf-8'],
    ] as const) {
      const file = await request.get(`/SubtitleMiner/${link?.slice(2) ?? ''}`);
      expect(file.status()).toBe(200);
      expect(file.headers()['content-type']).toBe(type);
      expect(file.headers()['cache-control']).toBe('public, max-age=31536000, immutable');
      expect(file.headers()['x-content-type-options']).toBe('nosniff');
    }
  });

  test('send the address without a slash on to the one with it', async ({ request }) => {
    const response = await request.get('/SubtitleMiner?from=bookmark', { maxRedirects: 0 });
    expect(response.status()).toBe(302);
    // A relative address, so it also holds behind a reverse proxy with a path of its own.
    expect(response.headers().location).toBe('SubtitleMiner/?from=bookmark');
  });

  test('answer 404 for a file that is not there', async ({ request }) => {
    for (const path of ['nothing.js', 'assets/nothing.css', '..%2F..%2Fsystem.xml', 'Api']) {
      const response = await request.get(`/SubtitleMiner/${path}`);
      expect(response.status(), path).toBe(404);
    }
  });
});

test.describe('the address of version 1', () => {
  test('leads to the new page', async ({ request }) => {
    for (const [old, target] of [
      ['/Plugins/JellyfinMiner', '../SubtitleMiner/'],
      ['/Plugins/JellyfinMiner/', '../../SubtitleMiner/'],
      ['/Plugins/JellyfinMiner/index.html', '../../SubtitleMiner/'],
    ] as const) {
      const response = await request.get(old, { maxRedirects: 0 });
      expect(response.status(), old).toBe(302);
      expect(response.headers().location, old).toBe(target);
    }

    const followed = await request.get('/Plugins/JellyfinMiner/');
    expect(followed.status()).toBe(200);
    expect(new URL(followed.url()).pathname).toBe('/SubtitleMiner/');
  });

  test('no longer answers the old calls', async ({ request }) => {
    for (const path of ['api', 'api/sessions', 'api/health']) {
      const response = await request.get(`/Plugins/JellyfinMiner/${path}`, { maxRedirects: 0 });
      expect(response.status(), path).toBe(404);
    }
  });
});

test.describe('the status call', () => {
  test('refuses a caller without a login', async ({ request }) => {
    expect((await request.get('/SubtitleMiner/Api/Status')).status()).toBe(401);
    const wrong = await request.get('/SubtitleMiner/Api/Status', {
      headers: tokenHeader('not-a-token'),
    });
    expect(wrong.status()).toBe(401);
  });

  test('tells an administrator who they are and which versions run', async ({
    request,
    jellyfin,
  }) => {
    const admin = await signIn(request, jellyfin.admin);
    const response = await request.get('/SubtitleMiner/Api/Status', {
      headers: tokenHeader(admin.token),
    });
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('application/json');
    expect(await response.json()).toEqual({
      Version: jellyfin.plugin.version,
      UserId: admin.userId,
      UserName: jellyfin.admin.name,
      IsAdministrator: true,
      ServerName: expect.any(String),
      ServerVersion: jellyfin.serverVersion,
    });
  });

  test('tells a user without rights the same, as no administrator', async ({
    request,
    jellyfin,
  }) => {
    const viewer = await signIn(request, jellyfin.viewer);
    const response = await request.get('/SubtitleMiner/Api/Status', {
      headers: tokenHeader(viewer.token),
    });
    expect(response.status()).toBe(200);
    const status = (await response.json()) as Record<string, unknown>;
    expect(status.UserName).toBe(jellyfin.viewer.name);
    expect(status.UserId).toBe(viewer.userId);
    expect(status.IsAdministrator).toBe(false);
  });

  test('stops answering once the login has ended', async ({ request, jellyfin }) => {
    const viewer = await signIn(request, jellyfin.viewer);
    const headers = tokenHeader(viewer.token);
    expect((await request.get('/SubtitleMiner/Api/Status', { headers })).status()).toBe(200);
    expect((await request.post('/Sessions/Logout', { headers })).status()).toBe(204);
    expect((await request.get('/SubtitleMiner/Api/Status', { headers })).status()).toBe(401);
  });
});
