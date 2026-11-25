import {
  type DictionaryList,
  forgetDictionaries,
  importEnded,
  listDictionaries,
  metaDictionary,
  serveFiles,
  uploadDictionary,
  wordsDictionary,
} from '../support/dictionaries';
import { expect, type Login, signIn, test, tokenHeader } from '../support/fixtures';
import { API } from '../support/watching';
import { yomitanZip } from '../support/yomitanZip';

interface Lookup {
  results: {
    matched: string;
    deinflected: string;
    trace: { name: string }[];
    term: {
      expression: string;
      reading: string;
      glossaries: { dictionary: string; glossary: string; tags: { notes: string }[] }[];
      frequencies: { dictionary: string; frequencies: { value: number }[] }[];
      pitches: { dictionary: string; pitches: { position: number }[] }[];
    };
  }[];
}

let viewer: Login;
let admin: Login;

test.beforeAll(async ({ request, jellyfin }) => {
  viewer = await signIn(request, jellyfin.viewer);
  admin = await signIn(request, jellyfin.admin);
});

test.afterEach(async ({ request }) => {
  await forgetDictionaries(request, admin);
});

async function lookUp(
  request: Parameters<typeof listDictionaries>[0],
  login: Login,
  text: string,
): Promise<Lookup> {
  const response = await request.get(
    `${API}/Dictionaries/Lookup?text=${encodeURIComponent(text)}`,
    { headers: tokenHeader(login.token) },
  );
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json()) as Lookup;
}

