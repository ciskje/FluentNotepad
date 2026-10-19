# FluentNotepad

A native Notepad-style editor with built-in Markdown preview — two builds
from one repo, same features on both platforms.

![Markdown preview, Windows dark theme](windows/screenshot-dark.png)
![Markdown preview, macOS dark theme](macos/screenshot.png)

## Features (both platforms)

Same intents on both platforms — see the parity checklist in [AGENTS.md](AGENTS.md).

## Requirements

- Windows 10 1809+ (Mica on Windows 11) + WebView2 evergreen (preinstalled on Windows 11). To build: .NET 8 SDK.
- macOS 14+, Apple Silicon. To build: Swift 6 toolchain.

## Download

The repo contains sources only (builds and setups are **not** versioned). Same
version number = same features on both platforms (see Changelogs below).

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.10.2-win-x64.exe` (per-machine install in Program Files, optional file associations)
- **macOS**: [Releases](../../releases) page → `FluentNotepad.dmg` from a `macos-vX.Y.Z` release → drag `FluentNotepad.app` into Applications. First launch: right-click → Open (ad-hoc signature).

Or build it yourself: see `windows/AGENTS.md` (Build/run) and `macos/AGENTS.md` (Build/run).

## Changelog

Same version number = same features everywhere. Windows is at `2.10.2`, macOS at `2.10.1` (fully aligned otherwise).

- **2.10.2** (Windows) — Closing the last tab no longer leaves the previous editor on screen.
- **2.10.1** (macOS) — File associations for the new formats too (exported UTIs: `.yaml`/`.yml`, `.toml`, `.ini`/`.cfg`/`.inf`/`.properties`, `.jsonc`, `.config`; open by double-click); YAML, TOML and INI tree previews plus CSV grid preview (search, copy, click-to-line; edit-mode highlight for the tree formats); XML tree preview (attributes as `@name` rows, XPath copy paths, XML comments, invalid-XML error, XML highlight, Open/Save panel types); ⌘S saves the active tab; drag-reorderable tabs; print notice for non-preview tabs (expand-before-print for trees); footnotes/definition lists in Markdown preview; caret-line flash entering Edit; click-to-source blinks the row in preview before jumping.
- **2.10.1** (Windows) — Setup offers optional associations for the new formats too (`.yaml`/`.yml`, `.toml`, `.ini`/`.cfg`/`.inf`/`.properties`, `.csv`/`.tsv`; all unchecked by default).
- **2.10.0** (Windows) — YAML, TOML and INI tree previews plus CSV grid preview (search, copy, click-to-line; edit-mode highlight for the tree formats).
- **2.9.6** (Windows) — Click-to-source blinks the rendered row twice in the preview first, then switches to Edit.
Last 5 entries; full history in [Releases](../../releases) and the Done section
of [TODO.md](TODO.md) (archived at each release).

## License

MIT — see [LICENSE](LICENSE).
