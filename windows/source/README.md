# FluentNotepad 2.x — Windows 11 Notepad clone in WinUI 3

## Versioning (X.Y.Z)

The version lives in `<Version>` in the `.csproj` and is shown in the Settings panel.
It is updated **only** for code changes, as follows:

- **Z** (e.g. 2.0.0 → 2.0.1) — small changes: bugfixes, visual tweaks, texts
- **Y** (e.g. 2.0.0 → 2.1.0) — notable changes: new features, behavior changes
- **X** (e.g. 2.0.0 → 3.0.0) — upheavals: architecture, rewrites, breaking changes

For any other reason (docs, internal refactors, build) the version is **not** touched.

## Run

```bash
# double-click start-debug.bat (windows/), or from windows/:
dotnet build source/FluentNotepad.WinUI.csproj -c Debug
source/bin/Debug/net8.0-windows10.0.26100.0/win-x64/FluentNotepad.exe [file]
```

**Unpackaged** exe (no MSIX): runs standalone, only needs the
WebView2 evergreen runtime (preinstalled on Windows 11). Requires Windows 10 1809+,
Mica on Windows 11.

## Features

- **Native TabView**: drag-reorderable tabs, `+` after the last tab, `*` for dirty
- **Markdown**: opens `.md` with a **WebView2 + Markdig** preview (tables, task lists,
  code, quotes, links, **local/remote/data-URI/SVG images**), Edit/Preview
  toggle (Ctrl+Shift+V, or ESC), live preview, light/dark themes
- **Edit-mode highlighting**: same VS-style colors as the preview, applied live
  while typing (Markdig AST, `RichEditBox`)
- **Formatting toolbar** (H/B/I/S/link/quote/lists/code) inserting syntax
- **System Mica** (`MicaBackdrop` BaseAlt, tinted, zero P/Invoke), extended title bar with tabs inside
- File/Edit/View menus with shortcuts, new windows (Ctrl+Shift+N), find (Ctrl+F), zoom, word wrap, drag & drop
- Status bar like the original (line/col, characters, type, zoom, CRLF, UTF-8)
- `Assets/AppIcon.ico` icon (exe, taskbar, title bar)

## Layout (source/)

- `MainWindow.xaml` / `.xaml.cs` — shell, tabs, editor, menus, status, themes, shortcuts
- `MarkdownHtml.cs` — Markdown → HTML (Markdig + light/dark CSS + data-URI images)
- `MarkdownHighlight.cs` — Markdown colors in edit mode (Markdig AST → `RichEditBox` ranges, same palette as the preview)
- `App.xaml(.cs)` — startup + command-line file open + extra windows (cascaded)
- `example.md` / `example.png` — sample files

## Technical notes

- The editor is a WinUI `RichEditBox` (a plain `TextBox` cannot color individual
  ranges): Markdown colors come from the Markdig AST (`MarkdownHighlight.cs`,
  same VS palette as the preview) and are applied in `TextChanging`, so the
  formatting joins the user's undo unit instead of becoming a separate undo step
- No `FlowDocument`: the menu/toolbar `Cut`/`Copy`/`Paste` use
  `Windows.ApplicationModel.DataTransfer` with the remembered selection
  (clicking a button moves focus, so the live selection is cached)
- `ESC` is a window-level accelerator flipping Edit/Preview on Markdown docs
  (the find box keeps its own ESC); a file dropped onto the WebView2 preview
  would navigate away like Edge, so `NavigationStarting` cancels `file://`
  navigations and opens the file as a tab instead
- Click-to-source (like the Mac): `MarkdownHtml` annotates every rendered block
  with `data-src-line` in AST order (raw HTML blanked first); a document-start
  script posts the nearest one, `WebMessageReceived` jumps the caret there.
  Preview links jump to source instead of navigating (same tradeoff as the Mac).
- `AppWindow.Closing` has no deferral: confirm-with-`Cancel=true` + `Close()` pattern after dialogs
