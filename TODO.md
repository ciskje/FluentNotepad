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

- Single click-to-source blink on Windows (`generic`, win:[ ] mac:[x]): macOS 2.10.7 blinks the jumped-to line once (0.5 s pause, 1x250 ms); Windows still blinks twice. Mirror in `windows/source/MainWindow.xaml.cs`: `JumpToLine` (~line 637) needs one blink instead of two (e.g. a `blinks` parameter on `FlashCaretLine`, line 778, default 2, click path passes 1).

(nothing else pending — history lives in tags and release notes)

## Done

(archived at each release — history in tags and release notes)
