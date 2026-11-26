# Changelog

## 2.1.0.1

- Dictionaries packed on a Japanese Windows show their pictures. Some, such as Daijirin with its label pictures (漢, 文, 派生 and the like), come in a zip that writes the pictures' names in Shift-JIS and puts the same names in UTF-8 into a field beside them. Yomitan reads that field and the plugin did not: it kept those pictures under unreadable names, and the popup said "Image failed to load" wherever a definition asked for one. The plugin now takes the names as Yomitan does. **Import such a dictionary once more** to get its pictures: the new import takes the old one's place, in the same spot of the list and on or off as it was.

## 2.1.0.0

- Dictionaries are imported in seconds. Jitendex, with 437,000 words, took 27 seconds and now takes 2 to 4 on the same machine; JMdict went from 15 seconds to under 2. The server reads a dictionary's files side by side on up to 8 processor cores instead of one after another, keeps each definition as the dictionary wrote it instead of taking it apart and writing it anew, and compresses definitions together in blocks instead of one by one, which was two thirds of the time before.
- Imported dictionaries take less room on the server: Jitendex 94 MB instead of 211 MB, JMdict 70 MB instead of 124 MB. Dictionaries imported by an earlier version keep working as they are; import one again to make it smaller.
- Several dictionaries can be added at once. Pick as many zips as you like in the file field: they are sent one after another and imported in that order, each as soon as it is there, and the page says at the end which are ready and why any are not. A dictionary added while another is imported waits its turn instead of being turned away. Stop ends the import that runs and lets go of those that wait.
- A dictionary whose zip is damaged or lies about the size of a file in it is refused with the file's name, and leaves nothing behind.

## 2.0.2.2

- Pictures in the definitions of a dictionary added while the page was open, such as the marks Daijirin puts before a word's old form, said "Image failed to load" until the page was reloaded. The page did not know the new dictionary yet, as when its import was still running as the settings were closed. It now fetches the list of dictionaries again when a word comes from one it does not know, so the pictures and the dictionary's own styles show at once. A picture that could not be fetched is also asked for again the next time it is shown.

## 2.0.2.1

- The plugin's own pickaxe logo is back as the icon of the installed app, the browser tab and the picture in Jellyfin's plugin catalog. Version 2 had replaced it with a stand-in.

## 2.0.2.0

- Once a card has the chosen lines, through "Add to my newest card" or "New card", the lines are unselected, ready for the next card. What happened to the card stays at the bottom, with Undo where there is one, until you choose other lines or close it. A card that fails leaves the lines chosen, to try again.

## 2.0.1.1

- Videos with many subtitle tracks inside, as streaming releases with one for each of 40 languages, now show every track. Before, the first 15 or so of them, often English and Japanese, said "The subtitle track could not be read", because the plugin kept the lines of at most 24 tracks and let the rest go straight after reading them. It now keeps the tracks of a video together, for up to 16 videos and subtitle files, and a track that cannot be read says why in the Jellyfin log.

## 2.0.1.0

- A "Sync Anki now" button in the top bar, next to the settings, whenever cards go to an Anki. It has that Anki sync with AnkiWeb right away instead of when it next would by itself, and says how it went: synced, already the same as AnkiWeb, or why it could not, such as a sync already running or AnkiWeb asking for a full sync that would replace changes not on AnkiWeb yet (then nothing is replaced). For an AnkiConnect on a server the Jellyfin server makes the call with its key, as for cards; desktop Anki starts its own sync.

## 2.0.0.0

Version 2, a rewrite for Jellyfin 12.4 and later. It installs from the new repository address, `https://raw.githubusercontent.com/friedrich-de/jellyfin-subtitleminer/main/repository/manifest.json`, and takes the place of version 1. Version 1's settings are not carried over.

