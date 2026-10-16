# FluentNotepad

A native Notepad-style editor with built-in Markdown preview — two builds
from one repo, same features on both platforms.

![Markdown preview of BENCH.md, Windows dark theme](windows/screenshot-dark.png)
![Markdown preview of BENCH.md, macOS dark theme](macos/screenshot.png)

## Features (both platforms)

- Drag-reorderable **tabs**, `+` for a new tab, dirty marker, multiple **windows**
- **Markdown preview**: tables, task lists, code, quotes, links, local/remote images — `.md` files open directly in Preview
- **Visual Studio-style colors** for headings, links, code, bold/italic, quotes and tables (light and dark themes), in Preview and in edit mode (live, parser-driven)
- **Formatting toolbar** (heading, bold, italic, strikethrough, link, quotes, lists, code)
- Edit/Preview toggle + **ESC** both ways, click-to-source from the rendered preview
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

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.6.1-win-x64.exe` (per-machine install in Program Files, optional `.md` association)
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

## Changelog

Same version number = same features everywhere. macOS is at `2.6.2`, Windows at `2.6.1` (unsaved-tabs-as-Markdown pending there).

- **2.6.2** — Unsaved tabs count as Markdown: preview toggle, live highlighting and `.md` save default before the first save; new tabs open directly in Preview.

- **2.6.1** — Fixed double paste/cut/undo (Edit menu accelerators fired on top of native RichEditBox keys); typing+highlight is a single undo unit again. macOS: undo/redo now syncs back to the document (the view no longer snaps back on the next refresh).

- **2.6.0** — Click-to-source: click a rendered block to jump back to its source line.
- **2.4.2** — No more background flash when hovering the editor (template hover/focus brushes neutralized).
- **2.4.1** — Fixed edit-mode highlighting not rendering on file open (repaint on editor load) and black bars behind text (opaque editor background instead of transparent).
- **2.4.0** — Multiple windows (New window, cascaded); ESC toggles Edit/Preview on Markdown docs; files dropped onto the preview open as tabs instead of navigating away.
- **2.3.0** — Syntax highlighting in edit mode (same VS-style colors as the preview), applied live while typing.
- **2.2.2** — Fixed Markdown tables with `$` in cells (single-dollar math merged cells across `|`); dollars now stay literal.
- **2.2.1** — Fixed printing from dark mode (page no longer gets a dark frame) and the print header now shows the file name instead of `about:blank`.
- **2.2.0** — File → Print: prints the rendered Markdown preview in color via browser print dialog, always with the light theme on paper; plain text shows an info message.
- **2.1.1** — Fixed spurious `Ctrl++` tooltip over the editor (accelerator display hidden, shortcuts unchanged); fixed window geometry not being restored (settings JSON casing mismatch).
- **2.1.0** — Window remembers position, size and maximized state; renamed `avvia-debug.bat` → `start-debug.bat`.
- **1.3.3** — ESC now toggles Edit/Preview in both directions (previously Edit → Preview only); handled at window level so it works with focus anywhere except text fields and IME composition.
- **1.3.2** — Dropped files open as tabs from every surface: the editor and the preview now claim file drops themselves instead of relying on event bubbling (which left drops over the body with nowhere to go).
- **1.3.1** — Files dropped onto the editor open as tabs (the editor no longer eats the drop and pastes the file name).
- **1.3.0** — Multiple windows, each with its own tabs; ESC returns from Edit to Preview; files dropped onto the preview open as tabs instead of navigating away.
- **1.2.2** — Standard save-changes sheets (tab close, window close, quit) instead of the custom alert.
- **1.2.1** — Edit highlighting is parser-driven (no more emphasis leaking across table cells); structural pipes, bold table headers.
- **1.2.0** — Click-to-source: click a rendered block to jump to its source line.
- **1.1.2** — Highlighting covers raw HTML too (tags, attributes, comments).
- **1.1.1** — Fixed highlighting not appearing (font resets wiped the colors).
- **1.1.0** — Markdown syntax highlighting in Edit mode.
- **1.0.2** — Full-bleed vector app icon, crisp at every size.
- **1.0.1** — Fixed the light/dark theme toggle.
- **1.0.0** — First native release.

## License

MIT — see [LICENSE](LICENSE).
