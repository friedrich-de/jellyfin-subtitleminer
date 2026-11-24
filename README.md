# Subtitle Miner for Jellyfin

Watch on any Jellyfin client (TV, mobile, web) and make Anki cards from the subtitles on any device, with the sentence, a picture and the audio cut from the video. Mining from your couch.

Only watching locally with mpv? Check out [mpv-subtitleminer](https://github.com/friedrich-de/mpv-subtitleminer) instead.

## Where things stand

The main branch holds **version 2, which is being rebuilt from scratch and is not released yet**. It is for Jellyfin 12.4 and later.

| | Version 1 (1.1.0.1) | Version 2 (main) |
| --- | --- | --- |
| Jellyfin | 10.11 | 12.4 and later |
| State | Released, no longer developed | In progress, not released |
| Source | [tag v1.1.0.1](https://github.com/friedrich-de/jellyfin-subtitleminer/tree/v1.1.0.1) | this branch |
| Install and usage | [README of v1.1.0.1](https://github.com/friedrich-de/jellyfin-subtitleminer/blob/v1.1.0.1/README.md) | follows with the release |

Version 1 was called Jellyfin Miner. Its sign-in does not work on Jellyfin 12, because Jellyfin 12 no longer reads the header it sends.

What version 2 can do today:

- The page lives at `/SubtitleMiner/` at your Jellyfin address. The address of version 1, `/Plugins/JellyfinMiner/`, leads there.
- No separate sign-in. The page uses the login of Jellyfin's web client in the same browser. Where there is none, it asks for your Jellyfin name and password once.
- It follows what you play on any Jellyfin app, line by line, and keeps what you watched in the last hours. You pick lines and see the picture and hear the sound the card will get.
- It makes Anki cards: it adds the lines, the picture and the sound to the card you just made with Yomitan, or makes a new one. Cards go to AnkiConnect on your device, or to an AnkiConnect on a server with its API key, which the Jellyfin server calls for you, so nothing needs to run on your device.

- It installs as an app on a phone, tablet or computer, from the browser's menu, when Jellyfin is opened over HTTPS.

What comes next: a dictionary hosted on the Jellyfin server. [CHANGELOG.md](CHANGELOG.md) lists what is done.

## Development

The plugin is C# for .NET 10 in `Jellyfin.Plugin.SubtitleMiner/`. The page is Vue and TypeScript in `web/`; its build is embedded in the plugin's one DLL.

You need the .NET 10 SDK, Node.js 22 or newer, `zip`, Python 3 and, for the end-to-end tests, ffmpeg.

```sh
# The plugin: build (warnings are errors) and unit tests
dotnet build -c Release
dotnet test -c Release

# The page: types, lint, formatting and unit tests
cd web && npm ci && npm run check

# The package Jellyfin installs: dist/subtitle-miner_<version>.zip and dist/manifest.json
dev/package.sh

# End to end: a throwaway Jellyfin 12.4 with the package installed through Jellyfin's own
# plugin installer, then the plugin's addresses over HTTP and the page in a real browser
# at desktop and at phone size
dev/e2e.sh
```

`dev/e2e.sh` downloads the Jellyfin server once into `.cache/` (about 110 MB, x86-64 Linux) and needs Playwright's Chromium (`cd web && npx playwright install chromium`). The top of the script says how to use an installed server instead. The tests make their own videos with ffmpeg (colours that change every five seconds, a hum on each audio track, subtitles inside and next to the video), play them with a pretend player through Jellyfin's own playback calls, and keep the server off the internet. `SUBTITLE_MINER_E2E_SCREENS=<folder> dev/e2e.sh screens` takes pictures of the page.

To work on the page against a Jellyfin that has the plugin installed: `cd web && JELLYFIN_URL=http://localhost:8096 npm run dev`.

The app's icons are drawn from `web/icons/*.svg` into `web/public/icons/` with `node dev/render-icons.mjs` (it uses Playwright's Chromium); the PNGs are committed. The page's service worker (`web/src/worker/`) is built into `sw.js` with the list of the build's files, and keeps nothing else.

Nothing runs on GitHub by itself. Whoever makes a change runs the checks above before merging.

## Philosophy

A lot of great tools for language learning and media mining exist, but they place the learner in artificial learning environments that they would not use if they weren't actively engaged in language learning. In my opinion, this is an issue. Immersion should be performed in the same environment as leisure media consumption. This plugin fixes that gap - watch shows as you would normally. Only when you want to mine a subtitle do you need to open the plugin interface, and you can do that on any device. Watch on TV, mine on mobile. Watch on desktop, mine on desktop. The choice is yours.

## Licence

GPL-3.0, see [LICENSE](LICENSE). Jellyfin plugins link Jellyfin's libraries, which are GPL-3.0.

The rules the dictionary uses to undo Japanese conjugations are [Yomitan](https://github.com/yomidevs/yomitan)'s, as the [Hoshidicts](https://github.com/bee-san/hoshidicts) Japanese language pack writes them (GPL-3.0-or-later); [Dictionary/Japanese/SOURCES.md](Jellyfin.Plugin.SubtitleMiner/Dictionary/Japanese/SOURCES.md) says where each file comes from. No dictionary comes with the plugin: an administrator downloads or uploads the ones they want, under their own licences.
