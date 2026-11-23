import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import { type AddressInfo, createServer as createTcpServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

/** A user of the test server. */
export interface Account {
  name: string;
  password: string;
  id: string;
}

/** What the tests need to know about the running test server. */
export interface ServerState {
  /** The server's address, without a slash at the end. */
  baseUrl: string;
  serverVersion: string;
  plugin: { guid: string; name: string; version: string };
  admin: Account;
  viewer: Account;
}

export interface ServerOptions {
  /** The Jellyfin server binary. */
  jellyfin: string;
  /** The web client's folder. By default the one next to the binary. */
  web?: string | undefined;
  ffmpeg?: string | undefined;
  /** The output of dev/package.sh. */
  packageDir: string;
  /** Keep the server's folder when it stops. */
  keep?: boolean | undefined;
}

interface ManifestVersion {
  version: string;
  sourceUrl: string;
}

interface ManifestPlugin {
  guid: string;
  name: string;
  versions: ManifestVersion[];
}

const CLIENT =
  'MediaBrowser Client="subtitle-miner-e2e", Device="setup", DeviceId="e2e-setup", Version="1"';
const START_TIMEOUT = 180_000;

/** The header Jellyfin 12 reads a client and its login from. */
export function authorization(token?: string): string {
  return token === undefined ? CLIENT : `${CLIENT}, Token="${token}"`;
}

async function freePort(): Promise<number> {
  const probe = createTcpServer();
  probe.listen(0, '127.0.0.1');
  await once(probe, 'listening');
  const { port } = probe.address() as AddressInfo;
  probe.close();
  await once(probe, 'close');
  return port;
}

/**
 * A throwaway Jellyfin server with the packaged plugin installed the way a user installs it:
 * from a plugin repository, through Jellyfin's own installer, followed by a restart.
 */
export class JellyfinServer {
  private process: ChildProcess | null = null;
  private output = '';
  private token = '';

  private constructor(
    private readonly options: ServerOptions,
    private readonly root: string,
    readonly state: ServerState,
  ) {}

  static async start(options: ServerOptions): Promise<JellyfinServer> {
    const manifestText = await readFile(path.join(options.packageDir, 'manifest.json'), 'utf8');
    const manifest = JSON.parse(manifestText) as ManifestPlugin[];
    const plugin = manifest[0];
    const version = plugin?.versions[0];
    if (plugin === undefined || version === undefined || manifest.length !== 1) {
      throw new Error('dist/manifest.json does not describe exactly one plugin.');
    }

    const root = await mkdtemp(path.join(tmpdir(), 'subtitle-miner-e2e-'));
    const port = await freePort();
    const server = new JellyfinServer(options, root, {
      baseUrl: `http://127.0.0.1:${port}`,
      serverVersion: '',
      plugin: { guid: plugin.guid, name: plugin.name, version: version.version },
      admin: { name: 'admin', password: 'e2e-admin-password', id: '' },
      viewer: { name: 'viewer', password: 'e2e-viewer-password', id: '' },
    });

    try {
      await mkdir(path.join(root, 'config'), { recursive: true });
      await writeFile(
        path.join(root, 'config', 'network.xml'),
        `<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <InternalHttpPort>${port}</InternalHttpPort>
  <PublicHttpPort>${port}</PublicHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <EnableIPv6>false</EnableIPv6>
  <LocalNetworkAddresses>
    <string>127.0.0.1</string>
  </LocalNetworkAddresses>
</NetworkConfiguration>
`,
      );

      // First run: finish the wizard, then install the plugin from the repository.
      server.launch();
      await server.waitUntilUp();
      await server.completeWizard();
      server.state.admin.id = await server.signIn(server.state.admin);
      await server.installPlugin(manifest, plugin, version);

      // A plugin is loaded at start, so the server has to go down and come up again.
      await server.shutDown();
      server.launch();
      await server.waitUntilUp();
      await server.signIn(server.state.admin);

      const created = await server.send('POST', 'Users/New', {
        Name: server.state.viewer.name,
        Password: server.state.viewer.password,
      });
      server.state.viewer.id = ((await created.json()) as { Id: string }).Id;
      return server;
    } catch (error) {
      const tail = server.output.split('\n').slice(-60).join('\n');
      await server.stop();
      throw new Error(
        `The Jellyfin test server could not be prepared: ${String(error)}\n--- server output ---\n${tail}`,
        { cause: error },
      );
    }
  }

  async stop(): Promise<void> {
    await this.kill();
    if (this.options.keep === true) {
      console.log(`The test server's folder was kept: ${this.root}`);
    } else {
      await rm(this.root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    }
  }

  private launch(): void {
    const binary = path.resolve(this.options.jellyfin);
    const web = this.options.web ?? path.join(path.dirname(binary), 'jellyfin-web');
    const args = [
      '--datadir',
      path.join(this.root, 'data'),
      '--configdir',
      path.join(this.root, 'config'),
      '--cachedir',
      path.join(this.root, 'cache'),
      '--logdir',
      path.join(this.root, 'log'),
      '--webdir',
      web,
    ];
    if (this.options.ffmpeg !== undefined) args.push('--ffmpeg', this.options.ffmpeg);

    // The server gets a small environment of its own: nothing of the caller's keys or proxies.
    const env: NodeJS.ProcessEnv = {
      PATH: process.env.PATH,
      HOME: this.root,
      LANG: 'C.UTF-8',
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    };

    // Jellyfin drops a few folders into its working directory.
    const child = spawn(binary, args, { cwd: this.root, env, stdio: ['ignore', 'pipe', 'pipe'] });
    const remember = (chunk: Buffer): void => {
      this.output = (this.output + chunk.toString('utf8')).slice(-200_000);
    };
    child.stdout.on('data', remember);
    child.stderr.on('data', remember);
    this.process = child;
  }

  private async waitUntilUp(): Promise<void> {
    const deadline = Date.now() + START_TIMEOUT;
    while (Date.now() < deadline) {
      if (this.process === null || this.process.exitCode !== null) {
        throw new Error('The server exited while it was starting.');
      }

      try {
        // While the server is still starting, a placeholder answers in its place: it calls itself
        // "Degraded" and refuses the rest with 503. Only the real server reports "Healthy".
        const health = await fetch(`${this.state.baseUrl}/health`);
        if (health.ok && (await health.text()).trim() === 'Healthy') {
          const info = await fetch(`${this.state.baseUrl}/System/Info/Public`);
          if (info.ok) {
            const version = ((await info.json()) as { Version?: string }).Version ?? '';
            if (version !== '') {
              this.state.serverVersion = version;
              return;
            }
          }
        }
      } catch {
        // Not listening yet.
      }

      await sleep(500);
    }

    throw new Error(`The server did not answer within ${START_TIMEOUT / 1000} s.`);
  }

  private async send(method: string, pathAndQuery: string, body?: unknown): Promise<Response> {
    const response = await fetch(`${this.state.baseUrl}/${pathAndQuery}`, {
      method,
      headers: {
        Authorization: authorization(this.token === '' ? undefined : this.token),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (!response.ok) {
      throw new Error(
        `${method} ${pathAndQuery} answered ${response.status}: ${await response.text()}`,
      );
    }

    return response;
  }

  private async completeWizard(): Promise<void> {
    await this.send('POST', 'Startup/Configuration', {
      UICulture: 'en-US',
      MetadataCountryCode: 'US',
      PreferredMetadataLanguage: 'en',
    });

    // Asking for the first user creates it; only then can it be named.
    await this.send('GET', 'Startup/User');
    await this.send('POST', 'Startup/User', {
      Name: this.state.admin.name,
      Password: this.state.admin.password,
    });
    await this.send('POST', 'Startup/Complete');
  }

  /** Signs in for the setup's own calls and returns the user's id. */
  private async signIn(account: Account): Promise<string> {
    this.token = '';
    const response = await this.send('POST', 'Users/AuthenticateByName', {
      Username: account.name,
      Pw: account.password,
    });
    const session = (await response.json()) as { AccessToken: string; User: { Id: string } };
    this.token = session.AccessToken;
    return session.User.Id;
  }

  private async installPlugin(
    manifest: ManifestPlugin[],
    plugin: ManifestPlugin,
    version: ManifestVersion,
  ): Promise<void> {
    // The repository is this process for as long as the installation takes. The manifest is
    // served as packaged, except that the download address points here.
    const zipName = path.basename(new URL(version.sourceUrl).pathname);
    const zip = await readFile(path.join(this.options.packageDir, zipName));
    const repository: Server = createServer((request, response) => {
      const address = repository.address() as AddressInfo;
      if (request.url === '/repo/manifest.json') {
        version.sourceUrl = `http://127.0.0.1:${address.port}/repo/${zipName}`;
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify(manifest));
      } else if (request.url === `/repo/${zipName}`) {
        response.writeHead(200, { 'Content-Type': 'application/zip' });
        response.end(zip);
      } else {
        response.writeHead(404);
        response.end();
      }
    });
    repository.listen(0, '127.0.0.1');
    await once(repository, 'listening');

    try {
      const { port } = repository.address() as AddressInfo;
      const manifestUrl = `http://127.0.0.1:${port}/repo/manifest.json`;

      // Only this repository, so the test does not depend on Jellyfin's public catalog.
      await this.send('POST', 'Repositories', [
        { Name: 'Subtitle Miner (e2e)', Url: manifestUrl, Enabled: true },
      ]);

      const packages = (await (await this.send('GET', 'Packages')).json()) as { guid: string }[];
      const same = (guid: string): boolean =>
        guid.replaceAll('-', '').toLowerCase() === plugin.guid.replaceAll('-', '').toLowerCase();
      const offered = packages.filter((offer) => same(offer.guid)).length;
      if (offered !== 1) {
        throw new Error(`The server's catalog offers the plugin ${offered} times, expected once.`);
      }

      await this.send(
        'POST',
        `Packages/Installed/${encodeURIComponent(plugin.name)}?assemblyGuid=${plugin.guid}` +
          `&version=${version.version}&repositoryUrl=${encodeURIComponent(manifestUrl)}`,
      );

      // The download and unpacking run in the background.
      const deadline = Date.now() + 60_000;
      while (Date.now() < deadline) {
        const plugins = (await (await this.send('GET', 'Plugins')).json()) as { Id: string }[];
        if (plugins.some((installed) => same(installed.Id))) return;
        await sleep(500);
      }

      throw new Error('Jellyfin did not install the plugin within 60 s.');
    } finally {
      repository.close();
    }
  }

  private async shutDown(): Promise<void> {
    const child = this.process;
    if (child === null) return;
    const exited = once(child, 'exit');
    await this.send('POST', 'System/Shutdown');
    const stopped = await Promise.race([exited.then(() => true), sleep(60_000).then(() => false)]);
    if (!stopped) await this.kill();
    this.process = null;
    this.token = '';
  }

  private async kill(): Promise<void> {
    const child = this.process;
    this.process = null;
    if (child === null || child.exitCode !== null || child.signalCode !== null) return;
    const exited = once(child, 'exit');
    child.kill('SIGKILL');
    await Promise.race([exited, sleep(10_000)]);
  }
}
