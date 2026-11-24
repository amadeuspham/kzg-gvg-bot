#!/bin/bash
# Runs the Kzg bot on this Mac as a background service of this user: it starts at login, starts again after
# a crash, and keeps the Mac awake while it runs. Double-click it in Finder (choose Open if macOS asks), or
# run it in Terminal. Apple Silicon only. The first time, it opens the setup page in the browser.
#
#   ./install-mac.command                 install, or update to this folder, and start the bot
#   ./install-mac.command --remove        stop the bot and remove the service; the data folder stays
#   ./install-mac.command --plist <file>  only write the service file to <file>, to inspect it
set -euo pipefail

label="com.kzg.bot"
here="$(cd "$(dirname "$0")" && pwd)"
data="$HOME/Library/Application Support/Kzg"
logs="$HOME/Library/Logs/Kzg"
log="$logs/bot.log"
agent="$HOME/Library/LaunchAgents/$label.plist"
domain="gui/$(id -u)"

fail() { echo "install-mac: $*" >&2; exit 1; }

# Paths go into XML, so &, < and > are escaped.
xml() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

# KeepAlive restarts the bot whenever it ends other than cleanly: a crash, a kill, a failed start (every
# 10 s at most). A clean stop (exit 0) stays stopped until the next login. Interactive keeps macOS from
# throttling it as a background job, which would make the audio stutter. The bot writes its own log
# (--log-file), kept to 30 MB; launchd's own output files would grow without end, so they go nowhere.
write_plist() {
    cat > "$1" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$label</string>
    <key>ProgramArguments</key>
    <array>
        <string>$(xml "$here/Kzg.Bot")</string>
        <string>--data-dir</string>
        <string>$(xml "$data")</string>
        <string>--log-file</string>
        <string>$(xml "$log")</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$(xml "$here")</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>ThrottleInterval</key>
    <integer>10</integer>
    <key>ExitTimeOut</key>
    <integer>20</integer>
    <key>ProcessType</key>
    <string>Interactive</string>
    <key>StandardOutPath</key>
    <string>/dev/null</string>
    <key>StandardErrorPath</key>
    <string>/dev/null</string>
</dict>
</plist>
EOF
    plutil -lint -s "$1" || fail "the service file $1 is not a valid property list"
}

[ "$(uname -s)" = Darwin ] || fail "this is for macOS"

case "${1:-}" in
    --plist)
        write_plist "${2:?--plist needs a file}"
        echo "wrote $2"
        exit 0
        ;;
    --remove)
        launchctl bootout "$domain/$label" 2>/dev/null || true
        rm -f "$agent"
        echo "The Kzg bot is stopped and won't start at login any more. Its data stays in: $data"
        exit 0
        ;;
    "") ;;
    *) fail "unknown option '$1'" ;;
esac

[ "$(uname -m)" = arm64 ] || fail "the bot runs on Apple Silicon Macs only (M1 or newer), not on Intel Macs"
[ -x "$here/Kzg.Bot" ] || fail "Kzg.Bot isn't beside this script ($here); unpack the whole archive first"

# A downloaded archive is quarantined, and macOS would refuse the unsigned bot. Apple Silicon runs only
# signed code, so anything unsigned (a package built on another OS) is signed ad hoc, for this Mac alone.
xattr -dr com.apple.quarantine "$here" 2>/dev/null || true
while IFS= read -r -d '' file; do
    codesign --verify "$file" 2>/dev/null || codesign --force --sign - "$file" 2>/dev/null \
        || fail "couldn't sign $file for this Mac"
done < <(find "$here" -type f \( -name Kzg.Bot -o -name '*.dylib' \) -print0)

mkdir -p "$data" "$logs" "$(dirname "$agent")"
# The data folder holds the bot tokens: this user's alone.
chmod 700 "$data"

launchctl bootout "$domain/$label" 2>/dev/null || true
write_plist "$agent"
size() { if [ -f "$log" ]; then wc -c < "$log" | tr -d ' '; else echo 0; fi; }
offset=$(( $(size) + 1 ))
launchctl bootstrap "$domain" "$agent" || fail "launchctl couldn't load $agent"

echo "The Kzg bot runs now, starts at every login, and starts again if it stops by accident."
echo "Its log: $log (open it with Console). It is kept to 30 MB: older lines move to bot.1.log and bot.2.log."
echo "macOS may show a notice that a background item was added; that is this bot."

if [ ! -f "$data/config.json" ]; then
    echo "Waiting for the setup link..."
    for _ in $(seq 1 60); do
        # The log may have started a new file since: then read it whole.
        [ "$(size)" -ge "$offset" ] || offset=1
        url="$( (tail -c +"$offset" "$log" 2>/dev/null || true) | grep -o 'http://localhost:[0-9]*/setup?key=[A-Za-z0-9_-]*' | tail -n 1 || true)"
        if [ -n "$url" ]; then
            echo "Opening the setup page in your browser."
            open "$url"
            exit 0
        fi
        sleep 1
    done
    echo "The setup link didn't appear yet. Look for it in $log, or run: tail -f \"$log\"" >&2
fi
