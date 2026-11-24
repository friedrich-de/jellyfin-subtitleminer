# Where the Japanese rules come from

`transforms.json` is Yomitan's set of rules for undoing Japanese inflections, written as data by the
[Hoshidicts](https://github.com/bee-san/hoshidicts) Japanese language pack (`languages/ja/transforms.json` at
`088ac5d`), copied unchanged.

- The rules and their descriptions are Yomitan's
  ([`ext/js/language/ja/japanese-transforms.js`](https://github.com/yomidevs/yomitan/blob/e7334a96106a6c74e6fa75bd33a5ea1219d92712/ext/js/language/ja/japanese-transforms.js)),
  GPL-3.0-or-later, Copyright (C) 2024-2026 Yomitan Authors.
- The transforms marked `"source": "own"` (such as `-ください` and `-ながら`) were written for Hoshidicts,
  GPL-3.0-or-later.

The tests in `Jellyfin.Plugin.SubtitleMiner.Tests/Dictionary/Japanese/` come from the same pack: `yomitan.tsv` is
Yomitan's Japanese conjugation test corpus (GPL-3.0-or-later, Copyright (C) 2023-2026 Yomitan Authors and 2020-2022
Yomichan Authors) and `cases.tsv` was written for Hoshidicts (GPL-3.0-or-later).

To update: copy the pack's `languages/ja/transforms.json` and `languages/ja/tests/*.tsv` from a newer Hoshidicts,
note its commit here, and run the plugin's tests.
