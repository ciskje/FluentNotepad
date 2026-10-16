# AGENTS.md — FluentNotepad

WinUI 3 / .NET 8, unpackaged (no MSIX). Clone del Notepad di Win11 con anteprima Markdown (WebView2 + Markdig).

## Build / run

```bash
dotnet build source/FluentNotepad.WinUI.csproj -c Debug
# oppure doppio clic su start-debug.bat (in windows/)
source/bin/Debug/net8.0-windows10.0.26100.0/win-x64/FluentNotepad.exe [file]
```

Release + installer:

```bash
dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
iscc installer\FluentNotepad.iss   # richiede Inno Setup 6, output in installer\dist\
```

- `app/` (build published) e `installer/dist/` **non** sono versionati (vedi `.gitignore`).
- `dotnet build` da `windows/`; il `RuntimeIdentifier` di default segue l'architettura host.

## Versionamento (X.Y.Z)

Solo il dettaglio Windows (regole condivise in root `AGENTS.md`): allineare in
3 punti `<Version>` in `source/FluentNotepad.WinUI.csproj`, `#define MyAppVersion`
in `installer/FluentNotepad.iss`, nome setup nel `README.md` in root.

## Release su GitHub

Comando (il resto — note in inglese, ultime 2 per piattaforma, exe stale — in
root `AGENTS.md`):

```bash
git tag windows-vX.Y.Z && git push origin windows-vX.Y.Z
gh release create windows-vX.Y.Z "installer/dist/FluentNotepad-Setup-X.Y.Z-win-x64.exe" \
  --title "windows-vX.Y.Z" --latest --notes "<riepilogo changelog>"
```

## Vincoli unpackaged (niente package identity)

- **Niente MRT/`ResourceLoader` da codice**: il costruttore lancia `FileNotFoundException`. Le stringhe da codice in `MainWindow.xaml.cs` (`Str()`, `LoadResw()`) leggono i `.resw` come XML da `Strings/<lingua>/` accanto all'exe. Gli `x:Uid` in XAML invece funzionano (MRT interno).
- **Niente `ApplicationData.LocalSettings`**: le impostazioni sono un file JSON in `%LocalAppData%\FluentNotepad\settings.json`.
- **WebView2**: `WEBVIEW2_USER_DATA_FOLDER` va impostato su `%LocalAppData%\FluentNotepad\WebView2` nel costruttore **prima** di qualunque inizializzazione, altrimenti in Program Files (non scrivibile) non parte mai.

## settings.json

Schema (indentato): `{theme, x, y, width, height, maximized, hasWindow}`.
Condiviso tra le finestre (last-wins): due finestre aperte salvano a turno,
la geometria ripristinata è quella dell'ultima; le nuove finestre aprono in
cascata +36px per non impilarsi perfette (`App.NewWindow()`).

- `theme`: `light|dark|system` (default `system` = segue Windows via registro `AppsUseLightTheme`).
- Geometria: salvata su `AppWindow.Changed` (debounce 1s via `DispatcherQueueTimer`) e su `AppWindow.Closing` in tutti i percorsi. Il vecchio file solo-`{"theme"}` resta leggibile.
- Ripristino in `RestoreWindowGeometry()` prima di `Activate()`: `MoveAndResize()` o `Maximize()`; fallback 1000x650 se assente/non valido/fuori da tutti gli schermi (`DisplayArea.FindAll()` vs `WorkArea`).
- Quando massimizzata, `AppWindow.Size/Position` riportano il monitor: i bounds di restore sono tenuti in cache (`_restoreBounds`, aggiornati su `Changed` solo con `OverlappedPresenter.State == Restored`). Lo stato minimizzato non viene mai persistito.

## Gotcha WinUI imparati

- L'editor è un `RichEditBox`, non `TextBox` (`TextBox` non colora singoli range):
  testo via `ITextDocument` (`GetText` accoda un `\r` di fine storia e usa `\r`
  per gli a capo; normalizzare a `\n` per lavorare con gli span Markdig 1:1,
  riconvertire in CRLF/LF al salvataggio).
- Highlight edit-mode (`MarkdownHighlight.cs`, AST Markdig, stessa palette del
  preview) applicato in `TextChanging` (equivalente di EN_UPDATE): la
  formattazione entra nella stessa unità di undo della digitazione. Cache per
  chiave; skip in composizione IME, oltre 300.000 caratteri (come il Mac) e a
  controllo fuori dal visual tree.
