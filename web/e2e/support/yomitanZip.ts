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
  /**
   * Files of `files` that the zip names as a packer on a Japanese Windows does, by their path:
   * these bytes, the path in Shift-JIS, stand where the name goes, and the path in UTF-8 is in
   * the "Unicode Path" field beside them. Real dictionaries such as Daijirin come that way.
   */
  shiftJisNames?: Record<string, Buffer>;
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
  return zip(files, dictionary.shiftJisNames ?? {});
}

function json(value: unknown): Buffer {
  return Buffer.from(JSON.stringify(value));
}

/** Info-ZIP's "Unicode Path" field: a version, the checksum of the name it stands beside, the name in UTF-8. */
function unicodePath(rawName: Buffer, name: string): Buffer {
  const utf8 = Buffer.from(name);
  const head = Buffer.alloc(9);
  head.writeUInt16LE(0x7075, 0);
  head.writeUInt16LE(5 + utf8.length, 2);
  head.writeUInt8(1, 4);
  head.writeUInt32LE(crc32(rawName), 5);
  return Buffer.concat([head, utf8]);
}

function zip(files: [string, Buffer][], shiftJisNames: Record<string, Buffer>): Buffer {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const [name, data] of files) {
    const shiftJis = shiftJisNames[name];
    const path = shiftJis ?? Buffer.from(name);
    const extra = shiftJis ? unicodePath(shiftJis, name) : Buffer.alloc(0);
    const flags = shiftJis ? 0 : 0x0800; // names in UTF-8, unless the packer wrote its own encoding
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4); // version needed
    local.writeUInt16LE(flags, 6);
    local.writeUInt16LE(0, 8); // stored
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(path.length, 26);
    local.writeUInt16LE(extra.length, 28);
    locals.push(local, path, extra, data);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(flags, 8);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(path.length, 28);
    central.writeUInt16LE(extra.length, 30);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, path, extra);
    offset += local.length + path.length + extra.length + data.length;
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
