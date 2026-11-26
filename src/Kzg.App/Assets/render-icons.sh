#!/usr/bin/env bash
# Regenerates app.ico, AppIcon.icns and icon-256.png from icon.svg. macOS only: it uses qlmanage
# (renders the SVG, filters included), sips, iconutil and python3 (standard library only).
set -euo pipefail
cd "$(dirname "$0")"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

qlmanage -t -s 1024 -o "$work" icon.svg >/dev/null 2>&1
master="$work/icon.svg.png"
[ -s "$master" ] || { echo "qlmanage produced no render" >&2; exit 1; }

sips -z 256 256 "$master" --out icon-256.png >/dev/null

iconset="$work/AppIcon.iconset"
mkdir "$iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$master" --out "$iconset/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) "$master" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o AppIcon.icns

for s in 16 24 32 48 64 128 256; do
  sips -z $s $s "$master" --out "$work/ico_$s.png" >/dev/null
done
python3 - "$work" app.ico <<'PY'
import struct, sys
work, out = sys.argv[1:3]
sizes = [16, 24, 32, 48, 64, 128, 256]
pngs = [open(f"{work}/ico_{s}.png", "rb").read() for s in sizes]
offset = 6 + 16 * len(sizes)
head = struct.pack("<HHH", 0, 1, len(sizes))
body = b""
for s, data in zip(sizes, pngs):
    head += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
    body += data
open(out, "wb").write(head + body)
PY
echo "regenerated app.ico, AppIcon.icns, icon-256.png"
