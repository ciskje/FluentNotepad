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

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.9.1-win-x64.exe` (per-machine install in Program Files, optional file associations)
- **macOS**: [Releases](../../releases) page → `FluentNotepad.dmg` from a `macos-vX.Y.Z` release → drag `FluentNotepad.app` into Applications. First launch: right-click → Open (ad-hoc signature).

Or build it yourself: see `windows/AGENTS.md` (Build/run) and `macos/AGENTS.md` (Build/run).

## Changelog

Same version number = same features everywhere. Windows is at `2.9.1`, macOS at `2.7.0` (JSON/XML previews pending there).

- **2.9.1** (Windows) — Setup now offers optional `.json`/`.jsonc`/`.xml` file associations (unchecked by default).
- **2.9.0** (Windows) — XML tree preview: `.xml`/`.config` files open in a collapsible tree on the same schema (attributes as `@name` rows, XPath copy paths, XML comments shown, invalid-XML error with line/column, XML highlighting in edit mode). JSONC comments now shown in the JSON tree. Fixed a startup crash when opening tree-preview files (no RichEdit formatting inside the layout pass) and the Release publish missing the app `.pri`/`.xbf` (installed app died on launch). Per-machine install now targets the real Program Files on 64-bit.
- **2.8.0** — never published (local only).
- **2.7.0** — In-app keyboard-shortcuts reference (Help menu, both languages). macOS also: app icon back on the mounted DMG volume (plus custom icon on the `.dmg` file); build number shown in About.
Last 5 entries; full history in [Releases](../../releases) and the Done section
of [TODO.md](TODO.md) (archived at each release).

- **2.6.3** — Code cleanup only, no behavior change: shared Markdown pipeline/palette, native click-to-source renderer and clipboard ops on Windows, unified file-drop helper and precomputed highlight fonts on macOS, docs/config dedup.

- **2.6.2** — Unsaved tabs count as Markdown: preview toggle, live highlighting and `.md` save default before the first save; new tabs open directly in Preview.

## License

MIT — see [LICENSE](LICENSE).
