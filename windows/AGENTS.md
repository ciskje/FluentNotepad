# AGENTS.md — FluentNotepad

WinUI 3 / .NET 8, unpackaged (no MSIX). Windows 11 Notepad clone with Markdown preview (WebView2 + Markdig).

## Build / run

```bash
dotnet build source/FluentNotepad.WinUI.csproj -c Debug
# or double-click start-debug.bat (in windows/)
source/bin/Debug/net8.0-windows10.0.26100.0/win-x64/FluentNotepad.exe [file]
```

Release + installer:

```bash
dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
iscc installer\FluentNotepad.iss   # requires Inno Setup 6, output in installer\dist\
```

- `app/` (local publish output, never commit) and `installer/dist/`
  (not versioned, see `.gitignore`).
- `dotnet build` from `windows/`; the default `RuntimeIdentifier` follows the host architecture.

## Versioning (X.Y.Z)

Windows-only detail (shared rules in root `AGENTS.md`): single source is
`<Version>` in `source/FluentNotepad.WinUI.csproj` — `#define MyAppVersion`
in `installer/FluentNotepad.iss` and the setup name in the root `README.md`
derive from it at build/release time (never bumped for docs/refactor/build).

## GitHub releases

Command (the rest — English notes, last 2 per platform, stale exe — in
root `AGENTS.md`):

```bash
git tag windows-vX.Y.Z && git push origin windows-vX.Y.Z
gh release create windows-vX.Y.Z "installer/dist/FluentNotepad-Setup-X.Y.Z-win-x64.exe" \
  --title "windows-vX.Y.Z" --latest --notes "<changelog summary>"
```

## Unpackaged constraints (no package identity)

- **No MRT/`ResourceLoader` from code**: the constructor throws `FileNotFoundException`. Code-behind strings in `MainWindow.xaml.cs` (`Str()`, `LoadResw()`) read the `.resw` files as XML from `Strings/<language>/` next to the exe. `x:Uid` in XAML works instead (internal MRT).
- **No `ApplicationData.LocalSettings`**: settings are a JSON file at `%LocalAppData%\FluentNotepad\settings.json`.
- **WebView2**: set `WEBVIEW2_USER_DATA_FOLDER` to `%LocalAppData%\FluentNotepad\WebView2` in the constructor **before** any initialization, otherwise it never starts under Program Files (not writable).

## settings.json

Schema (indented): `{theme, x, y, width, height, maximized, hasWindow}`.
Shared across windows (last-wins): two open windows save in turn, the restored
geometry is the last one; new windows cascade +36px so they never stack
pixel-perfect (`App.NewWindow()`).

- `theme`: `light|dark|system` (default `system` = follows Windows via the `AppsUseLightTheme` registry value).
- Geometry: saved on `AppWindow.Changed` (1s debounce via `DispatcherQueueTimer`) and on `AppWindow.Closing` on all paths. The old theme-only `{"theme"}` file stays readable.
- Restore in `RestoreWindowGeometry()` before `Activate()`: `MoveAndResize()` or `Maximize()`; 1000x650 fallback when missing/invalid/off every screen (`DisplayArea.FindAll()` vs `WorkArea`).
- When maximized, `AppWindow.Size/Position` report the monitor: restore bounds are cached (`_restoreBounds`, updated on `Changed` only with `OverlappedPresenter.State == Restored`). The minimized state is never persisted.

## WinUI gotchas learned

- The editor is a `RichEditBox`, not a `TextBox` (`TextBox` cannot color individual ranges):
  text via `ITextDocument` (`GetText` appends a story-final `\r` and uses `\r`
  for line breaks; normalize to `\n` to index Markdig spans 1:1, convert back
  to CRLF/LF on save).
- Edit-mode highlighting (`MarkdownHighlight.cs`, Markdig AST, same palette as
  the preview) applied in `TextChanging` (the EN_UPDATE equivalent): formatting
  joins the user's own undo unit. Keyed cache; skipped during IME composition,
  over 300,000 chars (like the Mac) and with the control outside the visual tree.
- Formatting applied before the first `Loaded` doesn't render (the model looks
  colored but the screen stays white): forced repaint on the editor's `Loaded`
  event, which fires on every hosting.
- Never a `Transparent` background on ranges (`Color.FromArgb(0,0,0,0)`): RichEdit
  ignores the alpha and renders it black (black bars behind every line). Paint
  the editor background instead (`#282828`/white, like `ApplyTheme`); repaints
  on theme change realign it.
- Template hover/focus brushes (`TextControlBackgroundPointerOver`,
  `...Focused` + borders) must also be neutralized per-control, otherwise the
  editor background flashes on mouse hover.
- Undo is one Ctrl+Z for typing+highlight: the group opens on `KeyDown` and
  closes on `TextChanged` (`TextChanging` is too late, the keystroke is already
  its own unit); unpaired `Begin`/`EndUndoGroup` are no-ops. Never put
  Ctrl+Z/Y/X/C/V/A accelerators on menu items: they fire IN ADDITION to the
  native `RichEditBox` keys (double paste/cut/undo) — Click handlers stay.
- `ESC` is a window-level accelerator flipping Edit/Preview on Markdown docs;
  it yields when the find box has focus (`FocusManager`) or IME is active.
  `ContentDialog`s live outside `Root`'s tree, so their ESC never collides.
- A file dropped onto the `WebView2` (which navigates away like Edge, before any
  XAML handler): `NavigationStarting` cancels `file://` navigations (except the
  JSON tree preview's own temp file, allow-listed by path) and opens the file as
  a tab instead.
- JSON tree preview is served from a temp file
  (`%LocalAppData%\FluentNotepad\preview\json-preview-<pid>.html`, `?v=`-busted):
  `NavigateToString` caps HTML at 2 MB and the tree payload can exceed the source.
- Each `MainWindow` is independent (tabs, highlight, busy); they share only
  `settings.json` and the preview temp file (`fluent_preview_<pid>.html`).
- `TabView` never removes the tab by itself: remove manually (also on Cancel).
- `AppWindow.Closing` has no deferral: confirm-with-`Cancel=true` + `Close()` pattern after dialogs (`_closeConfirmed`).
- `Microsoft.UI.Dispatching.DispatcherQueueTimer` must be fully qualified: bare `DispatcherQueueTimer` resolves `Windows.System` and breaks the build (CS0029).
- If the C# build fails, the XAML compiler reports bogus cascading errors (e.g. `Unknown type 'CursorGrid'`): look at the CS errors first.
- A `Window`'s default presenter is already `OverlappedPresenter`; check `IsMaximizable` before `Maximize()`.
- Keyboard accelerators on a container (e.g. `Root.KeyboardAccelerators`) show the first one in a hover tooltip over the whole area: set `KeyboardAcceleratorPlacementMode.Hidden` on the scope, shortcuts stay active.
- UI language: `PrimaryLanguageOverride = "it-IT"` for non-it/en tags; English only on English systems (`MainWindow` ctor + `Str()`).

## Headless GUI checks

- Smoke start: launch the exe with a file (`FluentNotepad.exe ..\README.md &`),
  wait, screenshot with `CopyFromScreen` via PowerShell, kill the process.
- Edit/Preview toggle and focus via UI Automation (Invoke on the button,
  `SetFocus` on the Edit); `SendKeys`/`AppActivate` alone are unreliable.
- Query document state via TextPattern (selection, attributes); for real colors
  trust only the screenshot, never the COM read-back.
