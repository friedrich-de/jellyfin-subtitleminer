import { crc32 } from 'node:zlib';

/** What goes into a test dictionary in Yomitan's format. */
export interface TestDictionary {
  title: string;
  /** Format 3 term rows: [expression, reading, definitionTags, rules, score, glossary[], sequence, termTags]. */
  terms?: unknown[][];
  /** Term meta rows: [expression, "freq" | "pitch", data]. */
  metas?: unknown[][];
  /** Tag rows: [name, category, order, notes, score]. */
  tags?: unknown[][];
  styles?: string;
  frequencyMode?: 'rank-based' | 'occurrence-based';
  /** Other files, such as pictures, by their path in the zip. */
  files?: Record<string, Buffer>;
}

/** A format 3 term row. */
export function term(
  expression: string,
  reading: string,
  rules: string,
  glossary: unknown[],
  definitionTags = '',
  score = 0,
): unknown[] {
  return [expression, reading, definitionTags, rules, score, glossary, 0, ''];
}

/**
 * The dictionary as a zip, written by hand with every file stored as it is: enough for the
 * plugin, which reads any zip, and no package to add for it.
 */
export function yomitanZip(dictionary: TestDictionary): Buffer {
  const files: [string, Buffer][] = [
    [
      'index.json',
      json({
        title: dictionary.title,
        revision: '1',
        format: 3,
        author: 'End-to-end tests',
        ...(dictionary.frequencyMode ? { frequencyMode: dictionary.frequencyMode } : {}),
      }),
    ],
  ];
  if (dictionary.terms?.length) files.push(['term_bank_1.json', json(dictionary.terms)]);
  if (dictionary.metas?.length) files.push(['term_meta_bank_1.json', json(dictionary.metas)]);
  if (dictionary.tags?.length) files.push(['tag_bank_1.json', json(dictionary.tags)]);
  if (dictionary.styles) files.push(['styles.css', Buffer.from(dictionary.styles)]);
  for (const [name, data] of Object.entries(dictionary.files ?? {})) files.push([name, data]);
  return zip(files);
}

function json(value: unknown): Buffer {
  return Buffer.from(JSON.stringify(value));
}

function zip(files: [string, Buffer][]): Buffer {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const [name, data] of files) {
    const path = Buffer.from(name);
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4); // version needed
    local.writeUInt16LE(0x0800, 6); // names in UTF-8
    local.writeUInt16LE(0, 8); // stored
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(path.length, 26);
    locals.push(local, path, data);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(path.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, path);
    offset += local.length + path.length + data.length;
  }

  const directory = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}
