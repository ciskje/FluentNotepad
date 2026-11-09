# Changelog

Full history, newest first. The [README](README.md) keeps only the last 5
entries; release artifacts live on the [Releases](../../releases) page.

- **2.10.13** (Windows) — The print now follows the preview zoom: zooming from the preview (Ctrl+Plus/Minus, Ctrl+wheel, pinch) drives the app zoom instead of the view-only browser zoom, and printing re-renders at the current size before opening the dialog.
- **2.10.12** (Windows) — `.jsonc` files with comments or trailing commas now show the tree preview instead of an invalid-JSON error; plain `.json` stays strict.
- **2.10.11** (Windows) — Word-wrap off now fully disables wrapping in every preview (Markdown, tree, grid) with horizontal scroll, and the View menu shows its checkmark; Edit/Preview icons are 2x in a rounded group; toolbar buttons are square with centered icons (disabled Save stays flat); invalid files show a rich error page (cause, position, fixes, actions); sample docs moved to `esempio/` with school data.
- **2.10.10** (Windows) — Word wrap now applies to the preview as well: with "A capo automatico" off, paragraphs, tables and grid/tree values no longer break at the window edge (the preview scrolls horizontally); printing still wraps.
- **2.10.9** (Windows) — Fix WebView2 preview init race.
- **2.10.8** (Windows) — Edit/Preview toolbar buttons are now icon-only: the active mode is accent blue, the inactive one is neutral like the other toolbar icons (no borders).
- **2.10.7** (Windows) — Click-to-source jumps to Edit and blinks the jumped-to line once (0.5 s pause, 1x250 ms); plain Edit entries keep the immediate 2x140 ms blink.
- **2.10.7** (macOS) — Click-to-source pause back to 0.5 s with a single 250 ms blink (plain Edit entries keep 2x140 ms).
- **2.10.6** (macOS) — Click flash drives the live editor, fixed click blink length.
- **2.10.5** (macOS) — Click-to-source pause before caret blink 0.5 s -> 1 s.
- **2.10.4** (macOS) — Click flash in Edit, open-file dedup, About formats.
- **2.10.3** (Windows) — Open-file dedup, tree/CSV click blink, About formats.
- **2.10.2** (Windows) — Restore MdToggle visibility update dropped from dispatch (toggle never appeared).
- **2.10.1** (macOS) — XML/YAML/TOML/INI tree + CSV grid previews (WKWebView ports); Cmd+S, tab reorder, print notice, footnotes/deflists, caret flash, click blink, file associations.
- **2.8.0** (macOS) — JSON tree preview (WKWebView port of Windows 2.8.0).
- **2.7.0** (macOS) — Help menu, DMG icons, build 3.
- **2.7.0** (Windows) — Line endings eol=lf repo-wide (silences autocrlf warnings definitively).
- **2.6.3** (Windows, macOS) — Code cleanup, no behavior change (shared pipeline/palette, native renderer/clipboard, unified drops).
