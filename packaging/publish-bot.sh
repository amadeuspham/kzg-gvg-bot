#!/usr/bin/env bash
# Builds the bot as a self-contained folder for one OS, with its native libraries, and packs it: .zip for Windows, .tar.gz for Linux and macOS. Nothing is uploaded or released.
#
# Usage: packaging/publish-bot.sh --rid <win-x64|linux-x64|linux-arm64|osx-arm64> [--out <dir>] [--no-archive]
#   --out <dir>    where the folder and archive go (default: packaging/out)
#   --no-archive   leave the folder, skip the archive (the Dockerfile copies the folder)
# Any machine can build any of these: .NET cross-publishes. The native libraries come from the NuGet
# package: libdave, pinned to PINNED_LIBDAVE. macOS is Apple Silicon only. Built on a Mac, the bot is
# signed ad hoc, as Apple Silicon needs to run it; built elsewhere, install-mac.command signs it on the Mac.
set -euo pipefail

PINNED_LIBDAVE=1.2.1.9

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
if [ -f scripts/dotnet.sh ]; then . scripts/dotnet.sh; else DOTNET="${DOTNET:-dotnet}"; fi

rid="" out="$repo/packaging/out" archive=1
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rid="${2:?--rid needs a value}"; shift 2 ;;
        --out) out="${2:?--out needs a value}"; shift 2 ;;
        --no-archive) archive=0; shift ;;
        *) echo "unknown option '$1'" >&2; exit 64 ;;
    esac
done
case "$rid" in
    win-x64 | linux-x64 | linux-arm64 | osx-arm64) ;;
    *) echo "--rid must be win-x64, linux-x64, linux-arm64 or osx-arm64 (Intel Macs aren't supported)" >&2; exit 64 ;;
esac

version="$("$DOTNET" msbuild src/Kzg.Bot/Kzg.Bot.csproj -getProperty:Version | tr -d '\r')"
name="kzg-bot-$version-$rid"
stage="$out/$name"
rm -rf "$stage"
mkdir -p "$out"

echo "== publish $name"
"$DOTNET" publish src/Kzg.Bot/Kzg.Bot.csproj -c Release -r "$rid" --self-contained -o "$stage" -nologo -v:q

# libdave's package copies every OS's library into runtimes/; only this OS's is wanted.
for dir in "$stage"/runtimes/*/; do
    [ "$(basename "$dir")" = "$rid" ] || rm -rf "$dir"
done

case "$rid" in
    win-x64) exe=Kzg.Bot.exe; dave=libdave.dll ;;
    osx-*) exe=Kzg.Bot; dave=libdave.dylib ;;
    *) exe=Kzg.Bot; dave=libdave.so ;;
esac
for needed in "$exe" "runtimes/$rid/native/$dave"; do
    [ -f "$stage/$needed" ] || { echo "publish-bot: '$needed' is missing from the published folder" >&2; exit 1; }
done
# The csproj says 1.2.1.9, which NuGet reads as "at least"; the lock-in is what was restored.
if ! grep -q "\"libdave/$PINNED_LIBDAVE\"" src/Kzg.Discord/obj/project.assets.json; then
    echo "publish-bot: libdave $PINNED_LIBDAVE is not the version that was restored" >&2
    exit 1
fi

cp packaging/bot/config.example.json packaging/bot/secrets.example.json "$stage/"
case "$rid" in
    win-x64) for f in start.cmd install-windows-task.ps1; do sed 's/$/\r/' "packaging/bot/$f" > "$stage/$f"; done ;;
    osx-*)
        cp packaging/bot/start.sh packaging/bot/install-mac.command "$stage/"
        chmod +x "$stage/Kzg.Bot" "$stage/start.sh" "$stage/install-mac.command"
        ;;
    *)
        cp packaging/bot/start.sh packaging/bot/kzg-bot.service packaging/bot/kzg-bot-restart.service packaging/bot/kzg-bot-restart.timer "$stage/"
        chmod +x "$stage/Kzg.Bot" "$stage/start.sh"
        ;;
esac

if [ "$archive" = 0 ]; then
    echo "folder: $stage"
    exit 0
fi

echo "== archive"
case "$rid" in
    win-x64)
        file="$out/$name.zip"
        rm -f "$file"
        (cd "$out" && zip -qr "$name.zip" "$name")
        ;;
    *)
        file="$out/$name.tar.gz"
        # COPYFILE_DISABLE keeps macOS's extended attributes out of the archive.
        COPYFILE_DISABLE=1 tar -czf "$file" -C "$out" "$name"
        ;;
esac
if command -v sha256sum >/dev/null 2>&1; then sum="$(sha256sum "$file")"; else sum="$(shasum -a 256 "$file")"; fi
echo "archive: $file ($(du -h "$file" | cut -f1))"
echo "sha256:  ${sum%% *}"
