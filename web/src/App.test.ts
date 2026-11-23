import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';

import App from './App.vue';
import { WEB_CLIENT_KEY } from './auth/credentials';
import {
  env,
  fakeServer,
  json,
  MemoryStorage,
  problem,
  status,
  webClientLogins,
} from './testing/fakes';

/** Stands in for the window: tests fire its storage event by hand. */
function fakeEvents() {
  const listeners = new Set<(event: StorageEvent) => void>();
  return {
    addEventListener: ((type: string, listener: (event: StorageEvent) => void) => {
      if (type === 'storage') listeners.add(listener);
    }) as Window['addEventListener'],
    removeEventListener: ((type: string, listener: (event: StorageEvent) => void) => {
      if (type === 'storage') listeners.delete(listener);
    }) as Window['removeEventListener'],
    fire(key: string | null): void {
      for (const listener of [...listeners]) listener({ key } as StorageEvent);
    },
    count: () => listeners.size,
  };
}

describe('the page', () => {
  it('signs in by itself when Jellyfin is signed in in this browser', async () => {
    const storage = new MemoryStorage();
    storage.setItem(WEB_CLIENT_KEY, webClientLogins({ token: 'web-token' }));
    const server = fakeServer(json(status({ UserName: 'mika' })));

    const page = mount(App, { props: { env: env(server.fetch, storage), events: fakeEvents() } });
    await flushPromises();

    expect(page.get('[data-testid="signed-in-as"]').text()).toBe('Signed in as mika');
    expect(page.get('[data-testid="versions"]').text()).toBe(
      'Subtitle Miner 2.0.0.0 on Home, Jellyfin 12.4.0',
    );
    expect(page.find('form').exists()).toBe(false);
    // The login belongs to Jellyfin's web client, so the page offers no sign-out for it.
    expect(page.findAll('button').map((button) => button.text())).not.toContain('Sign out');
  });

  it('asks for a name and password when the browser holds no login, and signs in with them', async () => {
    const server = fakeServer(
      json({ AccessToken: 'fresh', User: { Id: 'u', Name: 'mika' } }),
      json(status({ UserName: 'mika' })),
    );

    const page = mount(App, { props: { env: env(server.fetch), events: fakeEvents() } });
    await flushPromises();
    expect(page.get('h1').text()).toBe('Sign in');
    expect(page.get('a.button').attributes('href')).toBe('https://jellyfin.example/base/web/');

    await page.get('#sign-in-name').setValue('mika');
    await page.get('#sign-in-password').setValue('secret');
    await page.get('form').trigger('submit');
    await flushPromises();

    expect(page.get('[data-testid="signed-in-as"]').text()).toBe('Signed in as mika');
    expect(page.findAll('button').map((button) => button.text())).toContain('Sign out');
  });

  it('shows why a sign-in was refused and lets the user try again', async () => {
    const server = fakeServer(problem(401, 'Invalid username or password entered.'));

    const page = mount(App, { props: { env: env(server.fetch), events: fakeEvents() } });
    await flushPromises();
    await page.get('#sign-in-name').setValue('mika');
    await page.get('#sign-in-password').setValue('wrong');
    await page.get('form').trigger('submit');
    await flushPromises();

    expect(page.get('[role="alert"]').text()).toBe('That name or password is not right.');
    expect(page.get('button[type="submit"]').attributes('disabled')).toBeUndefined();
  });

  it('does not send an empty name', async () => {
    const server = fakeServer();

    const page = mount(App, { props: { env: env(server.fetch), events: fakeEvents() } });
    await flushPromises();
    await page.get('form').trigger('submit');
    await flushPromises();

    expect(server.sent).toHaveLength(0);
  });

  it('goes on by itself once someone signs in to Jellyfin in another tab', async () => {
    const storage = new MemoryStorage();
    const events = fakeEvents();
    const server = fakeServer(json(status({ UserName: 'mika' })));

    const page = mount(App, { props: { env: env(server.fetch, storage), events } });
    await flushPromises();
    expect(page.get('h1').text()).toBe('Sign in');

    events.fire('some-other-key');
    await flushPromises();
    expect(server.sent).toHaveLength(0);

    storage.setItem(WEB_CLIENT_KEY, webClientLogins({ token: 'web-token' }));
    events.fire(WEB_CLIENT_KEY);
    await flushPromises();

    expect(page.get('[data-testid="signed-in-as"]').text()).toBe('Signed in as mika');
  });

  it('asks for a sign-in again once Jellyfin is signed out in another tab', async () => {
    const storage = new MemoryStorage();
    storage.setItem(WEB_CLIENT_KEY, webClientLogins({ token: 'web-token' }));
    const events = fakeEvents();
    const server = fakeServer(json(status()));

    const page = mount(App, { props: { env: env(server.fetch, storage), events } });
    await flushPromises();
    expect(page.find('[data-testid="signed-in-as"]').exists()).toBe(true);

    storage.removeItem(WEB_CLIENT_KEY);
    events.fire(null);
    await flushPromises();

    expect(page.get('h1').text()).toBe('Sign in');
  });

  it('signs out of its own login and shows the sign-in again', async () => {
    const server = fakeServer(
      json({ AccessToken: 'fresh', User: { Id: 'u', Name: 'mika' } }),
      json(status()),
      new Response(null, { status: 204 }),
    );

    const page = mount(App, { props: { env: env(server.fetch), events: fakeEvents() } });
    await flushPromises();
    await page.get('#sign-in-name').setValue('mika');
    await page.get('form').trigger('submit');
    await flushPromises();

    const signOut = page.findAll('button').find((button) => button.text() === 'Sign out');
    await signOut?.trigger('click');
    await flushPromises();

    expect(server.sent.at(-1)?.url).toBe('https://jellyfin.example/base/Sessions/Logout');
    expect(page.get('h1').text()).toBe('Sign in');
  });

  it('says that the server cannot be reached and tries again when asked', async () => {
    const storage = new MemoryStorage();
    storage.setItem(WEB_CLIENT_KEY, webClientLogins({ token: 'web-token' }));
    const server = fakeServer(new TypeError('Failed to fetch'), json(status({ UserName: 'mika' })));

    const page = mount(App, { props: { env: env(server.fetch, storage), events: fakeEvents() } });
    await flushPromises();
    expect(page.get('h1').text()).toBe('The server could not be reached');

    await page.get('button').trigger('click');
    await flushPromises();

    expect(page.get('[data-testid="signed-in-as"]').text()).toBe('Signed in as mika');
  });

  it('stops listening when it is closed', async () => {
    const events = fakeEvents();
    const page = mount(App, { props: { env: env(fakeServer().fetch), events } });
    await flushPromises();
    expect(events.count()).toBe(1);

    page.unmount();

    expect(events.count()).toBe(0);
  });
});
