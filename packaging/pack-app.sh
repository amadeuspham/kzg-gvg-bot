#!/usr/bin/env bash
# Builds the PC app for one platform and packs it with Velopack into an installer, a portable zip and an
# update feed. Nothing is uploaded or released. Windows packs are never signed. A macOS pack is signed only
# when KZG_SIGN_IDENTITY is set, and is otherwise left to the ad-hoc signature of a dev build.
#
# Usage: packaging/pack-app.sh --rid <win-x64|osx-arm64|osx-x64> [--out <dir>] [--allow-missing-velopack-hook]
#   --out <dir>   where the publish folder and the Velopack output go (default: packaging/out)
#   --allow-missing-velopack-hook
#                 pack even if the app never calls VelopackApp.Build().Run(). vpk refuses by default,
#                 because an installer from such an app can't run its install and update hooks. Only for
#                 trying the packaging; never ship the result. Windows only: vpk makes no such check on macOS.
#
# Stable Mac signing (free, not notarized, so the first-install warning stays):
#   KZG_SIGN_IDENTITY      the name (or SHA-1 hash) of the maintainer's one self-made code-signing
#                          certificate, as `security find-identity -p codesigning` lists it. The same
#                          certificate on every release keeps the app's code identity the same, so macOS
#                          keeps the Input Monitoring grant across updates. It must be in a keychain on
#                          the keychain search list; vpk looks it up there. The certificate is kept as a
#                          password-protected .p12 outside the repo and never committed.
#
# Automatic updates:
#   KZG_UPDATE_FEED        an https address the app looks for updates at: a GitHub repository address
#                          (https://github.com/<owner>/<repo>) or a web folder holding the Velopack files.
#                          It is built into the app. Left unset, the app never looks for updates; the same
#                          variable set when the app runs overrides what was built in.
# The icons (app.ico, AppIcon.icns) are rendered from Assets/icon.svg by src/Kzg.App/Assets/render-icons.sh.
# Windows packs build on any OS. macOS packs build only on macOS: vpk needs Apple's tools. The Velopack
# tool's version is the one the app's Velopack package must have.
set -euo pipefail

