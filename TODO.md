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

- generic — Print reflects the current zoom: printing re-renders at the live zoom/font/wrap and waits for the page load before opening the print dialog (zoom-out then print used to print the previous size) — win:[x] mac:[ ]
  - win — root cause (stale HTML): `Navigate`/`NavigateToString` return before load, so `Print_Click` printed whatever HTML was settled (stale right after a zoom step, from Edit, or during the 400 ms debounce). Fix in `MainWindow.xaml.cs`: `Print_Click` stops the debounce timer, ensures the preview, re-renders via `TryRenderAsync` and prints only after `NavigationCompleted` (`WaitForNavigationAsync`, 5 s timeout); stale-tab guard included. AGENTS.md print intent now says "at the current zoom"
  - win — root cause (zoom-from-preview invisible): with focus in the page, Ctrl+Plus/Minus/wheel/pinch never reach the XAML accelerators — Chromium applies its native view-only zoom, so the HTML (and the print) stayed large. Fix: page-level interception (wheel + keydown listeners in the Markdown doc-start script, `PreviewTree.Js`, `CsvPreviewHtml.Js`) posting `{op:zoom,dir}` → `Preview_WebMessage` → `SetZoom`; `preventDefault` blocks the native zoom (verified in Chromium: synthetic ctrl+wheel/ctrl+Plus/Minus report `defaultPrevented`, Ctrl+0 untouched)
  - mac — mirror in the `App.swift` print path + `PreviewView`/`Store.zoom` once the win path is proven

- generic — sample `esempio/config.jsonc` adapted from `opencode.jsonc` (sanitized values, English comments, `//` + `/* */` + trailing commas) so the JSONC tree preview has a commented demo file — win:[x] mac:[x]
- generic — "A capo automatico" drives the preview too: off = no line breaking in the rendered HTML (horizontal scroll), on = wrap; print always wraps — win:[x] mac:[ ]
  - win — code written, **compiled Debug 0 errors**, uncommitted. Files touched: `PreviewTree.cs` (`WrapCss`), `MarkdownHtml.cs`, `CsvPreviewHtml.cs` (`GridCss`), `Json/Xml/Yaml/Toml/IniPreviewHtml.cs` (new `wrap` param), `MainWindow.xaml.cs` (`WrapOn()`, `Wrap_Changed` re-renders, `TryRenderAsync`)
  - win — smoke tested live 2026-10-21: JSON tree + CSV grid via `tree-preview-<pid>.html` markers (wrap-on `.row{white-space:normal}`+`min-width:0` vs wrap-off `body{white-space:nowrap}`+`.cm{white-space:pre}`; CSV `.tbl` `pre-wrap`/`max-width:100%` vs `pre`/`max-width:none`), Markdown via code path + app launch (responding, full UIA tree; screenshots inconclusive = overlay promotion per `windows/AGENTS.md`). No visual regression in HTML; tree-row wrap-on change remains the only risk
  - win — release `2.10.10` (publish + `iscc installer/FluentNotepad.iss` + tag/release) when asked; version already bumped in csproj/iss/README
  - mac — mirror the intent: `macos/Sources/FluentNotepad/EditorView.swift`, `PreviewTree.swift`, `Markdown.swift`
- generic — Word-wrap OFF has no effect in preview (reported 2026-10-22): off must fully disable line breaking in every preview (Markdown + JSON/XML/YAML/TOML/INI tree + CSV grid) with horizontal scroll; on = wrap; print always wraps — win:[x] mac:[ ]
  - win — root causes: directly-applied `white-space` (tree `.msg` base `pre-wrap`, Markdown elements) beat the inherited `body{nowrap}`; print left nowrap spans/cells (paper clipped). Fix hardened `MarkdownHtml` (element nowrap list + `pre` guard), `PreviewTree.WrapCss` (span list), `CsvPreviewHtml` print reset; re-render paths already live (`Wrap_Changed`, tab switch, Edit→Preview). Debug 0 errors, harness 19/19, live smoke: UIA-invoked "A capo automatico" re-rendered `tree-preview-<pid>.html` with nowrap markers + persisted `wrap:false`, screenshot shows the long JSON string scrolling horizontally
  - win — follow-up: error-page chrome (Causa etc.) spilled out of its cards in wrap-off → chrome now always wraps inside cards (`white-space:normal`), only `pre` source blocks scroll. Verified live on screenshot with wrap off
  - mac — same fix in `Markdown.swift`, `PreviewTree.swift`, `CsvGrid.swift` (+ tree builders) once the win path is proven
- generic — JSONC comments/trailing commas must not error: `.jsonc` parses lenient (comments + trailing commas), plain `.json` stays strict — win:[x] mac:[ ]
  - win — root cause: `TryRenderAsync` never passed `jsonc:true`, so `JsonPreviewHtml.Build` always ran strict (`CommentHandling.Disallow`) and every `//`/`/* */` in `.jsonc` fell into the invalid-JSON error page. Fix passes `doc.Path ends with .jsonc` (case-insensitive) as the `jsonc` flag in `MainWindow.xaml.cs`
  - mac — `JsonTree.parse` is always lenient (comments + trailing commas even for `.json`); mirror the strict/lenient split there if the shared intent (strict `.json`, lenient `.jsonc`, `ErrFixJsonc` wording) is to hold on mac too

## Done

(archived at each release — history in tags and release notes)
