# FluentNotepad

A Windows 11 Notepad clone in WinUI 3, with built-in Markdown preview.

![Markdown preview of BENCH.md](screenshot.png)

## Features

- Drag-reorderable **tabs**, `+` for a new tab, `*` for unsaved changes
- **Markdown preview** (WebView2 + Markdig): tables, task lists, code, quotes, links, local/remote/data-URI/SVG images — `.md` files open directly in Preview
- **Visual Studio-style colors** for headings, links, code, bold/italic, quotes and tables (light and dark themes)
- **Formatting toolbar** (heading, bold, italic, strikethrough, link, quotes, lists, code)
- System **Mica**, extended title bar with tabs inside, Edit/Preview toggle (Ctrl+Shift+V)
- Find (Ctrl+F), zoom, word wrap, file drag & drop
- Status bar like the original (line/column, characters, file type, zoom, CRLF/LF, UTF-8)
- Interface in **English and Italian** (follows the Windows display language)

## Requirements

- Windows 10 1809+ (Mica on Windows 11)
- WebView2 evergreen (preinstalled on Windows 11)
- To build: .NET 8 SDK

## Download

The repo contains sources only (builds and setup are **not** versioned). Two options:

- **Ready-made setup**: [Releases](../../releases) page → `FluentNotepad-Setup-2.0.10-win-x64.exe` (per-machine install in Program Files, optional `.md` association)
- **Build it yourself**:

```bash
dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
iscc installer\FluentNotepad.iss
```

The installer lands in `installer\dist\` (requires Inno Setup 6).

## Try without installing

```bash
# double-click avvia-debug.bat, or:
dotnet build source\FluentNotepad.WinUI.csproj -c Debug
```

## Structure

- `source/` — source code (WinUI 3, .NET 8): `MainWindow.*` (shell, tabs, editor, themes), `MarkdownHtml.cs` (Markdown → HTML), `CursorGrid.cs`, `Assets/`, `Properties/`, `Strings/` (EN/IT UI resources)
- `installer/` — Inno Setup 6 script (per-machine install in Program Files, optional `.md` association)
- `app/` — ready-to-run published build (**not versioned**, see `.gitignore`)
- `BENCH.md`, `dati-inventati.md` — sample files to try the preview (tables included)
- `screenshot.png` — the BENCH.md preview shown above

Technical details (in Italian) in [`source/README.md`](source/README.md).

## License

MIT — see [LICENSE](LICENSE).
