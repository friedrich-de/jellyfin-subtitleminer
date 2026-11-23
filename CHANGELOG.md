# Changelog

## 2.0.0.0

Not released yet. Version 2 is a rewrite for Jellyfin 12.4 and later, built in steps on the main branch.

Done so far:

- The page moved to `/SubtitleMiner/` at your Jellyfin address. The old address `/Plugins/JellyfinMiner/` leads there.
- No separate sign-in: the page uses the login of Jellyfin's own web client in the same browser. Where there is none, it asks for your Jellyfin name and password once.
- Signing in works on Jellyfin 12, which no longer reads the header version 1 sent.

## 1.1.0.1

The last release of version 1, for Jellyfin 10.11. Its source is at the tag `v1.1.0.1`.
