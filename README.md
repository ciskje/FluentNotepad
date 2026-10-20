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

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.10.4-win-x64.exe` (per-machine install in Program Files, optional file associations)
- **macOS**: [Releases](../../releases) page → `FluentNotepad.dmg` from a `macos-vX.Y.Z` release → drag `FluentNotepad.app` into Applications. First launch: right-click → Open (ad-hoc signature).

Or build it yourself: see `windows/AGENTS.md` (Build/run) and `macos/AGENTS.md` (Build/run).

## Changelog

Same version number = same features everywhere. Windows is at `2.10.4`, macOS at `2.10.5` (same intents; macOS pauses 1 s instead of 0.5 s before the click-to-source caret blink).

- **2.10.5** (macOS) — Click-to-source pause before the caret-line blink increased from 0.5 s to 1 s.
- **2.10.4** (macOS) — Click-to-source jumps to Edit and blinks the caret line twice (0.5 s pause, 2x250 ms); About lists all supported formats; opening an already-open file selects its tab (full-path, case-insensitive match).
- **2.10.4** (Windows) — Click-to-source jumps to Edit and blinks the caret line twice (0.5 s pause, 2x250 ms) instead of highlighting the preview row; editor wrap + font size now persist across launches; status bar shows the detected file encoding (UTF-8 / UTF-8 with BOM / UTF-16 LE/BE).

- **2.10.3** (Windows) — Opening an already-open file selects its tab instead of duplicating it; click-to-source now blinks the row in tree/CSV previews before jumping to Edit; About dialog lists all supported formats.
- **2.10.2** (Windows) — Closing the last tab no longer leaves the previous editor on screen.
- **2.10.1** (Windows) — Setup offers optional associations for the new formats too (`.yaml`/`.yml`, `.toml`, `.ini`/`.cfg`/`.inf`/`.properties`, `.csv`/`.tsv`; all unchecked by default).
Last 5 entries; full history in [Releases](../../releases) and the Done section
of [TODO.md](TODO.md) (archived at each release).

## License

MIT — see [LICENSE](LICENSE).
