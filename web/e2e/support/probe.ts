import { spawn } from 'node:child_process';

/**
 * Looks into a picture or a piece of sound the plugin cut, with ffmpeg and ffprobe, so that a test
 * can say what is in it and not only that something came back.
 */

function tool(name: 'ffmpeg' | 'ffprobe'): string {
  const ffmpeg = process.env.SUBTITLE_MINER_E2E_FFMPEG;
  if (ffmpeg === undefined || ffmpeg === '') return name;
  return name === 'ffmpeg' ? ffmpeg : ffmpeg.replace(/ffmpeg([^/\\]*)$/, 'ffprobe$1');
}

async function run(command: string, args: string[], input: Buffer): Promise<Buffer> {
  const child = spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'] });
  const output: Buffer[] = [];
  let errors = '';
  child.stdout.on('data', (chunk: Buffer) => output.push(chunk));
  child.stderr.on('data', (chunk: Buffer) => {
    errors += chunk.toString('utf8');
  });
  child.stdin.on('error', () => {
    // The tool may stop reading before everything is written.
  });
  child.stdin.end(input);
  const code = await new Promise<number | null>((resolve, reject) => {
    child.on('error', reject);
    child.on('close', resolve);
  });
  if (code !== 0) throw new Error(`${command} failed: ${errors}`);
  return Buffer.concat(output);
}

/** What ffprobe says a file is. */
export interface Probe {
  /** The container, for example "image2", "webp_pipe", "mp3" or "ogg". */
  format: string;
  codec: string;
  width: number;
  height: number;
  /** The number of pictures. */
  frames: number;
  /** The length of sound in seconds. */
  seconds: number;
  channels: number;
}

export async function probe(file: Buffer): Promise<Probe> {
  const text = await run(
    tool('ffprobe'),
    [
      ...['-v', 'error', '-count_frames', '-show_format', '-show_streams', '-of', 'json'],
      ...['-i', 'pipe:0'],
    ],
    file,
  );
  const parsed = JSON.parse(text.toString('utf8')) as {
    format?: { format_name?: string; duration?: string };
    streams?: {
      codec_name?: string;
      width?: number;
      height?: number;
      nb_read_frames?: string;
      duration?: string;
      channels?: number;
    }[];
  };
  const stream = parsed.streams?.[0];
  return {
    format: parsed.format?.format_name ?? '',
    codec: stream?.codec_name ?? '',
    width: stream?.width ?? 0,
    height: stream?.height ?? 0,
    frames: Number(stream?.nb_read_frames ?? 0),
    seconds: Number(stream?.duration ?? parsed.format?.duration ?? 0),
    channels: stream?.channels ?? 0,
  };
}

/** The average colour of a picture's first frame, as red, green and blue from 0 to 255. */
export async function colourOf(picture: Buffer): Promise<[number, number, number]> {
  const pixel = await run(
    tool('ffmpeg'),
    [
      ...['-nostdin', '-v', 'error', '-i', 'pipe:0', '-frames:v', '1'],
      ...['-vf', 'scale=1:1:flags=area', '-f', 'rawvideo', '-pix_fmt', 'rgb24', 'pipe:1'],
    ],
    picture,
  );
  if (pixel.length < 3) throw new Error('The picture could not be read.');
  return [pixel[0]!, pixel[1]!, pixel[2]!];
}

/** Whether a colour is nearer to the wanted one than a lossy picture format can blur it. */
export function isColour(
  actual: [number, number, number],
  wanted: [number, number, number],
): boolean {
  return actual.every((value, index) => Math.abs(value - wanted[index]!) < 60);
}

/**
 * The pitch of a piece of sound in Hz and its length in seconds. The test sound is a plain hum,
 * so counting how often the wave crosses zero gives the pitch.
 */
export async function soundOf(sound: Buffer): Promise<{ hz: number; seconds: number }> {
  const rate = 16_000;
  const raw = await run(
    tool('ffmpeg'),
    [
      ...['-nostdin', '-v', 'error', '-i', 'pipe:0'],
      ...['-ac', '1', '-ar', String(rate), '-f', 's16le', 'pipe:1'],
    ],
    sound,
  );
  const count = Math.floor(raw.length / 2);
  const seconds = count / rate;

  // The first and the last tenth of a second are left out: the cut fades in, and an encoder pads.
  const from = Math.floor(rate / 10);
  const to = count - from;
  let crossings = 0;
  for (let i = from + 1; i < to; i += 1) {
    if (raw.readInt16LE((i - 1) * 2) < 0 !== raw.readInt16LE(i * 2) < 0) crossings += 1;
  }

  return { hz: crossings / 2 / ((to - from) / rate), seconds };
}
