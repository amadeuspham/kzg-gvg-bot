#!/bin/sh
# Renders the four jungle call clips with macOS `say`, as 48 kHz mono 16-bit WAV, into the bot's embedded clips.
# Re-run it to change a phrase, or drop any WAV over a clip in src/Kzg.Bot/Jungle/audio/. Rebuild after.
set -eu
cd "$(dirname "$0")/../src/Kzg.Bot/Jungle/audio"
VOICE="${VOICE:-Samantha}"
render() {
  say -v "$VOICE" -o "$1.aiff" "$2"
  afconvert -f WAVE -d LEI16@48000 -c 1 "$1.aiff" "$1.wav"
  rm -f "$1.aiff"
}
render jungle40 "Jungle in 40 seconds"
render jungle20 "Jungle in 20 seconds"
render jungle-halftime40 "Jungle and halftime in 40 seconds"
render jungle-halftime20 "Jungle and halftime in 20 seconds"
