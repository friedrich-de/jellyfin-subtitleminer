import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createPluginApi } from '@/api/plugin';
import type {
  DictionariesView,
  DictionaryImport,
  DictionaryView,
  WordAudioView,
} from '@/api/types';
import { BASE, fakeRoutes, json, problem, type RoutedRequest } from '@/testing/fakes';

import DictionarySettings from './DictionarySettings.vue';

function dictionary(title: string, overrides: Partial<DictionaryView> = {}): DictionaryView {
  return {
    Id: title.toLowerCase(),
    Title: title,
    Revision: '2026.11.20',
    Enabled: true,
    Terms: 437_367,
    Frequencies: 0,
    Pitches: 0,
    Styles: '',
    Bytes: 211_300_000,
    Imported: '2026-11-24T01:00:00Z',
    ...overrides,
  };
}

/** The plugin's dictionaries as the server keeps them, with an import that takes two looks. */
function plugin(start: DictionaryView[], mayManage: boolean) {
  let list = [...start];
  let job: DictionaryImport = { State: 'idle', Progress: 0 };
  let looks = 0;
  let wordAudio: WordAudioView = { Set: false };
  const view = (): DictionariesView => ({
    Dictionaries: list,
    Import: job,
    MayManage: mayManage,
    WordAudio: mayManage ? wordAudio : { Set: wordAudio.Set },
  });
  const server = fakeRoutes((request: RoutedRequest) => {
    if (request.path === 'Dictionaries' && request.method === 'GET') {
      if (job.State === 'downloading' && (looks += 1) >= 2) {
        list = [...list, dictionary('Jitendex.org [2026-11-23]', { Id: 'jitendex' })];
        job = {
          State: 'done',
          Progress: 1,
          Title: 'Jitendex.org [2026-11-23]',
          Source: job.Source!,
        };
      } else if (job.State === 'downloading') {
        job = { ...job, Progress: 0.5 };
      }
      return json(view());
    }
    if (request.path === 'Dictionaries' && request.method === 'PUT') {
      const { Order, Disabled } = request.body as { Order: string[]; Disabled: string[] };
      list = Order.map((id) => {
        const found = list.find((item) => item.Id === id)!;
        return { ...found, Enabled: !Disabled.includes(id) };
      });
      return json(view());
    }
    if (request.path === 'Dictionaries/Download') {
      const { Url } = request.body as { Url: string };
      if (!Url.startsWith('https://')) return problem(400, 'Not an address.');
      job = { State: 'downloading', Progress: 0, Source: Url };
      return json(view(), 202);
    }
    if (request.path === 'Dictionaries' && request.method === 'POST') {
      job = { State: 'failed', Progress: 0, Error: 'That file is not a zip.' };
      return json(view(), 202);
    }
    const removed = /^Dictionaries\/(.+)$/.exec(request.path)?.[1];
    if (removed !== undefined && request.method === 'DELETE') {
      list = list.filter((item) => item.Id !== removed);
      return json(view());
    }
    if (request.path === 'WordAudio' && request.method === 'PUT') {
      const { Address } = request.body as { Address: string };
      if (Address === '') {
        wordAudio = { Set: false };
        return json({ WordAudio: wordAudio, Tried: 'none' });
      }
      if (!Address.includes('{term}')) return problem(400, 'The address needs {term}.');
      wordAudio = { Set: true, Host: new URL(Address).host };
      return json({ WordAudio: wordAudio, Tried: Address.includes('key=') ? 'found' : 'failed' });
    }
    return undefined;
  });
  return { ...server, list: () => list };
}

let screen: VueWrapper | undefined;

async function open(server: ReturnType<typeof plugin>, lookUp = true) {
  screen = mount(DictionarySettings, {
    props: {
      api: createPluginApi(server.fetch, BASE, 'token-1'),
      lookUp,
      'onUpdate:lookUp': (value: boolean) => screen?.setProps({ lookUp: value }),
    },
    attachTo: document.body,
  });
  await flushPromises();
  return screen;
}

const find = (wrapper: VueWrapper, id: string) => wrapper.get(`[data-testid="${id}"]`);

function button(wrapper: VueWrapper, label: string) {
  const found = wrapper.find(`[aria-label="${label}"]`);
  return found.exists() ? found : wrapper.findAll('button').find((item) => item.text() === label)!;
}

