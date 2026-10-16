#!/bin/bash
# DMG with the app + symlink to /Applications (classic drag-to-install),
# plus the app icon as the mounted-volume icon (.VolumeIcon.icns + SetFile).
set -euo pipefail
cd "$(dirname "$0")/.."

APP="dist/FluentNotepad.app"
[ -d "$APP" ] || { echo "missing $APP — run packaging/bundle.sh first"; exit 1; }
ICON="$APP/Contents/Resources/AppIcon.icns"
[ -f "$ICON" ] || { echo "missing $ICON — rebuild the .app"; exit 1; }

STAGE="dist/stage"
TMPDMG="dist/tmp-volicon.dmg"
rm -rf "$STAGE" "$TMPDMG" dist/FluentNotepad.dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

# Volume icon must be set on the mounted image (a direct UDZO has nowhere
# to run SetFile): stage read-write, attach, stamp, detach, compress.
echo "==> dmg (read-write staging)"
hdiutil create -volname "FluentNotepad" -srcfolder "$STAGE" -ov -format UDRW "$TMPDMG" >/dev/null
MNT="$(hdiutil attach -readwrite -noverify -noautoopen "$TMPDMG" | cut -f 3 | grep -E '/Volumes/' | head -1)"
[ -n "${MNT:-}" ] || { echo "mount failed"; exit 1; }
cleanup() { hdiutil detach "$MNT" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cp "$ICON" "$MNT/.VolumeIcon.icns"
if command -v SetFile >/dev/null; then
  SetFile -a C "$MNT/.VolumeIcon.icns"
else
  echo "warning: SetFile not found (Xcode CLT?) — volume keeps the generic icon"
fi
cleanup
trap - EXIT
echo "==> compress"
hdiutil convert "$TMPDMG" -format UDZO -o dist/FluentNotepad.dmg >/dev/null
rm -rf "$STAGE" "$TMPDMG"
ls -la dist/FluentNotepad.dmg
