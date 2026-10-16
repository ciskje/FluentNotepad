# FluentNotepadMac

Native macOS port of [FluentNotepad](https://github.com/ciskje/FluentNotepad) (Windows 11 Notepad clone with Markdown preview), rewritten in SwiftUI.

![Markdown preview of BENCH.md (dark theme)](screenshot.png)

## Features

- Tabs with `•` for unsaved changes, `+` for a new tab
- Markdown preview (WKWebView + swift-markdown): tables, task lists, code, quotes, links, local/remote images — `.md` files open directly in Preview
- Parser-driven syntax highlighting in Edit mode, same VS tones as the preview
- Click-to-source: click a rendered block to jump back to its source line
- Formatting toolbar, find (⌘F), zoom, word wrap, file drag & drop (including onto the preview)
- ESC toggles Edit/Preview
- Multiple windows (File > New Window, ⇧⌘N), each with its own tabs
- Print of the rendered Markdown preview
- Status bar (line/column, characters, file type, zoom, CRLF/LF, encoding)
- Light/dark/system theme, interface in English and Italian, window geometry memory
- Standard macOS save-changes sheets, native Settings window (⌘,)

## Requirements

- macOS 14+, Apple Silicon
- To build: Swift 6 toolchain (Xcode or Command Line Tools) + `brew install librsvg` (used once to render the `.icns` from the vector icon)

## Download

Releases page → `FluentNotepad.dmg` → drag `FluentNotepad.app` into Applications. First launch: right-click the app → Open (ad-hoc signature, no paid Developer ID).

## Build

```bash
swift build -c release
./packaging/bundle.sh   # dist/FluentNotepad.app (no Xcode needed)
./packaging/dmg.sh      # dist/FluentNotepad.dmg with /Applications symlink
```

## Structure

- `Sources/FluentNotepad/` — the app: `App.swift` (scenes, menus, close/quit guards), `ContentView.swift` (tabs, toolbar, status bar), `EditorView.swift` (native NSTextView), `Highlight.swift` (syntax tree highlighting), `Markdown.swift` (Markdown → HTML), `PreviewView.swift` (WKWebView), `Store.swift` / `Doc.swift` (documents, panels, settings), `SettingsView.swift`, `Resources/` (IT/EN strings)
- `packaging/` — `Info.plist`, `AppIcon.svg` (vector trace of the Windows icon), `bundle.sh`, `dmg.sh`
- `BENCH.md` — sample file to try the preview

## Changelog

- **1.3.3** — ESC now toggles Edit/Preview in both directions (previously Edit → Preview only); handled at window level so it works with focus anywhere except text fields and IME composition.

- **1.3.2** — Dropped files open as tabs from every surface: the editor and the preview now claim file drops themselves instead of relying on event bubbling (which left drops over the body with nowhere to go).

- **1.3.1** — Files dropped onto the editor open as tabs (the editor no longer eats the drop and pastes the file name).

- **1.3.0** — Multiple windows (⇧⌘N), each with its own tabs; ESC returns from Edit to Preview; files dropped onto the preview open as tabs instead of navigating away.

- **1.2.2** — Standard save-changes sheets (tab close, window close, quit) instead of the custom alert.
- **1.2.1** — Edit highlighting is parser-driven (no more emphasis leaking across table cells); structural pipes, bold table headers.
- **1.2.0** — Click-to-source: click a rendered block to jump to its source line.
- **1.1.2** — Highlighting covers raw HTML too (tags, attributes, comments).
- **1.1.1** — Fixed highlighting not appearing (font resets wiped the colors).
- **1.1.0** — Markdown syntax highlighting in Edit mode.
- **1.0.2** — Full-bleed vector app icon, crisp at every size.
- **1.0.1** — Fixed the light/dark theme toggle.
- **1.0.0** — First native release (SwiftUI + WKWebView, macOS 14+).

## License

MIT — see [LICENSE](LICENSE).