- The page moved to `/SubtitleMiner/` at your Jellyfin address. The old address `/Plugins/JellyfinMiner/` leads there.
- No separate sign-in: the page uses the login of Jellyfin's own web client in the same browser, only ever the one to this server. Where there is none, it asks for your Jellyfin name and password once, also at a plain http:// address in your home network.
- Signing in works on Jellyfin 12, which no longer reads the header version 1 sent.
- The page follows what you play on any Jellyfin app without asking the server again and again: the plugin tells it when playback starts, pauses, jumps, changes track or stops, and the page runs the clock itself in between. A player that moves on to the next episode takes the page with it.
- Episodes and films you watched in the last 12 hours stay listed after playback ends, so you can still mine from them.
- The plugin reads the subtitles itself: ASS, SRT and WebVTT, from files next to the video or from inside it, in UTF-8, UTF-16 or Shift-JIS. Signs and songs are told apart from what people say; signs are hidden until you show them.
- The Japanese track is chosen by itself, a second track can be shown under each line for its meaning, and both choices are remembered for the next episode of the show.
- Pick lines one by one or as a range (shift), and the page shows the picture and plays the sound that the card will get. The lines picked are always one run: a line next to them adds to it, any other starts a new one. When you have scrolled away from what is playing, a button takes you back to the line playing. Pictures (jpeg, webp, avif, still or moving) and sound (mp3, opus) are cut on the server, at most two at a time, and only from videos you may watch.
- The new look: Jellyfin's own dark greys and blue, made for a phone or a second screen.
- Anki cards from the chosen lines: "Add to my newest card" puts the lines, the picture and the sound into the card you just made with Yomitan, keeping the word Yomitan marked, and Undo puts the card back. "New card" makes one in the deck and note type you choose; where the note type's first field is for a word, as in Lapis, a card of lines alone gets the lines there. A card Anki would refuse, as one the deck has already, is refused before its picture and sound are stored. "Add to my newest card" names the card it will change by its first field and how long ago it was made, and is greyed out, saying why, when there is no card it may take: none made today, or one made too long ago (15 minutes by default). It looks again on its own while you mine, so a card Yomitan just made shows up.
- Cards go to an AnkiConnect on the device you use the page on (desktop Anki or AnkiConnect for Android), or to an AnkiConnect on a server, which the Jellyfin server calls for you with its key. Nothing has to run on your device then: the server's AnkiConnect gets the cards and the pictures and sound. The key stays on the Jellyfin server, encrypted, and the page never gets it back. Only the calls that read and add notes go through, never ones that delete or open windows.
- Sending cards through the server is for administrators unless an administrator lets everyone, since the server then calls whatever address a user types.
- The page installs as an app from the browser, with its own icon. The app opens without the server and says it cannot reach it; it keeps only the page's own files, never subtitles, pictures, sound or settings. Installing needs Jellyfin over HTTPS, or the address of the device itself.
- "Open Jellyfin" on the sign-in screen opens beside the page, which goes on by itself once you have signed in there.
- A dictionary on the Jellyfin server, so that looking up words needs no browser extension. Tap a word in a line, or hold Shift over it as with Yomitan, and a popup shows it, as Yomitan would: conjugations are undone with Yomitan's own rules, with each dictionary's meanings, tags, pictures, frequencies and pitch accents. On a phone the popup is a sheet at the bottom, which stays inside the part of the page you see, also when you have zoomed in. Each user can make its text smaller or larger, hide the tags (★, the dictionary's name, parts of speech and the like) and lay definitions out compactly, each meaning on one line as in Yomitan. Its + button makes a new card with the word, its reading and furigana, the meaning from the first dictionary, the part of a definition you selected in the popup (SelectionText, as Yomitan's {selection-text}), the frequency, the pitch, the word's sound, the sentence with the word in bold, the picture and the sound; a word already in the deck says so.
- Word audio from an audio source as Yomitan takes one, "Custom URL (JSON)" or "Custom URL", with {term} and {reading} in its address. An administrator sets it once for everyone; the Jellyfin server fetches each word's sound, so its address and any key in it stay on the server, encrypted, and never reach a page or the log. The popup gets a play button, and a word's card its ExpressionAudio. As in Yomitan, holding the play button (or right-click, Shift-click) lists the word's sounds by name; the one you choose plays from then on and goes on the card. A word the source has no sound for still gets its card.
- Administrators add Yomitan dictionaries in the settings: Jitendex and JMnedict with one tap, any other address, or a file. The server imports them (Jitendex takes about half a minute), and they can be put in order, turned off and removed. Every user may look words up; only administrators change the dictionaries.
- Looking up can be turned off in each user's settings, for those who mine with their own Yomitan.
- Settings are kept on the Jellyfin server for each user, so they follow you from device to device: where cards go, the deck, the note type and what goes into each field (guessed from the field names, for Lapis and most mining note types, and each choice named as Lapis names its field, with an example), tags, the picture's format and width, and how much sound to keep before and after the lines.
- A new user whose Anki has a Lapis note type (known by its fields, so also Kiku or a copy under another name) gets it chosen when Anki is connected, with the deck its newest card is in and every field filled as Lapis means it, PitchCategories and FreqSort included. "Restore defaults" in the settings puts every choice back that way and keeps where the cards go.

## 1.1.0.1

The last release of version 1, for Jellyfin 10.11. Its source is at the tag `v1.1.0.1`.
