import { once } from 'node:events';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';

import type { APIRequestContext } from '@playwright/test';

import { expect, type Login, tokenHeader } from './fixtures';
import { API } from './watching';
import { term, type TestDictionary, yomitanZip } from './yomitanZip';

/** The plugin's list of dictionaries, as the API gives it. */
export interface DictionaryList {
  Dictionaries: {
    Id: string;
    Title: string;
    Enabled: boolean;
    Terms: number;
    Frequencies: number;
    Pitches: number;
    Styles: string;
  }[];
  Import: { State: string; Source?: string; Progress: number; Title?: string; Error?: string };
  MayManage: boolean;
}

/** A small dictionary of words in the test videos' lines, with a picture and a stylesheet. */
export function wordsDictionary(title = 'E2E Words'): TestDictionary {
  return {
    title,
    terms: [
      term(
        '食べる',
        'たべる',
        'v1',
        [
          {
            type: 'structured-content',
            content: [
              { tag: 'span', data: { content: 'gloss' }, content: 'to eat' },
              { tag: 'img', path: 'img/bowl.png', width: 1, height: 1, sizeUnits: 'em' },
            ],
          },
        ],
        'v1 vt',
        10,
      ),
      term('食', 'しょく', '', ['food'], 'n'),
      term('昨日', 'きのう', '', ['yesterday'], 'n'),
      term(
        '話',
        'はなし',
        '',
        [
          {
            type: 'structured-content',
            content: [
              { tag: 'span', data: { content: 'gloss' }, content: 'talk; story' },
              { tag: 'img', path: 'img/bowl.png', width: 2, height: 2, sizeUnits: 'em' },
            ],
          },
        ],
        'n',
      ),
    ],
    tags: [['v1', 'partOfSpeech', 0, 'Ichidan verb', 0]],
    styles: '[data-sc-content="gloss"] { font-weight: bold; }',
    files: {
      // A 1×1 PNG.
      'img/bowl.png': Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        'base64',
      ),
    },
  };
}

/**
 * A dictionary that marks its definitions with small pictures, as Daijirin's for a word's old
 * form: an SVG under a Japanese name drawn in the text's colour, and a PNG.
 */
export function marksDictionary(title = 'E2E Marks'): TestDictionary {
  return {
    title,
    terms: [
      term(
        '昨日',
        'きのう',
        '',
        [
          {
            type: 'structured-content',
            content: [
              {
                tag: 'img',
                path: 'gaiji/文語.svg',
                height: 1,
                sizeUnits: 'em',
                appearance: 'monochrome',
                background: false,
                collapsible: false,
                collapsed: false,
              },
              { tag: 'span', content: '(ナリ) the day before today' },
              { tag: 'br' },
              {
                tag: 'img',
                path: 'gaiji/派生.png',
                height: 1,
                sizeUnits: 'em',
                collapsible: false,
              },
              { tag: 'span', content: '—さ(名)' },
            ],
          },
        ],
        'n',
      ),
    ],
    files: {
      'gaiji/文語.svg': Buffer.from(
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="16" height="16">' +
          '<rect x="1" y="1" width="14" height="14" rx="3" fill="none" stroke="black"/></svg>',
      ),
      'gaiji/派生.png': Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        'base64',
      ),
    },
  };
}

/** Frequencies and pitch accents for those words. */
export function metaDictionary(title = 'E2E Frequencies'): TestDictionary {
  return {
    title,
    frequencyMode: 'rank-based',
    metas: [
      ['食べる', 'freq', { reading: 'たべる', frequency: 512 }],
      ['食べる', 'pitch', { reading: 'たべる', pitches: [{ position: 2 }] }],
      ['昨日', 'freq', { reading: 'きのう', frequency: 77 }],
      ['昨日', 'pitch', { reading: 'きのう', pitches: [{ position: 2 }, { position: 0 }] }],
    ],
  };
}

export async function listDictionaries(
  request: APIRequestContext,
  login: Login,
): Promise<DictionaryList> {
  const response = await request.get(`${API}/Dictionaries`, { headers: tokenHeader(login.token) });
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json()) as DictionaryList;
}

/** Waits until the import that runs has ended, and gives the list then. */
export async function importEnded(
  request: APIRequestContext,
  login: Login,
): Promise<DictionaryList> {
  let list = await listDictionaries(request, login);
  await expect
    .poll(
      async () => {
        list = await listDictionaries(request, login);
        return list.Import.State;
      },
      { timeout: 30_000 },
    )
    .toMatch(/^(done|failed|idle)$/);
  return list;
}

/** Uploads a dictionary as an administrator and waits for its import. */
export async function uploadDictionary(
  request: APIRequestContext,
  admin: Login,
  dictionary: TestDictionary,
): Promise<DictionaryList> {
  const response = await request.post(
    `${API}/Dictionaries?name=${encodeURIComponent(dictionary.title)}.zip`,
    {
      headers: { ...tokenHeader(admin.token), 'Content-Type': 'application/zip' },
      data: yomitanZip(dictionary),
    },
  );
  expect(response.status(), await response.text()).toBe(202);
  const list = await importEnded(request, admin);
  expect(list.Import, JSON.stringify(list.Import)).toMatchObject({ State: 'done' });
  return list;
}

/** Removes every dictionary. They outlive a test, and the tests run twice. */
export async function forgetDictionaries(request: APIRequestContext, admin: Login): Promise<void> {
  await importEnded(request, admin);
  for (const dictionary of (await listDictionaries(request, admin)).Dictionaries) {
    const response = await request.delete(`${API}/Dictionaries/${dictionary.Id}`, {
      headers: tokenHeader(admin.token),
    });
    expect(response.status(), await response.text()).toBe(200);
  }
}

/** A web server on 127.0.0.1 that hands out files, as a dictionary's publisher does. */
export async function serveFiles(
  files: Record<string, Buffer>,
): Promise<{ url: string; asked: string[]; stop: () => Promise<void> }> {
  const asked: string[] = [];
  const server = createServer((request, response) => {
    asked.push(request.url ?? '');
    const file = files[request.url ?? ''];
    if (file === undefined) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { 'Content-Type': 'application/zip', 'Content-Length': file.length });
    response.end(file);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    asked,
    stop: () =>
      new Promise((resolve) => {
        server.close(() => resolve());
      }),
  };
}