describe('the dictionary settings', () => {
  afterEach(() => {
    vi.useRealTimers();
    screen?.unmount();
    screen = undefined;
  });

  it('let a user turn looking words up off, and show no tools to change them', async () => {
    const page = await open(
      plugin([dictionary('Jitendex'), dictionary('Off', { Enabled: false })], false),
    );

    expect(page.text()).toContain('In Jitendex, on the Jellyfin server.');
    await find(page, 'look-up').setValue(false);
    expect(page.emitted('update:lookUp')?.at(-1)).toEqual([false]);
    expect(page.find('[data-testid="dictionary-address"]').exists()).toBe(false);
    expect(page.find('[aria-label="Remove Jitendex"]').exists()).toBe(false);
  });

  it('say when the server has none to look words up in', async () => {
    const page = await open(plugin([dictionary('Off', { Enabled: false })], false));

    expect(find(page, 'no-dictionaries').text()).toBe(
      'This Jellyfin server has no dictionaries turned on to look words up in. An administrator of the server can add them.',
    );
    expect(page.find('[data-testid="look-up"]').exists()).toBe(false);
  });

  it('let an administrator put them in order, turn one off and remove one', async () => {
    const server = plugin([dictionary('First'), dictionary('Second')], true);
    const page = await open(server);
    expect(find(page, 'dictionary-First').text()).toContain(
      '437,367 words · 201.5 MB · revision 2026.11.20',
    );

    await button(page, 'Move Second up').trigger('click');
    await flushPromises();
    expect(server.list().map((item) => item.Title)).toEqual(['Second', 'First']);
    expect(button(page, 'Move Second up').attributes('disabled')).toBeDefined();

    await button(page, 'Use First').trigger('click');
    await flushPromises();
    expect(server.sent.at(-1)!.body).toEqual({ Order: ['second', 'first'], Disabled: ['first'] });
    expect(button(page, 'Use First').attributes('aria-checked')).toBe('false');
    expect(page.text()).toContain('In Second, on the Jellyfin server.');

    // Removing asks first.
    await button(page, 'Remove Second').trigger('click');
    expect(server.list()).toHaveLength(2);
    await find(page, 'confirm-remove').trigger('click');
    await flushPromises();
    expect(server.list().map((item) => item.Title)).toEqual(['First']);
  });

  it('let an administrator add Jitendex, and follow the download until it is ready', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const server = plugin([], true);
    const page = await open(server);
    expect(find(page, 'no-dictionaries').text()).toContain('Add one below.');

    await button(page, 'Add Jitendex, Japanese to English').trigger('click');
    await flushPromises();
    expect(server.sent.at(-1)!.body).toEqual({
      Url: 'https://github.com/stephenmk/stephenmk.github.io/releases/latest/download/jitendex-yomitan.zip',
    });
    expect(find(page, 'import-progress').text()).toContain('Downloading Jitendex… 0%');

    await vi.advanceTimersByTimeAsync(1_000);
    expect(find(page, 'import-progress').text()).toContain('50%');
    await vi.advanceTimersByTimeAsync(1_000);
    expect(find(page, 'dictionary-message').text()).toBe('Jitendex.org [2026-11-23] is ready.');
    expect(page.find('[data-testid="import-progress"]').exists()).toBe(false);
    expect(page.text()).toContain('In Jitendex.org [2026-11-23], on the Jellyfin server.');
    // It is there now, under its own title with its revision, so it is not offered again.
    expect(button(page, 'Add Jitendex, Japanese to English').attributes('disabled')).toBeDefined();
    expect(
      button(page, 'Add JMnedict, names of people and places').attributes('disabled'),
    ).toBeUndefined();
  });

  it('let an administrator set an audio source for words, which the page does not keep', async () => {
    const server = plugin([dictionary('Jitendex')], true);
    const page = await open(server);
    expect(find(page, 'word-audio-state').text()).toContain('Words have no sound.');
    expect(page.find('[data-testid="word-audio-remove"]').exists()).toBe(false);

    await find(page, 'word-audio-address').setValue('https://audio.example/list?reading={reading}');
    await find(page, 'word-audio-save').trigger('click');
    await flushPromises();
    expect(find(page, 'word-audio-message').text()).toBe('The address needs {term}.');

    await find(page, 'word-audio-address').setValue(
      'https://audio.example/list?term={term}&reading={reading}&key=secret',
    );
    await find(page, 'word-audio-save').trigger('click');
    await flushPromises();
    expect(find(page, 'word-audio-message').text()).toBe(
      'Saved. It works: it has a sound for 食べる.',
    );
    expect(find(page, 'word-audio-state').text()).toContain('come from audio.example');
    expect((find(page, 'word-audio-address').element as HTMLInputElement).value).toBe('');
    expect(page.html()).not.toContain('secret');

    // A source that does not answer is kept, and the administrator is told.
    await find(page, 'word-audio-address').setValue('https://other.example/{term}.mp3');
    await find(page, 'word-audio-save').trigger('click');
    await flushPromises();
    expect(find(page, 'word-audio-message').attributes('role')).toBe('alert');

    await find(page, 'word-audio-remove').trigger('click');
    await flushPromises();
    expect(find(page, 'word-audio-message').text()).toBe('Words have no sound now.');
    expect(find(page, 'word-audio-state').text()).toContain('Words have no sound.');
  });

  it('show a user who is not an administrator no audio source to set', async () => {
    const page = await open(plugin([dictionary('Jitendex')], false));

    expect(page.find('[data-testid="word-audio-address"]').exists()).toBe(false);
  });

  it('say why an address or a file was not taken', async () => {
    const server = plugin([], true);
    const page = await open(server);

    await find(page, 'dictionary-address').setValue('ftp://example.org/x.zip');
    await find(page, 'dictionary-download').trigger('click');
    await flushPromises();
    expect(find(page, 'dictionary-message').text()).toBe('Not an address.');

    const input = find(page, 'dictionary-file');
    const file = new File(['not a zip'], 'words.zip', { type: 'application/zip' });
    Object.defineProperty(input.element, 'files', { value: [file] });
    await input.trigger('change');
    await flushPromises();
    const upload = server.sent.at(-1)!;
    expect(upload.method).toBe('POST');
    expect(upload.query.get('name')).toBe('words.zip');
    expect(upload.headers['content-type']).toBe('application/zip');
    expect(page.text()).toContain('That file is not a zip.');
  });
});
