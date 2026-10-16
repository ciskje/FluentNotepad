# AGENTS.md — FluentNotepadMac

Native macOS build of FluentNotepad (SwiftUI + WKWebView, Swift 6, macOS 14+, Apple Silicon).
No Xcode project: SPM + manual `.app` bundle. Sibling: `windows/` (WinUI 3). Shared intents and release rules in root `AGENTS.md`.

## Build / run (SSD, never NAS)

The NAS has slow mechanical disks: NEVER `swift build` here. Copy sources to SSD
temp, build there, copy back only `dist/` artifacts, then delete temp.
`.build/`, `dist/`, `._*` are git-ignored.

```bash
S=$(mktemp -d) && cp -R Package.swift Sources packaging $S/ && mkdir $S/dist && cd $S
swift build -c release
./packaging/bundle.sh   # dist/FluentNotepad.app (binary, Info.plist, .lproj, icon)
./packaging/dmg.sh      # dist/FluentNotepad.dmg with /Applications symlink
```

Headless self-tests: TEMP hook in the SSD copy's `AppDelegate` (`FN_SELFTEST` env,
`NSApp.terminate` at the end, `fputs` to stderr). Never ship hooks — verify the
release binary with `strings … | grep -c FN_SELFTEST` (= 0) and the 3 launches
(no-args / `open -a` with file / raw CLI binary with file).

## Versioning / releases

Shared lockstep versioning, see root `AGENTS.md`: same X.Y.Z on both platforms
(same intent set); tags `macos-vX.Y.Z`; `README.md` Changelog mirrors it.
Release + publish: `gh release create macos-vX.Y.Z dist/FluentNotepad.dmg`,
commit + push `main`. `CFBundleShortVersionString` lives in `packaging/Info.plist`.

## Structure (Sources/FluentNotepad/)

- `App.swift` — scenes, menus (frontmost routing), `WindowRoot` (one Store per
  window), raw-CLI fallback window, quit chain, ESC monitor, notifications.
- `Store.swift` / `Doc.swift` — per-window tabs/docs; `WindowStores` registry
  (frontmost routing); `PendingFiles` queue (CLI/openFile).
- `ContentView.swift` — tabs, toolbar, find, status bar, preview toggle, drop.
- `EditorView.swift` — `EditorTextView` (NSTextView: file drops open tabs,
  neutral typing attributes).
- `Highlight.swift` — parser-driven highlighting (swift-markdown source ranges);
  regex only inside HTML nodes. Non-markdown and huge docs stay plain.
- `Markdown.swift` — Markdown → HTML (+ `data-src-line` for click-to-source).
- `PreviewView.swift` — `PreviewWebView` (WKWebView subclass: claims file drops,
  click-to-source JS bridge) + `WebRef` (print/title).
- `SettingsView.swift`, `Strings.swift` (`L()`), `Resources/<lang>.lproj`
  (copied into the bundle by `bundle.sh`, NOT via SPM resources).

## Gotchas learned

- SwiftUI withholds the default window on raw-CLI launches with files → AppKit
  fallback window. CLI args + `openFile` + `onOpenURL` can all fire for one file:
  `Store.open` dedups by path.
- Setting `NSTextView.font` wipes highlight attributes: set only on size change
  (and invalidate the highlight key).
- `qlmanage` renders SVGs shrunken and off-center → icon via `rsvg-convert`
  (`brew install librsvg`); `AppIcon.svg` is a vtracer trace of `AppIcon.ico`.
- WKWebView swallows drops over the preview, NSTextView inserts file names:
  both subclasses claim file drops directly (never rely on bubbling up).
- ESC toggle: one window-level local monitor (key window only). It yields to
  `NSTextField` (find bar) and IME composition; per-view interception
  double-fires — don't add it back.
- `CGWindowListCreateImage` is gone in the macOS 26 SDK → window list +
  `screencapture -l<id>` for screenshots.
- `osascript` needs Accessibility rights (denied here) — don't rely on it.
- Ad-hoc signature: first launch needs right-click → Open.
