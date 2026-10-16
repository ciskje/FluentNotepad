#!/bin/bash
# Builds FluentNotepad.app (no Xcode needed: swift build + manual bundle).
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> swift build (release)"
swift build -c release 2>&1 | tail -2

BIN=".build/release/FluentNotepad"
APP="dist/FluentNotepad.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/FluentNotepad"
cp packaging/Info.plist "$APP/Contents/Info.plist"
cp -R Sources/FluentNotepad/Resources/en.lproj Sources/FluentNotepad/Resources/it.lproj \
  "$APP/Contents/Resources/"

# Icon: vector trace of the Windows AppIcon.ico (packaging/AppIcon.svg, 14KB).
# Rendered with rsvg-convert (brew install librsvg): Quick Look's qlmanage
# renders SVGs shrunken and off-center, so it must NOT be used here.
# (The .ico itself only contains up to 256px; the SVG was traced once with
#  vtracer --mode spline; do NOT regenerate from raster on every build.)
SRC_SVG="packaging/AppIcon.svg"
if [ -f "$SRC_SVG" ]; then
  command -v rsvg-convert >/dev/null || { echo "missing rsvg-convert (brew install librsvg)"; exit 1; }
  echo "==> icon"
  rm -rf /tmp/FN.iconset && mkdir -p /tmp/FN.iconset
  rsvg-convert -w 1024 -h 1024 "$SRC_SVG" -o /tmp/FN.iconset/base1024.png
  for s in 16 32 128 256 512; do
    sips -z $s $s /tmp/FN.iconset/base1024.png --out /tmp/FN.iconset/icon_${s}x${s}.png >/dev/null
    sips -z $((s*2)) $((s*2)) /tmp/FN.iconset/base1024.png --out /tmp/FN.iconset/icon_${s}x${s}@2x.png >/dev/null
  done
  rm /tmp/FN.iconset/base1024.png
  iconutil -c icns /tmp/FN.iconset -o "$APP/Contents/Resources/AppIcon.icns"
  rm -rf /tmp/FN.iconset
fi

codesign --force --deep --sign - "$APP" 2>/dev/null || true
echo "==> $APP"
ls -la dist/ | grep -E "app|dmg"
