#!/usr/bin/env bash
# Starts the bot from the folder this file is in.
# Pass --data-dir <folder> to keep config.json and secrets.json somewhere else than the default.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
exec "$here/Kzg.Bot" "$@"
