#!/usr/bin/env bash
# Builds the page and the plugin and packs them the way Jellyfin installs plugins: one zip,
# plus a repository manifest that points at it.
#
#   dev/package.sh [BASE_URL]     a local test repository in dist/
#   dev/package.sh --publish      this version added to the public repository in repository/
#
# The page in web/ is built first, because the plugin carries it inside its one DLL.
#
# Local: BASE_URL is where dist/ will be served from (default http://127.0.0.1:8765).
# Jellyfin downloads the zip from BASE_URL/<zip name> and checks it against the
# manifest's checksum. Output: dist/subtitle-miner_<version>.zip and dist/manifest.json.
#
# Publish: the zip goes into repository/, and repository/manifest.json gets an entry
# for this version, with its section of CHANGELOG.md as the changelog. The versions
# already in the manifest stay. Jellyfin reads the manifest from the main branch on
# GitHub (PUBLIC_URL below), so a version is out once the commit that adds it is on main.
# A version that is in the manifest already is never built again: its checksum would
# change under the people who installed it. Raise the version instead.
set -euo pipefail

cd "$(dirname "$0")/.."
PUBLIC_URL="https://raw.githubusercontent.com/friedrich-de/jellyfin-subtitleminer/main/repository"
PROJECT="Jellyfin.Plugin.SubtitleMiner"

publish=false
if [ "${1:-}" = "--publish" ]; then
    publish=true
    base_url="$PUBLIC_URL"
    out=repository
else
    base_url="${1:-http://127.0.0.1:8765}"
    base_url="${base_url%/}"
    out=dist
fi

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
jellyfin_version="$(sed -n 's:.*<JellyfinVersion>\(.*\)</JellyfinVersion>.*:\1:p' Directory.Build.props)"
guid="$(sed -n 's:.*PluginId = Guid.Parse("\(.*\)").*:\1:p' "$PROJECT/Plugin.cs")"
[ -n "$version" ] && [ -n "$jellyfin_version" ] && [ -n "$guid" ] || { echo "could not read the version, the Jellyfin version or the plugin id" >&2; exit 1; }
zip_name="subtitle-miner_${version}.zip"

# The changelog of this version: its section of CHANGELOG.md, without the heading.
changelog="$(awk -v v="## ${version}" '
    index($0, v) == 1 { on = 1; next }
    on && /^## / { exit }
    on { print }' CHANGELOG.md | sed '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}')"
if [ -z "$changelog" ]; then
    if $publish; then
        echo "CHANGELOG.md has no section \"## ${version}\"" >&2
        exit 1
    fi
    changelog="A build for testing."
fi

if $publish; then
    mkdir -p "$out"
    if [ -e "$out/$zip_name" ] || { [ -e "$out/manifest.json" ] && grep -q "\"version\": \"${version}\"" "$out/manifest.json"; }; then
        echo "${version} is published already. Raise <Version> in Directory.Build.props first." >&2
        exit 1
    fi
else
    rm -rf "$out"
    mkdir -p "$out"
fi

# The page. Its packages are installed from the lock file when they are missing or older than it.
(
    cd web
    if [ ! -d node_modules ] || [ package-lock.json -nt node_modules/.package-lock.json ]; then
        npm ci --no-audit --no-fund
    fi
    npm run --silent build -- --logLevel warn
)
[ -f web/dist/index.html ] || { echo "the page was not built: web/dist/index.html is missing" >&2; exit 1; }

dotnet build "$PROJECT" -c Release --nologo -v quiet

# The server ships every other assembly the plugin uses, and the page is inside the DLL,
# so the zip holds the one file.
(cd "$PROJECT/bin/Release/net10.0" && zip -q -X "../../../../${out}/${zip_name}" "$PROJECT.dll")

checksum="$(md5sum "${out}/${zip_name}" | cut -d' ' -f1)"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# The manifest: the plugin once, with its versions newest first.
VERSION="$version" TARGET_ABI="${jellyfin_version}.0" SOURCE_URL="${base_url}/${zip_name}" \
CHECKSUM="$checksum" TIMESTAMP="$timestamp" CHANGELOG="$changelog" GUID="$guid" \
MANIFEST="${out}/manifest.json" python3 -I - <<'PY'
import json
import os
from pathlib import Path

path = Path(os.environ["MANIFEST"])
entry = {
    "version": os.environ["VERSION"],
    "changelog": os.environ["CHANGELOG"],
    "targetAbi": os.environ["TARGET_ABI"],
    "sourceUrl": os.environ["SOURCE_URL"],
    "checksum": os.environ["CHECKSUM"],
    "timestamp": os.environ["TIMESTAMP"],
}
older = []
if path.exists():
    older = json.loads(path.read_text(encoding="utf-8"))[0]["versions"]


def key(item):
    return tuple(int(part) for part in item["version"].split("."))


plugin = {
    "guid": os.environ["GUID"],
    "name": "Subtitle Miner",
    "overview": "Follow the subtitles of what you watch and make Anki cards from them.",
    "description": (
        "A page for your phone or a second screen that follows what is playing on Jellyfin. "
        "It shows the subtitle lines as they are spoken and makes Anki cards from them, with "
        "the sentence, a picture and the audio cut from the video."
    ),
    "owner": "friedrich-de",
    "category": "Subtitles",
    "versions": sorted([entry, *older], key=key, reverse=True),
}
path.write_text(json.dumps([plugin], indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
PY

echo "${out}/${zip_name} (md5 ${checksum})"
echo "${out}/manifest.json -> ${base_url}/manifest.json"
