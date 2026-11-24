# Changelog

## 2.0.0.0

Version 2, a rewrite for Jellyfin 12.4 and later. It installs from the new repository address, `https://raw.githubusercontent.com/friedrich-de/jellyfin-subtitleminer/main/repository/manifest.json`, and takes the place of version 1. Version 1's settings are not carried over.

- The page moved to `/SubtitleMiner/` at your Jellyfin address. The old address `/Plugins/JellyfinMiner/` leads there.
- No separate sign-in: the page uses the login of Jellyfin's own web client in the same browser, only ever the one to this server. Where there is none, it asks for your Jellyfin name and password once, also at a plain http:// address in your home network.
- Signing in works on Jellyfin 12, which no longer reads the header version 1 sent.
- The page follows what you play on any Jellyfin app without asking the server again and again: the plugin tells it when playback starts, pauses, jumps, changes track or stops, and the page runs the clock itself in between. A player that moves on to the next episode takes the page with it.
- Episodes and films you watched in the last 12 hours stay listed after playback ends, so you can still mine from them.
- The plugin reads the subtitles itself: ASS, SRT and WebVTT, from files next to the video or from inside it, in UTF-8, UTF-16 or Shift-JIS. Signs and songs are told apart from what people say; signs are hidden until you show them.
- The Japanese track is chosen by itself, a second track can be shown under each line for its meaning, and both choices are remembered for the next episode of the show.
- Pick lines one by one or as a range (shift), and the page shows the picture and plays the sound that the card will get. Pictures (jpeg, webp, avif, still or moving) and sound (mp3, opus) are cut on the server, at most two at a time, and only from videos you may watch.
- The new look: Jellyfin's own dark greys and blue, made for a phone or a second screen.
- Anki cards from the chosen lines: "Add to my newest card" puts the lines, the picture and the sound into the card you just made with Yomitan, keeping the word Yomitan marked, and Undo puts the card back. "New card" makes one in the deck and note type you choose; where the note type's first field is for a word, as in Lapis, a card of lines alone gets the lines there. A card Anki would refuse, as one the deck has already, is refused before its picture and sound are stored. A card made too long ago (15 minutes by default) is not taken for the newest one.
- Cards go to an AnkiConnect on the device you use the page on (desktop Anki or AnkiConnect for Android), or to an AnkiConnect on a server, which the Jellyfin server calls for you with its key. Nothing has to run on your device then: the server's AnkiConnect gets the cards and the pictures and sound. The key stays on the Jellyfin server, encrypted, and the page never gets it back. Only the calls that read and add notes go through, never ones that delete or open windows.
- Sending cards through the server is for administrators unless an administrator lets everyone, since the server then calls whatever address a user types.
- The page installs as an app from the browser, with its own icon. The app opens without the server and says it cannot reach it; it keeps only the page's own files, never subtitles, pictures, sound or settings. Installing needs Jellyfin over HTTPS, or the address of the device itself.
- "Open Jellyfin" on the sign-in screen opens beside the page, which goes on by itself once you have signed in there.
- A dictionary on the Jellyfin server, so that looking up words needs no browser extension. Tap a word in a line, or hold Shift over it as with Yomitan, and a popup shows it, as Yomitan would: conjugations are undone with Yomitan's own rules, with each dictionary's meanings, tags, pictures, frequencies and pitch accents. On a phone the popup is a sheet at the bottom. Its + button makes a new card with the word, its reading and furigana, the meaning from the first dictionary, the part of a definition you selected in the popup (SelectionText, as Yomitan's {selection-text}), the frequency, the pitch, the word's sound, the sentence with the word in bold, the picture and the sound; a word already in the deck says so.
- Word audio from an audio source as Yomitan takes one, "Custom URL (JSON)" or "Custom URL", with {term} and {reading} in its address. An administrator sets it once for everyone; the Jellyfin server fetches each word's sound, so its address and any key in it stay on the server, encrypted, and never reach a page or the log. The popup gets a play button, and a word's card its ExpressionAudio. A word the source has no sound for still gets its card.
- Administrators add Yomitan dictionaries in the settings: Jitendex and JMnedict with one tap, any other address, or a file. The server imports them (Jitendex takes about half a minute), and they can be put in order, turned off and removed. Every user may look words up; only administrators change the dictionaries.
- Looking up can be turned off in each user's settings, for those who mine with their own Yomitan.
- Settings are kept on the Jellyfin server for each user, so they follow you from device to device: where cards go, the deck, the note type and what goes into each field (guessed from the field names, for Lapis and most mining note types, and each choice named as Lapis names its field, with an example), tags, the picture's format and width, and how much sound to keep before and after the lines.

## 1.1.0.1

The last release of version 1, for Jellyfin 10.11. Its source is at the tag `v1.1.0.1`.
