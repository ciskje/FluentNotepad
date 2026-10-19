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

- JSON tree preview (`generic`, win:[x] mac:[ ]): `.json`/`.jsonc` auto-open
  in a collapsible tree preview (expand/collapse all, search, copy path/value,
  per-tab open state, invalid-JSON error page, print expanded, JSON edit-mode
  highlight). Windows done; macOS (WKWebView port) pending.

## Done

(archived at each release — history in tags and release notes)
