#!/bin/bash
# DMG with the app + symlink to /Applications (classic drag-to-install).
set -euo pipefail
cd "$(dirname "$0")/.."

APP="dist/FluentNotepad.app"
[ -d "$APP" ] || { echo "missing $APP — run packaging/bundle.sh first"; exit 1; }

STAGE="dist/stage"
rm -rf "$STAGE" dist/FluentNotepad.dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

echo "==> dmg"
hdiutil create -volname "FluentNotepad" -srcfolder "$STAGE" -ov -format UDZO \
  dist/FluentNotepad.dmg >/dev/null
rm -rf "$STAGE"
ls -la dist/FluentNotepad.dmg
