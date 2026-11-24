# Changelog

## 2.0.0.0

Not released yet. Version 2 is a rewrite for Jellyfin 12.4 and later, built in steps on the main branch.

Done so far:

- The page moved to `/SubtitleMiner/` at your Jellyfin address. The old address `/Plugins/JellyfinMiner/` leads there.
- No separate sign-in: the page uses the login of Jellyfin's own web client in the same browser. Where there is none, it asks for your Jellyfin name and password once.
- Signing in works on Jellyfin 12, which no longer reads the header version 1 sent.
- The page follows what you play on any Jellyfin app without asking the server again and again: the plugin tells it when playback starts, pauses, jumps, changes track or stops, and the page runs the clock itself in between. A player that moves on to the next episode takes the page with it.
- Episodes and films you watched in the last 12 hours stay listed after playback ends, so you can still mine from them.
- The plugin reads the subtitles itself: ASS, SRT and WebVTT, from files next to the video or from inside it, in UTF-8, UTF-16 or Shift-JIS. Signs and songs are told apart from what people say; signs are hidden until you show them.
- The Japanese track is chosen by itself, a second track can be shown under each line for its meaning, and both choices are remembered for the next episode of the show.
- Pick lines one by one or as a range (shift), and the page shows the picture and plays the sound that the card will get. Pictures (jpeg, webp, avif, still or moving) and sound (mp3, opus) are cut on the server, at most two at a time, and only from videos you may watch.
- The new look: Jellyfin's own dark greys and blue, made for a phone or a second screen.

## 1.1.0.1

The last release of version 1, for Jellyfin 10.11. Its source is at the tag `v1.1.0.1`.
