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

- **Windows**: [Releases](../../releases) page → `FluentNotepad-Setup-2.10.9-win-x64.exe` (per-machine install in Program Files, optional file associations)
- **macOS**: [Releases](../../releases) page → `FluentNotepad.dmg` from a `macos-vX.Y.Z` release → drag `FluentNotepad.app` into Applications. First launch: right-click → Open (ad-hoc signature).

Or build it yourself: see `windows/AGENTS.md` (Build/run) and `macos/AGENTS.md` (Build/run).

## Changelog

Windows is at `2.10.9`, macOS at `2.10.7` (2.10.8–2.10.9 are Windows-only; same intents).

- **2.10.9** (Windows) — Fixed the preview failing with "unavailable (WebView2)" after a failed engine init: the view is re-shown before initializing WebView2, the engine state is null-checked afterwards, print no longer races the render, and switching tabs re-renders the preview.
- **2.10.8** (Windows) — Edit/Preview toolbar buttons are now icon-only: the active mode is accent blue, the inactive one is neutral like the other toolbar icons (no borders).
- **2.10.7** (Windows) — Click-to-source jumps to Edit and blinks the jumped-to line once (0.5 s pause, 1x250 ms); plain Edit entries keep the immediate 2x140 ms blink.
- **2.10.7** (macOS) — Click-to-source pause back to 0.5 s with a single 250 ms blink (plain Edit entries keep 2x140 ms).
- **2.10.6** (macOS) — Click-to-source flash drives the live editor synchronously (timers bracket real selection changes; the target line is computed up front, never from a stale caret); fixed the click blink length (was 250 ns, now 250 ms).
Last 5 entries; full history in [Releases](../../releases) and the Done section
of [TODO.md](TODO.md) (archived at each release).

## License

MIT — see [LICENSE](LICENSE).