- Formattazione applicata prima del primo `Loaded` non renderizza (il modello
  risulta colorato ma lo schermo resta bianco): repaint forzato sull'evento
  `Loaded` dell'editor, che scatta a ogni hosting.
- Mai sfondo `Transparent` sui range (`Color.FromArgb(0,0,0,0)`): RichEdit
  ignora l'alfa e lo rende nero (barre nere dietro ogni riga). Dipingere il
  colore di sfondo dell'editor (`#282828`/bianco, come `ApplyTheme`); i repaint
  su cambio tema lo riallineano.
- Anche i brush hover/focus del template (`TextControlBackgroundPointerOver`,
  `...Focused` + bordi) vanno neutralizzati per-controllo, altrimenti lo
  sfondo dell'editor lampeggia al passaggio del mouse.
- Undo is one Ctrl+Z for typing+highlight: the group opens on `KeyDown` and
  closes on `TextChanged` (`TextChanging` is too late, the keystroke is already
  its own unit); unpaired `Begin`/`EndUndoGroup` are no-ops. Never put
  Ctrl+Z/Y/X/C/V/A accelerators on menu items: they fire IN ADDITION to the
  native `RichEditBox` keys (double paste/cut/undo) — Click handlers stay.
- `ESC` è un acceleratore a livello finestra che flippa Modifica/Anteprima sui
  doc Markdown; cede se il focus è sulla find box (`FocusManager`) o IME attivo.
  I `ContentDialog` vivono fuori dall'albero di `Root`, il loro ESC non collide.
- File trascinato sopra il `WebView2` (che naviga via come Edge, prima degli
  handler XAML): `NavigationStarting` cancella le navigazioni `file://` (tranne
  il temp interno dell'anteprima) e apre il file come scheda.
- Ogni `MainWindow` è indipendente (schede, highlight, busy); condivide solo
  `settings.json` e il temp di anteprima (`fluent_preview_<pid>.html`).
- `TabView` non rimuove la tab da solo: rimozione manuale (vale anche per Annulla).
- `AppWindow.Closing` non ha deferral: pattern conferma con `Cancel=true` + `Close()` dopo i dialoghi (`_closeConfirmed`).
- `Microsoft.UI.Dispatching.DispatcherQueueTimer` va qualificato per esteso: `DispatcherQueueTimer` da solo risolve `Windows.System` e rompe la build (CS0029).
- Se la build C# fallisce, il compilatore XAML riporta errori a cascata fasulli (es. `Unknown type 'CursorGrid'`): guardare prima gli errori CS.
- Presenter di default di `Window` è già `OverlappedPresenter`; verificare `IsMaximizable` prima di `Maximize()`.
- Gli acceleratori da tastiera su un contenitore (es. `Root.KeyboardAccelerators`) mostrano il primo in un tooltip all'hover su tutta l'area: impostare `KeyboardAcceleratorPlacementMode.Hidden` sullo scope, gli shortcut restano attivi.
- Lingua UI: `PrimaryLanguageOverride = "it-IT"` per tag non it/en; en solo se sistema inglese (`MainWindow` ctor + `Str()`).

## Verifica GUI senza interazione

- Avvio smoke: lanciare l'exe con un file (`FluentNotepad.exe source\example.md &`),
  attendere, screenshot con `CopyFromScreen` via PowerShell, killare il processo.
- Toggle Modifica/Anteprima e focus via UI Automation (Invoke sul pulsante,
  `SetFocus` sull'Edit); `SendKeys`/`AppActivate` sono inaffidabili da soli.
- Lo stato del documento si interroga via TextPattern (selezione, attributi);
  per i colori reali fidarsi solo dello screenshot, non del read-back COM.

## Struttura

- `source/MainWindow.xaml(.cs)` — shell, tab, editor (`RichEditBox`), menu, status, temi, geometria finestra.
- `source/MarkdownHtml.cs` — Markdown → HTML (Markdig + CSS chiaro/scuro + immagini data-URI).
- `source/MarkdownHighlight.cs` — colori Markdown in edit mode (AST Markdig → range `ITextDocument`, stessa palette del preview).
- `source/CursorGrid.cs` — cursore busy per i render anteprima.
- `source/App.xaml(.cs)` — avvio + apertura file da riga di comando + finestre extra in cascata.
- `source/Strings/it-IT|en-US/Resources.resw` — risorse UI (copiate accanto all'exe via `.csproj`).
- `source/Assets/AppIcon.ico` — icona exe (Explorer) + `AppWindow.SetIcon` (runtime).
- `source/example.md` + root `BENCH.md` — file di prova per l'anteprima.
