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

- update about (`generic`, win:[x] mac:[ ]): new .ext are supported
- flash line in Edit mode on preview click (`generic`, win:[x] mac:[ ]): jump to Edit with caret on the source line, wait 0.5 s, then blink the caret line twice at 250 ms (selection-only, no undo/format side effects); abort on any edit, tab switch or back-to-preview; other Edit entries keep the immediate 2x140 ms blink

## Done

(archived at each release — history in tags and release notes)
