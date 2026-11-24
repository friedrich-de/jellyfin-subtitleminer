import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { copyFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

/**
 * The videos of the test library. ffmpeg makes them, so that a test can tell from a picture or a
 * piece of sound which moment and which track it was cut from:
 *
 * - the picture is one plain colour for five seconds, then the next (COLOURS);
 * - the first audio track hums at 440 Hz for 15 seconds and at 660 Hz after that;
 * - the second audio track hums at 880 Hz throughout.
 */

/** A line of the test subtitles. Times in milliseconds. */
export interface TestLine {
  startMs: number;
  endMs: number;
  japanese: string;
  english: string;
  kind: 'dialogue' | 'sign' | 'song';
  name?: string;
}

/** The lines both languages have. The song and the sign exist in the Japanese track inside the video only. */
export const LINES: readonly TestLine[] = [
  {
    startMs: 1_000,
    endMs: 3_500,
    japanese: '昨日の話、覚えてる？',
    english: 'Do you remember what we talked about yesterday?',
    kind: 'dialogue',
    name: 'ミカ',
  },
  {
    startMs: 4_000,
    endMs: 6_000,
    japanese: 'もちろん。\n約束したからね。',
    english: 'Of course.\nI promised, after all.',
    kind: 'dialogue',
    name: 'ユウ',
  },
  {
    startMs: 6_500,
    endMs: 9_000,
    japanese: '駅前商店街',
    english: '',
    kind: 'sign',
  },
  {
    startMs: 10_000,
    endMs: 13_000,
    japanese: 'じゃあ、明日の朝に行こう。',
    english: "Then let's go tomorrow morning.",
    kind: 'dialogue',
    name: 'ミカ',
  },
  {
    startMs: 14_000,
    endMs: 17_000,
    japanese: '夜空に星が降る',
    english: '',
    kind: 'song',
  },
  {
    startMs: 18_000,
    endMs: 21_000,
    japanese: '早起きは苦手なんだけど…',
    english: "I'm not good at getting up early, though…",
    kind: 'dialogue',
    name: 'ユウ',
  },
  {
    startMs: 22_000,
    endMs: 25_000,
    japanese: '大丈夫、起こしてあげる。',
    english: "Don't worry, I'll wake you.",
    kind: 'dialogue',
    name: 'ミカ',
  },
  {
    startMs: 26_000,
    endMs: 29_000,
    japanese: 'ありがとう。',
    english: 'Thank you.',
    kind: 'dialogue',
    name: 'ユウ',
  },
];

/** The lines people speak: what the English track and the subtitle file next to the video hold. */
export const SPOKEN: readonly TestLine[] = LINES.filter((line) => line.kind === 'dialogue');

/** The picture's colour, five seconds each, as ffmpeg names it and as red, green and blue. */
export const COLOURS: readonly { name: string; rgb: [number, number, number] }[] = [
  { name: 'red', rgb: [255, 0, 0] },
  { name: 'lime', rgb: [0, 255, 0] },
  { name: 'blue', rgb: [0, 0, 255] },
  { name: 'yellow', rgb: [255, 255, 0] },
  { name: 'magenta', rgb: [255, 0, 255] },
  { name: 'cyan', rgb: [0, 255, 255] },
];
export const COLOUR_MS = 5_000;
export const VIDEO_MS = COLOURS.length * COLOUR_MS;
export const VIDEO_WIDTH = 320;
export const VIDEO_HEIGHT = 180;

/** What the audio tracks hum at, in Hz. */
export const FIRST_TRACK_EARLY_HZ = 440;
export const FIRST_TRACK_LATE_HZ = 660;
export const FIRST_TRACK_CHANGE_MS = 15_000;
export const SECOND_TRACK_HZ = 880;

export const SHOW = 'Sample Show';
export const FILM = 'Hidden Film';

/**
 * The spoken Japanese lines as an SRT file in Shift-JIS with Windows line ends, the way many
 * Japanese subtitle files come. Node cannot write Shift-JIS, so the bytes are kept here; a test
 * compares what the plugin reads from them with SPOKEN.
 */
const SHIFT_JIS_SRT = Buffer.from(
  [
    'MQ0KMDA6MDA6MDEsMDAwIC0tPiAwMDowMDowMyw1MDANCo3wk/qCzJhigUGKb4KmgsSC6YFIDQoNCjINCjAwOjAw',
    'OjA0LDAwMCAtLT4gMDA6MDA6MDYsMDAwDQqC4IK/guuC8YFCDQqW8ZGpgrWCvYKpgueCy4FCDQoNCjMNCjAwOjAw',
    'OjEwLDAwMCAtLT4gMDA6MDA6MTMsMDAwDQqCtoLhgqCBQZa+k/qCzJKpgsmNc4KxgqSBQg0KDQo0DQowMDowMDox',
    'OCwwMDAgLS0+IDAwOjAwOjIxLDAwMA0KkYGLToKrgs2L6o7ogsiC8YK+gq+Cx4FjDQoNCjUNCjAwOjAwOjIyLDAw',
    'MCAtLT4gMDA6MDA6MjUsMDAwDQqR5Y/klXaBQYtOgrGCtYLEgqCCsILpgUINCg0KNg0KMDA6MDA6MjYsMDAwIC0t',
    'PiAwMDowMDoyOSwwMDANCoKgguiCqoLGgqSBQg0K',
  ].join(''),
  'base64',
);

function clock(ms: number, separator: string, digits: number): string {
  const part = (value: number, width: number): string => String(value).padStart(width, '0');
  const fraction = digits === 3 ? part(ms % 1000, 3) : part(Math.floor((ms % 1000) / 10), 2);
  const seconds = Math.floor(ms / 1000);
  return `${part(Math.floor(seconds / 3600), digits === 3 ? 2 : 1)}:${part(Math.floor(seconds / 60) % 60, 2)}:${part(seconds % 60, 2)}${separator}${fraction}`;
}

/** The Japanese track inside the video: ASS with a style for signs and one for the opening song. */
function japaneseAss(): string {
  const events = LINES.map((line) => {
    const text = line.japanese.replaceAll('\n', '\\N');
    const times = `${clock(line.startMs, '.', 2)},${clock(line.endMs, '.', 2)}`;
    if (line.kind === 'sign') {
      return `Dialogue: 0,${times},Sign,,0,0,0,,{\\an8\\pos(160,20)}${text}`;
    }

    if (line.kind === 'song') {
      const sung = [...text].map((letter) => `{\\k30}${letter}`).join('');
      return `Dialogue: 0,${times},OP,,0,0,0,,${sung}`;
    }

    return `Dialogue: 0,${times},Default,${line.name ?? ''},0,0,0,,${text}`;
  });
  const style = (name: string): string =>
    `Style: ${name},Noto Sans CJK JP,14,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1`;
  return [
    '[Script Info]',
    'ScriptType: v4.00+',
    `PlayResX: ${VIDEO_WIDTH}`,
    `PlayResY: ${VIDEO_HEIGHT}`,
    '',
    '[V4+ Styles]',
    'Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding',
    style('Default'),
    style('Sign'),
    style('OP'),
    '',
    '[Events]',
    'Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text',
    ...events,
    '',
  ].join('\n');
}

function englishSrt(): string {
  return SPOKEN.map(
    (line, index) =>
      `${index + 1}\n${clock(line.startMs, ',', 3)} --> ${clock(line.endMs, ',', 3)}\n${line.english}\n`,
  ).join('\n');
}

async function ffmpeg(binary: string, args: string[]): Promise<void> {
  const child = spawn(binary, ['-nostdin', '-hide_banner', '-loglevel', 'error', '-y', ...args], {
    stdio: ['ignore', 'ignore', 'pipe'],
  });
  let errors = '';
  child.stderr.on('data', (chunk: Buffer) => {
    errors += chunk.toString('utf8');
  });
  const [code] = (await once(child, 'exit')) as [number | null];
  if (code !== 0) throw new Error(`ffmpeg could not make a test video: ${errors}`);
}

/** Where the test videos are, for Jellyfin's libraries. */
export interface TestMedia {
  showsDir: string;
  filmsDir: string;
}

/**
 * Makes the test libraries under a folder: a show with two episodes and a film.
 * Episode 1 has two subtitle tracks inside and a Japanese subtitle file next to it,
 * episode 2 and the film have the tracks inside only.
 */
export async function makeMedia(root: string, ffmpegBinary = 'ffmpeg'): Promise<TestMedia> {
  const showsDir = path.join(root, 'shows');
  const filmsDir = path.join(root, 'films');
  const season = path.join(showsDir, SHOW, 'Season 01');
  const film = path.join(filmsDir, FILM);
  await mkdir(season, { recursive: true });
  await mkdir(film, { recursive: true });

  const ass = path.join(root, 'japanese.ass');
  const srt = path.join(root, 'english.srt');
  await writeFile(ass, japaneseAss(), 'utf8');
  await writeFile(srt, englishSrt(), 'utf8');

  const seconds = COLOUR_MS / 1000;
  const picture =
    COLOURS.map(
      (colour, index) =>
        `color=c=${colour.name}:s=${VIDEO_WIDTH}x${VIDEO_HEIGHT}:r=10:d=${seconds}[c${index}]`,
    ).join(';') +
    `;${COLOURS.map((_, index) => `[c${index}]`).join('')}concat=n=${COLOURS.length}:v=1:a=0[out0]`;
  const change = FIRST_TRACK_CHANGE_MS / 1000;
  const total = VIDEO_MS / 1000;
  const firstTrack =
    `sine=f=${FIRST_TRACK_EARLY_HZ}:r=48000:d=${change}[early];` +
    `sine=f=${FIRST_TRACK_LATE_HZ}:r=48000:d=${total - change}[late];` +
    '[early][late]concat=n=2:v=0:a=1[out0]';

  const episode1 = path.join(season, `${SHOW} - S01E01.mkv`);
  await ffmpeg(ffmpegBinary, [
    ...['-f', 'lavfi', '-i', picture],
    ...['-f', 'lavfi', '-i', firstTrack],
    ...['-f', 'lavfi', '-i', `sine=f=${SECOND_TRACK_HZ}:r=48000:d=${total}`],
    ...['-i', ass, '-i', srt],
    ...['-map', '0:v', '-map', '1:a', '-map', '2:a', '-map', '3:s', '-map', '4:s'],
    // A key frame every second keeps the file small and a jump to any moment quick.
    // FLAC, unlike AAC, starts without a pause of its own, so no track is moved against the others
    // and every line is exactly where the test expects it.
    ...['-c:v', 'mpeg4', '-q:v', '4', '-g', '10', '-c:a', 'flac'],
    ...['-c:s:0', 'ass', '-c:s:1', 'srt'],
    ...['-metadata:s:a:0', 'language=jpn', '-metadata:s:a:1', 'language=eng'],
    ...['-metadata:s:a:1', 'title=Commentary'],
    ...['-metadata:s:s:0', 'language=jpn', '-metadata:s:s:0', 'title=Japanese'],
    ...['-metadata:s:s:1', 'language=eng', '-metadata:s:s:1', 'title=English'],
    ...['-disposition:s:0', 'default', '-disposition:s:1', '0'],
    ...['-t', String(total), episode1],
  ]);

  await writeFile(path.join(season, `${SHOW} - S01E01.ja.srt`), SHIFT_JIS_SRT);
  await copyFile(episode1, path.join(season, `${SHOW} - S01E02.mkv`));
  await copyFile(episode1, path.join(film, `${FILM}.mkv`));
  return { showsDir, filmsDir };
}
