import { randomUUID } from 'node:crypto';

import type { Account } from './server';

/** What a player tells Jellyfin about its playback. Jellyfin numbers the tracks; -1 means no subtitles. */
export interface PlayerReport {
  positionMs?: number;
  paused?: boolean;
  audioIndex?: number;
  subtitleIndex?: number;
}

/**
 * A player that plays nothing: it signs in as a device of its own and tells Jellyfin what a real
 * player would tell it, through the same three calls. To the server it is a player like any other.
 */
export class PretendPlayer {
  private token = '';
  private itemId = '';
  private playSession = '';
  private last: Required<PlayerReport> = {
    positionMs: 0,
    paused: false,
    audioIndex: 2,
    subtitleIndex: -1,
  };

  private readonly deviceId = `e2e-player-${randomUUID()}`;

  constructor(
    private readonly baseUrl: string,
    private readonly account: Account,
    /** The device's name, as Jellyfin shows it. */
    readonly device = 'Living room',
    /** The app's name. */
    readonly client = 'mpv',
  ) {}

  private async send(path: string, body: unknown): Promise<Response> {
    const login = this.token === '' ? '' : `, Token="${this.token}"`;
    const response = await fetch(`${this.baseUrl}/${path}`, {
      method: 'POST',
      headers: {
        Authorization: `MediaBrowser Client="${this.client}", Device="${this.device}", DeviceId="${this.deviceId}", Version="1"${login}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      throw new Error(
        `The player's call ${path} answered ${response.status}: ${await response.text()}`,
      );
    }

    return response;
  }

  private body(): Record<string, unknown> {
    return {
      ItemId: this.itemId,
      MediaSourceId: this.itemId,
      PlaySessionId: this.playSession,
      PositionTicks: this.last.positionMs * 10_000,
      IsPaused: this.last.paused,
      AudioStreamIndex: this.last.audioIndex,
      SubtitleStreamIndex: this.last.subtitleIndex,
      PlayMethod: 'DirectPlay',
      CanSeek: true,
    };
  }

  /** Opens a video. A video that was open before is left the way a real player leaves it: without a word. */
  async play(itemId: string, report: PlayerReport = {}): Promise<void> {
    if (this.token === '') {
      const response = await this.send('Users/AuthenticateByName', {
        Username: this.account.name,
        Pw: this.account.password,
      });
      this.token = ((await response.json()) as { AccessToken: string }).AccessToken;
    }

    this.itemId = itemId;
    this.playSession = randomUUID().replaceAll('-', '');
    this.last = { positionMs: 0, paused: false, audioIndex: 2, subtitleIndex: -1, ...report };
    await this.send('Sessions/Playing', this.body());
  }

  /** Reports where playback is, or that something changed: a pause, a jump, another track. */
  async report(report: PlayerReport): Promise<void> {
    this.last = { ...this.last, ...report };
    await this.send('Sessions/Playing/Progress', this.body());
  }

  /** Closes the video. */
  async stop(positionMs?: number): Promise<void> {
    if (this.itemId === '') return;
    if (positionMs !== undefined) this.last.positionMs = positionMs;
    await this.send('Sessions/Playing/Stopped', this.body());
    this.itemId = '';
  }
}