VELOPACK_VERSION=1.2.161
# Windows installs to %LOCALAPPDATA%\<PACK_ID> and removes that folder on uninstall. The app keeps its
# settings in %LOCALAPPDATA%\Kzg\App and the bot its data in %LOCALAPPDATA%\Kzg, so the id is not "Kzg".
# The id names the install folder and the update feed, so it stays even though players never see it: changing
# it would strand installed apps. The title below is what players see (the Mac .app is named after it);
# DISPLAY_PREFIX names the installer files they download.
PACK_ID=KZGBroadcast
PACK_TITLE="Discord Broadcast"
DISPLAY_PREFIX=DiscordBroadcast

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
if [ -f scripts/dotnet.sh ]; then . scripts/dotnet.sh; else DOTNET="${DOTNET:-dotnet}"; fi
# vpk's launcher finds .NET through DOTNET_ROOT, and falls back to a system install that may be the wrong CPU.
case "$DOTNET" in /*) export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$DOTNET")}" ;; esac

rid="" out="$repo/packaging/out" allow=0 check=()
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rid="${2:?--rid needs a value}"; shift 2 ;;
        --out) out="${2:?--out needs a value}"; shift 2 ;;
        --allow-missing-velopack-hook) allow=1; shift ;;
        *) echo "unknown option '$1'" >&2; exit 64 ;;
    esac
done
case "$rid" in
    win-x64)
        os='[win]'; mainExe=Kzg.App.exe; icon=src/Kzg.App/Assets/app.ico
        [ "$allow" = 0 ] || check=(--skipVeloAppCheck)
        ;;
    osx-arm64 | osx-x64)
        os='[osx]'; mainExe=Kzg.App; icon=src/Kzg.App/Assets/AppIcon.icns
        [ "$(uname -s)" = Darwin ] || { echo "pack-app: macOS packages can only be built on a Mac" >&2; exit 1; }
        ;;
    *) echo "--rid must be win-x64, osx-arm64 or osx-x64" >&2; exit 64 ;;
esac

# Signing options for vpk, empty unless an identity is given and this is a Mac pack. The identity is
# checked first so that a typo fails here, not after a long publish.
sign=()
if [ "$os" = '[osx]' ] && [ -n "${KZG_SIGN_IDENTITY:-}" ]; then
    if ! security find-identity -p codesigning 2>/dev/null | grep -qF -- "$KZG_SIGN_IDENTITY"; then
        echo "pack-app: no code-signing identity matching '$KZG_SIGN_IDENTITY' in the keychain search list" >&2
        exit 1
    fi
    sign=(--signAppIdentity "$KZG_SIGN_IDENTITY")
    echo "== signing the app with '$KZG_SIGN_IDENTITY'"
elif [ "$os" = '[osx]' ]; then
    echo "== not signing: KZG_SIGN_IDENTITY is not set"
fi
# vpk's own bundle id comes from the title and has an underscore, which Apple ids may not contain.
# A fixed id also keeps macOS's Input Monitoring grant on one identity across renames.
[ "$os" != '[osx]' ] || sign+=(--bundleId app.kzg.broadcast)

tools="$repo/packaging/.tools"
if [ ! -x "$tools/vpk" ] || ! "$tools/vpk" --help 2>/dev/null | grep -q "CLI $VELOPACK_VERSION"; then
    echo "== install vpk $VELOPACK_VERSION"
    "$DOTNET" tool install vpk --version "$VELOPACK_VERSION" --tool-path "$tools" >/dev/null 2>&1 \
        || "$DOTNET" tool update vpk --version "$VELOPACK_VERSION" --tool-path "$tools" >/dev/null
fi

version="$("$DOTNET" msbuild src/Kzg.App/Kzg.App.csproj -getProperty:Version | tr -d '\r')"
publish="$out/publish/app-$rid"
feed="$out/velopack-$rid"
rm -rf "$publish" "$feed"

echo "== publish app $version $rid"
feedProperty=()
[ -z "${KZG_UPDATE_FEED:-}" ] || feedProperty=("-p:KzgUpdateFeed=$KZG_UPDATE_FEED")
"$DOTNET" publish src/Kzg.App/Kzg.App.csproj -c Release -r "$rid" --self-contained -o "$publish" -nologo -v:q ${feedProperty[@]+"${feedProperty[@]}"}

echo "== pack"
"$tools/vpk" "$os" pack \
    --packId "$PACK_ID" --packTitle "$PACK_TITLE" --packAuthors "$PACK_TITLE" --icon "$icon" --packVersion "$version" \
    --packDir "$publish" --mainExe "$mainExe" --runtime "$rid" --channel "$rid" \
    --outputDir "$feed" ${check[@]+"${check[@]}"} ${sign[@]+"${sign[@]}"}
# Players see the installer names, so they carry the display prefix and the Mac ones say macOS. Only the
# two user-facing files per pack are renamed: the channel, the RID, the .nupkg, RELEASES-* and *.json names
# keep $PACK_ID and the RID, because installed apps update from that channel. The assets json lists the
# installers, so it is kept in step.
case "$rid" in
    win-x64) label="$rid"; kinds="Setup.exe Portable.zip" ;;
    *) label="macOS-${rid#osx-}"; kinds="Setup.pkg Portable.zip" ;;
esac
for kind in $kinds; do
    old="$PACK_ID-$rid-$kind"; new="$DISPLAY_PREFIX-$label-$kind"
    [ -f "$feed/$old" ] || { echo "pack-app: vpk did not write $old" >&2; exit 1; }
    mv "$feed/$old" "$feed/$new"
    sed -i.bak "s/$old/$new/g" "$feed/assets.$rid.json" && rm -f "$feed/assets.$rid.json.bak"
done
echo "output: $feed"
ls -1 "$feed"
