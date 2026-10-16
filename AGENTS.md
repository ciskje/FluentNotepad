# AGENTS.md — FluentNotepad (Windows + macOS)

One repo, two native builds of the same Windows 11 Notepad clone with Markdown preview:

- `windows/` — WinUI 3 / .NET 8, unpackaged (no MSIX).
- `macos/` — SwiftUI + WKWebView, Swift 6, macOS 14+, Apple Silicon.

Platform details (build, gotchas, paths) live in `windows/AGENTS.md` and
`macos/AGENTS.md`. This file holds only what is shared: intents, workflow,
versioning and releases. Build on each machine **only for its own platform**.

## Feature intents (parity checklist)

Both builds implement the same intents; platform-native details may differ
(Mica on Windows, save sheets on macOS):

- Drag-reorderable tabs, `+` for a new tab, dirty marker, multi-window
- Markdown preview (tables, task lists, code, quotes, links, local/remote images)
- `.md` files open directly in Preview; Edit/Preview toggle (+ ESC both ways)
- Parser-driven edit-mode highlighting, same VS tones as the preview
  (only real syntax nodes; never leaks across cells/spans)
- Formatting toolbar (heading, bold, italic, strikethrough, link, quote,
  bullet/numbered lists, code) operating on the remembered selection
- Find, zoom, word wrap, file drag & drop (including onto the preview)
- Print of the rendered Markdown preview in color
- Status bar (line/column, characters, file type, zoom, line ending, encoding)
- Light/dark/system theme, interface in English and Italian
- Click-to-source: click a rendered block to jump to its source line
  (macOS done; Windows pending)

## Change workflow

1. A change request updates the intents above first (this file).
2. Implement **only for the local platform**; the other platform implements
   the same intent later on its own machine.
3. Same rule for fixes: if the bug is in a shared intent, mirror the fix.

## Versioning (lockstep X.Y.Z)

Same version number on both platforms so changelogs stay comparable:
`X.Y.Z` = the same intent set. A platform publishes `X.Y.Z` when it has
implemented those intents; if the other lags, its release waits.

- Z = bugfix/tweak, Y = new feature/behavior, X = upheaval.
- Docs/refactor/build never change the version.
- Current: Windows `2.4.2`, macOS `1.3.3`; next shared version: `2.5.0`.
- Version locations: `<Version>` in `windows/source/FluentNotepad.WinUI.csproj`
  (+ `installer/FluentNotepad.iss`, setup name in `windows/README.md`);
  `CFBundleShortVersionString` in `macos/packaging/Info.plist`.

## Tags & releases

One channel per platform, tags always carry the platform prefix:

- Windows: tag `windows-vX.Y.Z` + `FluentNotepad-Setup-X.Y.Z-win-x64.exe`
  (per-machine install in Program Files, optional `.md` association).
- macOS: tag `macos-vX.Y.Z` + `FluentNotepad.dmg` (drag into Applications).
- Old prefix-less tags (`v2.x`, `v1.x`, `mac-v*`) stay as history.
- Every version bump is published (tag + release with artifact), otherwise
  Latest lags behind the code. Keep the **last 2 releases per platform**.
- Release notes **always in English**: standard install line + changelog
  entries since the previous release of that platform (same text as the
  Changelog in the READMEs, which are in English).

```bash
git tag windows-vX.Y.Z && git push origin windows-vX.Y.Z
gh release create windows-vX.Y.Z "windows/installer/dist/FluentNotepad-Setup-X.Y.Z-win-x64.exe" \
  --title "windows-vX.Y.Z" --latest --notes "<changelog>"
gh release create macos-vX.Y.Z "macos/dist/FluentNotepad.dmg" \
  --title "macos-vX.Y.Z" --latest --notes "<changelog>"
```

## "Genera l'applicazione" (build per l'ambiente corrente)

"Genera l'applicazione" significa sempre: compila **solo per la piattaforma
della macchina su cui gira l'agent**, mai per l'altra.

- Su Windows, da `windows/`: `dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64` poi `iscc installer\FluentNotepad.iss` → `installer\dist\FluentNotepad-Setup-X.Y.Z-win-x64.exe`.
- Su macOS, da `macos/` (su SSD, mai sul NAS): `swift build -c release`, `./packaging/bundle.sh`, `./packaging/dmg.sh` → `dist/FluentNotepad.dmg`.
- Non cross-compilare, non toccare la versione (salvo release), non pubblicare
  su GitHub se non richiesto esplicitamente.

## Source language

Source everything in English: comments, docs, sample file names.
Exception: UI strings (WinUI `.resw` it-IT/en-US, macOS `.lproj`), localized;
code-side strings go through the platform loader (`Str()` / `L()`).

## Structure

- `windows/` — `source/` (app), `installer/` (Inno Setup 6), `README.md`
  (product), `AGENTS.md` (platform notes), `start-debug.bat`.
- `macos/` — `Sources/` (app), `packaging/` (bundle/dmg scripts),
  `Package.swift`, `README.md` (product), `AGENTS.md` (platform notes).
- Root: `BENCH.md` (shared preview sample), `LICENSE` (MIT),
  `screenshot-*.png`.
