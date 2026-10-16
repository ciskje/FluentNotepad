# FluentNotepad

A Windows 11 Notepad clone with built-in Markdown preview — two native builds
from one repo, same features on both platforms.

![Markdown preview of BENCH.md, Windows dark theme](windows/screenshot-dark.png)

## Features (both platforms)

- Drag-reorderable **tabs**, `+` for a new tab, dirty marker, multiple **windows**
- **Markdown preview**: tables, task lists, code, quotes, links, local/remote images — `.md` files open directly in Preview
- **Visual Studio-style colors** for headings, links, code, bold/italic, quotes and tables (light and dark themes), in Preview and in edit mode (live, parser-driven)
- **Formatting toolbar** (heading, bold, italic, strikethrough, link, quotes, lists, code)
- Edit/Preview toggle + **ESC** both ways, click-to-source (macOS; Windows pending)
- Find, zoom, word wrap, file drag & drop (including onto the preview)
- Print of the rendered Markdown preview in color
- Status bar like the original (line/column, characters, file type, zoom, line ending, encoding)
- Interface in **English and Italian**

## Requirements

- Windows 10 1809+ (Mica on Windows 11) + WebView2 evergreen (preinstalled on Windows 11). To build: .NET 8 SDK.
- macOS 14+, Apple Silicon. To build: Swift 6 toolchain.

## Download

The repo contains sources only (builds and setups are **not** versioned). Same
version number = same features on both platforms (see Changelogs below).

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.4.2-win-x64.exe` (per-machine install in Program Files, optional `.md` association)
- **macOS**: [Releases](../../releases) page → `FluentNotepad.dmg` from a `macos-vX.Y.Z` release → drag `FluentNotepad.app` into Applications. First launch: right-click → Open (ad-hoc signature).

Or build it yourself:

```bash
# Windows (from windows/)
dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
iscc installer\FluentNotepad.iss   # installer\dist\ (requires Inno Setup 6)
```

```bash
# macOS (from macos/, on SSD — never build on the NAS)
swift build -c release
./packaging/bundle.sh   # dist/FluentNotepad.app (no Xcode needed)
./packaging/dmg.sh      # dist/FluentNotepad.dmg with /Applications symlink
```

## Structure

- `windows/` — WinUI 3 app (`source/`), installer, product `README.md`, `AGENTS.md`
- `macos/` — SwiftUI app (`Sources/`, `Package.swift`), packaging, product `README.md`, `AGENTS.md`
- `BENCH.md` — shared sample file; `AGENTS.md` — shared intents, versioning, releases

## Changelog — Windows

Next shared version: `2.5.0` (same features as macOS `2.5.0` when it lands).

- **2.4.2** — No more background flash when hovering the editor (template hover/focus brushes neutralized).
- **2.4.1** — Fixed edit-mode highlighting not rendering on file open (repaint on editor load) and black bars behind text (opaque editor background instead of transparent).
- **2.4.0** — Multiple windows (File → New window, Ctrl+Shift+N, cascaded); ESC toggles Edit/Preview on Markdown docs; files dropped onto the preview open as tabs instead of navigating away (all like FluentNotepadMac).
- **2.3.0** — Syntax highlighting in edit mode for Markdown files (same VS-style colors as the preview, like FluentNotepadMac), applied live while typing.
- Older: see `windows/README.md`.

## Changelog — macOS

Next shared version: `2.5.0` (same features as Windows `2.5.0`).

- **1.3.3** — ESC now toggles Edit/Preview in both directions (previously Edit → Preview only); handled at window level so it works with focus anywhere except text fields and IME composition.
- **1.3.2** — Dropped files open as tabs from every surface: the editor and the preview now claim file drops themselves instead of relying on event bubbling (which left drops over the body with nowhere to go).
- **1.3.1** — Files dropped onto the editor open as tabs (the editor no longer eats the drop and pastes the file name).
- **1.3.0** — Multiple windows (⇧⌘N), each with its own tabs; ESC returns from Edit to Preview; files dropped onto the preview open as tabs instead of navigating away.
- Older: see `macos/README.md`.

## License

MIT — see [LICENSE](LICENSE).
