# TODO.md — change tracker

Every requested change is classified **first** by scope, then appended here
before implementing:

- `generic` — shared intent: implement on **every** platform (each on its own
  machine/build). The row is done only when all platforms have it.
- `windows` / `macos` / `linux` — platform-only: implement (and rebuild the
  app) only where relevant; other platforms ignore the row.
- Implement only for the current platform, update the row; Done rows are
  archived (deleted here) at each release — history lives in tags and
  release notes, not in this file.
- Generic intents are also mirrored in the parity checklist in `AGENTS.md`.

Legend per row: `win:[x]` / `mac:[x]` = shipped on that platform.

## Pending

- [ ] [generic] In-app keyboard-shortcuts reference (native Help on each platform) — win:[ ] mac:[x]

## Done

- [x] [macos] Bump CFBundleVersion (build number) at every recompile — mac:[x]  (convention in macos/AGENTS.md)

- [x] [macos] Custom Finder icon for the .dmg file (app icon via NSWorkspace) — mac:[x]


- [x] [generic] Unsaved tabs count as Markdown (preview toggle, highlighting, .md save default) — win:[x] mac:[x]

- [x] [macos] Publish 2.6.1 (undo/redo Doc-sync fix) — mac:[x]

- [x] [macos] Copy-paste broken (fixed: undo/redo bypassed textDidChange, Doc kept pre-undo text and refresh snapped back; coordinator now syncs via NSTextStorageDelegate) — mac:[x]
- [x] [generic] Click-to-source (click a rendered block to jump to its source line) — win:[x] mac:[x]

- [x] [windows] Double paste/cut/undo from Edit menu accelerators firing on top of native RichEditBox keys — win:[x] mac:[n/a]

- [x] [generic] Quick-save button in the toolbar (disabled unless the tab is dirty) — win:[x] mac:[x]

- [x] [macos] DMG volume icon (app icon on the mounted volume via .VolumeIcon.icns + SetFile) — mac:[x]
- [x] [macos] Align to shared version 2.5.0 (lockstep rule, see AGENTS.md) — mac:[x]

- [x] [generic] Parser-driven edit-mode highlighting, same VS tones as preview — win:[x] mac:[x]
- [x] [generic] ESC toggles Edit/Preview both ways (yields to find field and IME) — win:[x] mac:[x]
- [x] [generic] Files dropped onto the preview open as tabs — win:[x] mac:[x]
- [x] [generic] Multiple windows with own tabs — win:[x] mac:[x]