test.describe('the dictionaries on the server', () => {
  test('are imported from an upload and looked up by every user, conjugations undone', async ({
    request,
  }) => {
    await uploadDictionary(request, admin, wordsDictionary());
    await uploadDictionary(request, admin, metaDictionary());

    const list = await listDictionaries(request, viewer);
    expect(list.MayManage).toBe(false);
    expect(list.Dictionaries.map((item) => [item.Title, item.Enabled, item.Terms])).toEqual([
      ['E2E Words', true, 4],
      ['E2E Frequencies', true, 0],
    ]);

    const found = await lookUp(request, viewer, '食べられなかったの');
    const first = found.results[0]!;
    expect(first.matched).toBe('食べられなかった');
    expect(first.term).toMatchObject({ expression: '食べる', reading: 'たべる' });
    expect(first.trace.map((step) => step.name)).toEqual([
      '-た',
      'negative',
      'potential or passive',
    ]);
    expect(first.term.glossaries[0]).toMatchObject({
      dictionary: 'E2E Words',
      tags: [{ notes: 'Ichidan verb' }],
    });
    expect(first.term.frequencies).toEqual([
      { dictionary: 'E2E Frequencies', frequencies: [{ value: 512 }] },
    ]);
    expect(first.term.pitches).toEqual([
      { dictionary: 'E2E Frequencies', pitches: [{ position: 2 }] },
    ]);
    expect((await lookUp(request, viewer, 'ﾀﾍﾞﾙ')).results[0]?.term.expression).toBe('食べる');
  });

  test('serve the pictures of their definitions so that they run nothing', async ({ request }) => {
    const id = (await uploadDictionary(request, admin, wordsDictionary())).Dictionaries[0]!.Id;

    const picture = await request.get(`${API}/Dictionaries/${id}/Media?path=img/bowl.png`, {
      headers: tokenHeader(viewer.token),
    });
    expect(picture.status()).toBe(200);
    expect(picture.headers()['content-type']).toBe('image/png');
    expect(picture.headers()['content-security-policy']).toContain('sandbox');
    expect(picture.headers()['x-content-type-options']).toBe('nosniff');

    const none = await request.get(`${API}/Dictionaries/${id}/Media?path=index.json`, {
      headers: tokenHeader(viewer.token),
    });
    expect(none.status()).toBe(404);
    const nobody = await request.get(`${API}/Dictionaries/Lookup?text=食べる`);
    expect(nobody.status()).toBe(401);
  });

  test('are downloaded by the server from the address an administrator gives', async ({
    request,
  }) => {
    const publisher = await serveFiles({ '/words.zip': yomitanZip(wordsDictionary('Downloaded')) });
    try {
      const started = await request.post(`${API}/Dictionaries/Download`, {
        headers: tokenHeader(admin.token),
        data: { Url: `${publisher.url}/words.zip` },
      });
      expect(started.status(), await started.text()).toBe(202);
      const list = await importEnded(request, admin);
      expect(list.Import).toMatchObject({ State: 'done', Title: 'Downloaded' });
      expect(list.Dictionaries.map((item) => item.Title)).toEqual(['Downloaded']);

      await request.post(`${API}/Dictionaries/Download`, {
        headers: tokenHeader(admin.token),
        data: { Url: `${publisher.url}/gone.zip` },
      });
      expect((await importEnded(request, admin)).Import).toMatchObject({
        State: 'failed',
        Error: 'The address answered 404 Not Found.',
      });
      expect(publisher.asked).toEqual(['/words.zip', '/gone.zip']);
    } finally {
      await publisher.stop();
    }
  });

  test('wait their turn when they are sent while another is imported', async ({ request }) => {
    let release = (): void => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const publisher = await serveFiles(
      { '/first.zip': yomitanZip(wordsDictionary('First')) },
      held,
    );
    const send = async (name: string, zip: Buffer): Promise<DictionaryList> => {
      const response = await request.post(`${API}/Dictionaries?name=${name}`, {
        headers: { ...tokenHeader(admin.token), 'Content-Type': 'application/zip' },
        data: zip,
      });
      expect(response.status(), await response.text()).toBe(202);
      return (await response.json()) as DictionaryList;
    };
    try {
      const before = (await listDictionaries(request, admin)).Import.Finished.at(-1)?.Number ?? 0;
      const started = await request.post(`${API}/Dictionaries/Download`, {
        headers: tokenHeader(admin.token),
        data: { Url: `${publisher.url}/first.zip` },
      });
      expect(started.status(), await started.text()).toBe(202);

      const second = await send('second.zip', yomitanZip(wordsDictionary('Second')));
      expect(second.Import).toMatchObject({ State: 'downloading', Waiting: 1 });
      await send('other.zip', Buffer.from('not a zip'));
      const fourth = await send('fourth.zip', yomitanZip(metaDictionary('Fourth')));
      expect(fourth.Import).toMatchObject({
        State: 'downloading',
        Source: `${publisher.url}/first.zip`,
        Waiting: 3,
      });
      expect(fourth.Dictionaries).toEqual([]);

      release();
      const list = await importEnded(request, admin);
      expect(list.Dictionaries.map((item) => item.Title)).toEqual(['First', 'Second', 'Fourth']);
      expect(list.Import).toMatchObject({ State: 'done', Title: 'Fourth', Waiting: 0 });
      expect(
        list.Import.Finished.filter((outcome) => outcome.Number > before).map((outcome) => [
          outcome.Number - before,
          outcome.Source,
          outcome.Title ?? outcome.Error,
        ]),
      ).toEqual([
        [1, `${publisher.url}/first.zip`, 'First'],
        [2, 'second.zip', 'Second'],
        [
          3,
          'other.zip',
          'That file is not a zip. A Yomitan dictionary is a zip with an index.json in it.',
        ],
        [4, 'fourth.zip', 'Fourth'],
      ]);
      expect((await lookUp(request, viewer, '食べる')).results[0]?.term.glossaries).toHaveLength(2);
    } finally {
      release();
      await publisher.stop();
    }
  });

  test('are let go of when the import they wait behind is stopped', async ({ request }) => {
    const publisher = await serveFiles(
      { '/first.zip': yomitanZip(wordsDictionary('First')) },
      new Promise(() => undefined),
    );
    try {
      const before = (await listDictionaries(request, admin)).Import.Finished.at(-1)?.Number ?? 0;
      await request.post(`${API}/Dictionaries/Download`, {
        headers: tokenHeader(admin.token),
        data: { Url: `${publisher.url}/first.zip` },
      });
      const waits = await request.post(`${API}/Dictionaries?name=waits.zip`, {
        headers: { ...tokenHeader(admin.token), 'Content-Type': 'application/zip' },
        data: yomitanZip(wordsDictionary('Waits')),
      });
      expect(((await waits.json()) as DictionaryList).Import.Waiting).toBe(1);

      const stopped = await request.delete(`${API}/Dictionaries/Import`, {
        headers: tokenHeader(admin.token),
      });
      expect(stopped.status(), await stopped.text()).toBe(200);
      expect(((await stopped.json()) as DictionaryList).Import.Waiting).toBe(0);

      const list = await importEnded(request, admin);
      expect(list.Import).toMatchObject({ State: 'failed', Error: 'The import was stopped.' });
      expect(list.Dictionaries).toEqual([]);
      expect(
        list.Import.Finished.filter((outcome) => outcome.Number > before).map((outcome) => [
          outcome.Source,
          outcome.Error,
        ]),
      ).toEqual([
        ['waits.zip', 'The import was stopped.'],
        [`${publisher.url}/first.zip`, 'The import was stopped.'],
      ]);
    } finally {
      await publisher.stop();
    }
  });

  test('are only changed by an administrator', async ({ request }) => {
    const upload = await request.post(`${API}/Dictionaries?name=x.zip`, {
      headers: { ...tokenHeader(viewer.token), 'Content-Type': 'application/zip' },
      data: yomitanZip(wordsDictionary()),
    });
    expect(upload.status()).toBe(403);
    const download = await request.post(`${API}/Dictionaries/Download`, {
      headers: tokenHeader(viewer.token),
      data: { Url: 'http://127.0.0.1:9/words.zip' },
    });
    expect(download.status()).toBe(403);
    expect((await listDictionaries(request, admin)).Dictionaries).toEqual([]);
  });

  test('are put in order, turned off and removed', async ({ request }) => {
    await uploadDictionary(request, admin, wordsDictionary('First'));
    const ids = (
      await uploadDictionary(request, admin, wordsDictionary('Second'))
    ).Dictionaries.map((item) => item.Id);

    const arranged = await request.put(`${API}/Dictionaries`, {
      headers: tokenHeader(admin.token),
      data: { Order: [ids[1], ids[0]], Disabled: [ids[0]] },
    });
    expect(arranged.status(), await arranged.text()).toBe(200);
    const glossaries = (await lookUp(request, viewer, '食べる')).results[0]!.term.glossaries;
    expect(glossaries.map((glossary) => glossary.dictionary)).toEqual(['Second']);

    const removed = await request.delete(`${API}/Dictionaries/${ids[1]}`, {
      headers: tokenHeader(admin.token),
    });
    expect(removed.status()).toBe(200);
    expect((await listDictionaries(request, admin)).Dictionaries.map((item) => item.Title)).toEqual(
      ['First'],
    );
  });
});
