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
  highlight). Windows done; macOS (WKWebView port) pending (incl. Open/Save
  panel types for .json/.jsonc).
- XML tree preview (`generic`, win:[x] mac:[ ]): `.xml`/`.config` with the same
  tree schema (attributes as `@name` rows, XPath copy paths, XML comments
  shown, invalid-XML error page, XML edit-mode highlight, Open/Save panel
  types). Windows done; macOS (WKWebView port) pending.
- Save via keyboard on macOS (`generic`, win:[x] mac:[ ]): ⌘S saves the active
  tab, plus cheatsheet row (like Windows Ctrl+S).
- Tab drag-reorder on macOS (`generic`, win:[x] mac:[ ]): checklist says
  drag-reorderable (Windows `CanReorderTabs`, mac TabStrip is a static HStack).
- Open-file dedup on Windows (`generic`, win:[ ] mac:[x]): opening an
  already-open file selects its tab instead of duplicating it (macOS does).
- Print notice on macOS (`generic`, win:[x] mac:[ ]): printing a non-Markdown
  tab shows the equivalent of PrintOnlyPreview instead of a silent no-op.
- Footnotes/definition lists in macOS preview (`generic`, win:[x] mac:[ ]):
  Markdig advanced renders them on Windows; swift-markdown has no equivalent.
- Stale editor with zero tabs (`windows`, win:[x]): closing the last tab leaves
  the last editor hosted; clear EditorHost when no tab is active.
- Caret-line flash on entering Edit (`generic`, win:[x] mac:[ ]): two blinks
  of the caret line when switching to Edit mode (selection-only, no undo or
  formatting touched); macOS port with native flash.

## Done

(archived at each release — history in tags and release notes)
