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

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.10.1-win-x64.exe` (per-machine install in Program Files, optional file associations)
- **macOS**: [Releases](../../releases) page → `FluentNotepad.dmg` from a `macos-vX.Y.Z` release → drag `FluentNotepad.app` into Applications. First launch: right-click → Open (ad-hoc signature).

Or build it yourself: see `windows/AGENTS.md` (Build/run) and `macos/AGENTS.md` (Build/run).

## Changelog

Same version number = same features everywhere. Windows is at `2.10.1`, macOS at `2.8.0` (XML/YAML/TOML/INI/CSV previews pending there).

- **2.10.1** (Windows) — Setup offers optional associations for the new formats too (`.yaml`/`.yml`, `.toml`, `.ini`/`.cfg`/`.inf`/`.properties`, `.csv`/`.tsv`; all unchecked by default).
- **2.10.0** (Windows) — YAML, TOML and INI tree previews plus CSV grid preview (search, copy, click-to-line; edit-mode highlight for the tree formats).
- **2.8.0** (macOS) — JSON tree preview: `.json`/`.jsonc` files open in a collapsible tree on the same schema (expand/collapse all, search with match count, copy path/value, per-tab open state, invalid-JSON error with line/column, print expanded, JSON highlighting in edit mode, Open/Save panel types).
- **2.9.6** (Windows) — Click-to-source blinks the rendered row twice in the preview first, then switches to Edit.
- **2.9.0** (Windows) — XML tree preview: `.xml`/`.config` files open in a collapsible tree on the same schema (attributes as `@name` rows, XPath copy paths, XML comments shown, invalid-XML error with line/column, XML highlighting in edit mode). JSONC comments now shown in the JSON tree. Fixed a startup crash when opening tree-preview files (no RichEdit formatting inside the layout pass) and the Release publish missing the app `.pri`/`.xbf` (installed app died on launch). Per-machine install now targets the real Program Files on 64-bit.
- **2.8.0** — never published (local only).
Last 5 entries; full history in [Releases](../../releases) and the Done section
of [TODO.md](TODO.md) (archived at each release).

## License

MIT — see [LICENSE](LICENSE).
