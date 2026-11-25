import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createPluginApi } from '@/api/plugin';
import type {
  DictionariesView,
  DictionaryImport,
  DictionaryImportOutcome,
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

/** An import the server was asked for, and how many looks it takes. */
interface Asked {
  Source: string;
  Title?: string;
  Error?: string;
  looks: number;
}

/**
 * The plugin's dictionaries as the server keeps them. One import runs at a time and the others
 * wait their turn: a download takes two looks, an upload one, and a file that is not a zip none.
 */
function plugin(start: DictionaryView[], mayManage: boolean) {
  let list = [...start];
  let job: Omit<DictionaryImport, 'Waiting' | 'Finished'> = { State: 'idle', Progress: 0 };
  let current: Asked | null = null;
  const waiting: Asked[] = [];
  const finished: DictionaryImportOutcome[] = [];
  let wordAudio: WordAudioView = { Set: false };
  /** An upload the server does not answer until the test lets it, by the file's name. */
  const held = new Map<string, () => void>();
  const view = (): DictionariesView => ({
    Dictionaries: list,
    Import: { ...job, Waiting: waiting.length, Finished: [...finished] },
    MayManage: mayManage,
    WordAudio: mayManage ? wordAudio : { Set: wordAudio.Set },
  });
  const begin = (asked: Asked): void => {
    current = asked;
    job = {
      State: asked.Source.startsWith('https://') ? 'downloading' : 'importing',
      Progress: 0,
      Source: asked.Source,
    };
    if (asked.looks === 0) look();
  };
  const end = (asked: Asked, error: string | undefined): void => {
    finished.push({
      Number: finished.length + 1,
      Source: asked.Source,
      ...(error === undefined ? { Title: asked.Title! } : { Error: error }),
    });
    if (error === undefined) {
      list = [
        ...list,
        dictionary(asked.Title!, { Id: asked.Title!.split(/[. ]/)[0]!.toLowerCase() }),
      ];
    }
    job =
      error === undefined
        ? { State: 'done', Progress: 1, Title: asked.Title!, Source: asked.Source }
        : { State: 'failed', Progress: 0, Error: error, Source: asked.Source };
  };
  /** One look at the import: it gets half way, or ends and lets the next one start. */
  function look(): void {
    if (current === null) return;
    if ((current.looks -= 1) > 0) {
      job = { ...job, Progress: 0.5 };
      return;
    }
    end(current, current.Error);
    current = null;
    const next = waiting.shift();
    if (next !== undefined) begin(next);
  }
  const ask = (asked: Asked): void => {
    if (current === null) begin(asked);
    else waiting.push(asked);
  };
  const server = fakeRoutes((request: RoutedRequest) => {
    if (request.path === 'Dictionaries' && request.method === 'GET') {
      look();
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
      ask({ Source: Url, Title: 'Jitendex.org [2026-11-23]', looks: 2 });
      return json(view(), 202);
    }
    if (request.path === 'Dictionaries' && request.method === 'POST') {
      const name = request.query.get('name')!;
      const take = (): Response => {
        if (name.startsWith('broken')) {
          ask({ Source: name, Error: 'That file is not a zip.', looks: 0 });
        } else {
          ask({ Source: name, Title: name.replace(/\.zip$/, ''), looks: 1 });
        }
        return json(view(), 202);
      };
      if (!name.startsWith('slow')) return take();
      return new Promise<Response>((resolve, reject) => {
        held.set(name, () => resolve(take()));
        request.signal?.addEventListener('abort', () =>
          reject(new DOMException('The request was aborted.', 'AbortError')),
        );
      });
    }
    if (request.path === 'Dictionaries/Import' && request.method === 'DELETE') {
      if (current !== null) {
        for (const asked of waiting.splice(0)) end(asked, 'The import was stopped.');
        end(current, 'The import was stopped.');
        current = null;
      }
      return json(view());
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
  return { ...server, list: () => list, held };
}

let screen: VueWrapper | undefined;

async function open(server: ReturnType<typeof plugin>, lookUp = true) {
  screen = mount(DictionarySettings, {
    props: {
      api: createPluginApi(server.fetch, BASE, 'token-1'),
      lookUp,
      'onUpdate:lookUp': (value: boolean) => screen?.setProps({ lookUp: value }),
      textSize: 100,
      'onUpdate:textSize': (value: number) => screen?.setProps({ textSize: value }),
      hideTags: false,
      'onUpdate:hideTags': (value: boolean) => screen?.setProps({ hideTags: value }),
      compact: false,
      'onUpdate:compact': (value: boolean) => screen?.setProps({ compact: value }),
    },
    attachTo: document.body,
  });
  await flushPromises();
  return screen;
}

const find = (wrapper: VueWrapper, id: string) => wrapper.get(`[data-testid="${id}"]`);

/** Picks files in the upload field, as the device's file dialog does. */
async function pick(wrapper: VueWrapper, ...names: string[]): Promise<void> {
  const input = find(wrapper, 'dictionary-file');
  const files = names.map((name) => new File(['zip'], name, { type: 'application/zip' }));
  Object.defineProperty(input.element, 'files', { value: files, configurable: true });
  await input.trigger('change');
}

/** The names of the files sent to the server so far, in the order they were sent. */
function uploads(server: ReturnType<typeof plugin>): string[] {
  return server.sent
    .filter((request) => request.path === 'Dictionaries' && request.method === 'POST')
    .map((request) => request.query.get('name')!);
}

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
    // How the popup looks: smaller text, no tags, compact definitions.
    await find(page, 'text-size').setValue('80');
    expect(page.emitted('update:textSize')?.at(-1)).toEqual([80]);
    await find(page, 'hide-tags').setValue(true);
    expect(page.emitted('update:hideTags')?.at(-1)).toEqual([true]);
    await find(page, 'compact').setValue(true);
    expect(page.emitted('update:compact')?.at(-1)).toEqual([true]);

    await find(page, 'look-up').setValue(false);
    expect(page.emitted('update:lookUp')?.at(-1)).toEqual([false]);
    expect(page.find('[data-testid="popup-look"]').exists()).toBe(false);
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

    await vi.advanceTimersByTimeAsync(400);
    expect(find(page, 'import-progress').text()).toContain('50%');
    await vi.advanceTimersByTimeAsync(400);
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

    await pick(page, 'broken.zip');
    await flushPromises();
    const upload = server.sent.at(-1)!;
    expect(upload.method).toBe('POST');
    expect(upload.query.get('name')).toBe('broken.zip');
    expect(upload.headers['content-type']).toBe('application/zip');
    expect(find(page, 'dictionary-message').text()).toBe('That file is not a zip.');
    expect(find(page, 'dictionary-message').attributes('role')).toBe('alert');
  });

  it('send several picked files one after another, and say how each of them ended', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const server = plugin([], true);
    const page = await open(server);
    expect(find(page, 'dictionary-file').attributes('multiple')).toBeDefined();

    await pick(page, 'Jitendex.zip', 'broken.zip', 'slow Names.zip');
    await flushPromises();
    // The first is imported while the last is still on its way.
    expect(uploads(server)).toEqual(['Jitendex.zip', 'broken.zip', 'slow Names.zip']);
    expect(find(page, 'import-progress').text()).toContain(
      'Importing Jitendex.zip… 0%, 1 more waiting',
    );
    expect(find(page, 'dictionary-sending').text()).toBe(
      'Sending slow Names.zip to the server (3 of 3)…',
    );
    expect(page.find('[data-testid="dictionary-file"]').exists()).toBe(false);

    // The server is done with what it has before the last file is there: nothing is said yet.
    await vi.advanceTimersByTimeAsync(400);
    expect(server.list().map((item) => item.Title)).toEqual(['Jitendex']);
    expect(find(page, 'import-progress').text()).toContain('Sending slow Names.zip to the server');
    expect(page.find('[data-testid="dictionary-message"]').exists()).toBe(false);

    server.held.get('slow Names.zip')!();
    await flushPromises();
    expect(find(page, 'import-progress').text()).toContain('Importing slow Names.zip… 0%');
    await vi.advanceTimersByTimeAsync(400);

    expect(page.find('[data-testid="import-progress"]').exists()).toBe(false);
    expect(find(page, 'dictionary-message').text()).toBe(
      '2 dictionaries are ready: Jitendex, slow Names.\nbroken.zip: That file is not a zip.',
    );
    expect(find(page, 'dictionary-message').attributes('role')).toBe('alert');
    expect((find(page, 'dictionary-file').element as HTMLInputElement).value).toBe('');

    // What ended before is not told again.
    await pick(page, 'Other.zip');
    await flushPromises();
    await vi.advanceTimersByTimeAsync(400);
    expect(find(page, 'dictionary-message').text()).toBe('Other is ready.');
  });

  it('send no more files ahead than the server should keep waiting', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const server = plugin([], true);
    const page = await open(server);
    const names = ['A.zip', 'B.zip', 'C.zip', 'D.zip', 'E.zip', 'F.zip', 'G.zip'];

    await pick(page, ...names);
    await flushPromises();
    // One is imported and four wait: the sixth is held back.
    expect(uploads(server)).toEqual(names.slice(0, 5));
    expect(find(page, 'import-progress').text()).toContain('Importing A.zip… 0%, 4 more waiting');
    expect(find(page, 'dictionary-sending').text()).toBe('Sending F.zip to the server (6 of 7)…');

    await vi.advanceTimersByTimeAsync(600);
    expect(uploads(server)).toEqual(names.slice(0, 6));
    await vi.advanceTimersByTimeAsync(4_000);

    expect(uploads(server)).toEqual(names);
    expect(server.list().map((item) => item.Title)).toEqual(['A', 'B', 'C', 'D', 'E', 'F', 'G']);
    expect(find(page, 'dictionary-message').text()).toBe(
      '7 dictionaries are ready: A, B, C, D, E, F, G.',
    );
    expect(find(page, 'dictionary-message').attributes('role')).toBe('status');
  });

  it('stop the import, those that wait and the files still on their way', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const server = plugin([], true);
    const page = await open(server);

    await pick(page, 'A.zip', 'B.zip', 'slow C.zip', 'D.zip');
    await flushPromises();
    expect(uploads(server)).toEqual(['A.zip', 'B.zip', 'slow C.zip']);

    await button(page, 'Stop').trigger('click');
    await flushPromises();

    expect(server.sent.at(-1)!.path).toBe('Dictionaries/Import');
    expect(
      server.sent.find((request) => request.query.get('name') === 'slow C.zip')!.signal!.aborted,
    ).toBe(true);
    expect(uploads(server)).toEqual(['A.zip', 'B.zip', 'slow C.zip']);
    expect(server.list()).toEqual([]);
    expect(page.find('[data-testid="import-progress"]').exists()).toBe(false);
    expect(find(page, 'dictionary-message').text()).toBe(
      'B.zip, A.zip, slow C.zip, D.zip: The import was stopped.',
    );

    // A file sent alone and stopped says so in a line.
    await pick(page, 'slow E.zip');
    await flushPromises();
    expect(find(page, 'import-progress').text()).toBe('Sending slow E.zip to the server…Stop');
    await button(page, 'Stop').trigger('click');
    await flushPromises();
    expect(find(page, 'dictionary-message').text()).toBe('The import was stopped.');
  });

  it('say nothing of an import that another page started and this one never saw run', async () => {
    const server = plugin([], true);
    const page = await open(server);
    const other = createPluginApi(server.fetch, BASE, 'token-2');
    await other.uploadDictionary(new Blob(['zip']), 'Elsewhere.zip');
    await other.dictionaries();
    expect(server.list().map((item) => item.Title)).toEqual(['Elsewhere']);

    await pick(page, 'broken.zip');
    await flushPromises();
    expect(find(page, 'dictionary-message').text()).toBe('That file is not a zip.');

    await other.uploadDictionary(new Blob(['zip']), 'Elsewhere too.zip');
    await other.dictionaries();
    await find(page, 'dictionary-address').setValue('https://example.org/x.zip');
    await find(page, 'dictionary-download').trigger('click');
    await flushPromises();
    await button(page, 'Stop').trigger('click');
    await flushPromises();
    expect(find(page, 'dictionary-message').text()).toBe('The import was stopped.');
  });
});
