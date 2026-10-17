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
hdiutil create -volname "FluentNotepad" -srcfolder "$STAGE" -ov -format UDZO dist/FluentNotepad.dmg >/dev/null
# Custom icon for the .dmg FILE (app icon; cosmetic: never fail the build).
ICON="$APP/Contents/Resources/AppIcon.icns"
if [ -f "$ICON" ]; then
  swift -e 'import AppKit; guard CommandLine.arguments.count == 3, let img = NSImage(contentsOfFile: CommandLine.arguments[1]) else { exit(1) }; NSWorkspace.shared.setIcon(img, forFile: CommandLine.arguments[2], options: [])' "$ICON" dist/FluentNotepad.dmg >/dev/null || echo "warning: dmg file icon skipped"
fi
rm -rf "$STAGE"
ls -la dist/FluentNotepad.dmg
