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

- XML tree preview (`generic`, win:[x] mac:[ ]): `.xml`/`.config` with the same
  tree schema (attributes as `@name` rows, XPath copy paths, XML comments
  shown, invalid-XML error page, XML edit-mode highlight, Open/Save panel
  types). Windows done; macOS (WKWebView port) pending.
- YAML tree preview (`generic`, win:[x] mac:[ ]): `.yaml`/`.yml` auto-open as a
  collapsible tree (indentation-based subset: mappings, sequences, scalars,
  comments; anchors/aliases/tags stripped, not resolved), `$.` copy paths,
  YAML edit-mode highlight. Windows done; macOS pending.
- TOML tree preview (`generic`, win:[x] mac:[ ]): `.toml` auto-open as a
  collapsible tree (tables, arrays of tables, dotted keys expanded),
  `$.` copy paths, TOML edit-mode highlight. Windows done; macOS pending.
- INI tree preview (`generic`, win:[x] mac:[ ]): `.ini`/`.cfg`/`.inf`/
  `.properties`/`.env*` as a collapsible tree (sections, `=`/`:` pairs,
  `;`/`#` comments, `\` continuations), `$.` copy paths, INI edit-mode
  highlight. Windows done; macOS pending.
- CSV grid preview (`generic`, win:[x] mac:[ ]): `.csv`/`.tsv` as a grid
  (sniffed delimiter, quoted/multiline fields, header row, row cap with
  banner, search, copy raw line, click-to-line). No edit-mode highlight
  (plain text). Windows done; macOS pending.
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
- Caret-line flash on entering Edit (`generic`, win:[x] mac:[ ]): click-to-source
  blinks the rendered row twice in the preview, then switches to Edit
  (selection-only flash for other entries, aborting on edit).

## Done

(archived at each release — history in tags and release notes)
