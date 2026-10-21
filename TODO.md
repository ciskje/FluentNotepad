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

- generic — "A capo automatico" drives the preview too: off = no line breaking in the rendered HTML (horizontal scroll), on = wrap; print always wraps — win:[x] mac:[ ]
  - win — code written, **compiled Debug 0 errors**, uncommitted. Files touched: `PreviewTree.cs` (`WrapCss`), `MarkdownHtml.cs`, `CsvPreviewHtml.cs` (`GridCss`), `Json/Xml/Yaml/Toml/IniPreviewHtml.cs` (new `wrap` param), `MainWindow.xaml.cs` (`WrapOn()`, `Wrap_Changed` re-renders, `TryRenderAsync`)
  - win — smoke tested live 2026-10-21: JSON tree + CSV grid via `tree-preview-<pid>.html` markers (wrap-on `.row{white-space:normal}`+`min-width:0` vs wrap-off `body{white-space:nowrap}`+`.cm{white-space:pre}`; CSV `.tbl` `pre-wrap`/`max-width:100%` vs `pre`/`max-width:none`), Markdown via code path + app launch (responding, full UIA tree; screenshots inconclusive = overlay promotion per `windows/AGENTS.md`). No visual regression in HTML; tree-row wrap-on change remains the only risk
  - win — release `2.10.10` (publish + `iscc installer/FluentNotepad.iss` + tag/release) when asked; version already bumped in csproj/iss/README
  - mac — mirror the intent: `macos/Sources/FluentNotepad/EditorView.swift`, `PreviewTree.swift`, `Markdown.swift`

## Done

(archived at each release — history in tags and release notes)
