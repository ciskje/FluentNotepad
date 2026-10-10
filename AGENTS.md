# AGENTS.md — FluentNotepad

WinUI 3 / .NET 8, unpackaged (no MSIX). Clone del Notepad di Win11 con anteprima Markdown (WebView2 + Markdig).

## Build / run

```bash
dotnet build source/FluentNotepad.WinUI.csproj -c Debug
# oppure doppio clic su start-debug.bat (nella root)
source/bin/Debug/net8.0-windows10.0.26100.0/win-x64/FluentNotepad.exe [file]
```

Release + installer:

```bash
dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
iscc installer\FluentNotepad.iss   # richiede Inno Setup 6, output in installer\dist\
```

- `app/` (build published) e `installer/dist/` **non** sono versionati (vedi `.gitignore`).
- `dotnet build` dalla root del repo; il `RuntimeIdentifier` di default segue l'architettura host.

## Versionamento (X.Y.Z)

Policy in `source/README.md`: si tocca **solo** per modifiche al codice.

- Z = bugfix/ritocchi, Y = nuove funzioni/cambi di comportamento, X = stravolgimenti.
- Docs/refactor/build non cambiano la versione.
- Allineare in 3 punti: `<Version>` in `source/FluentNotepad.WinUI.csproj`, `#define MyAppVersion` in `installer/FluentNotepad.iss`, nome setup in `README.md`.

## Release su GitHub

La pagina Releases è il canale "ready-made setup" citato nel `README.md`:
**ogni bump di versione va pubblicato anche lì** (tag + release con setup allegato),
altrimenti la release Latest resta indietro rispetto al codice.

```bash
git tag vX.Y.Z && git push origin vX.Y.Z
gh release create vX.Y.Z "installer/dist/FluentNotepad-Setup-X.Y.Z-win-x64.exe" \
  --title "vX.Y.Z" --latest --notes "<riepilogo changelog>"
```

- L'exe va ricostruito prima se manca o è stale: `dotnet publish` + `iscc` (vedi sopra).
- Nelle note: riga install standard (Program Files per-machine, `.md` opzionale, WebView2) + novità rispetto alla release precedente (voci dal Changelog nel `README.md`).
- Stessa policy del versionamento: niente release per docs/refactor/build.
- Allineare il nome setup nella sezione Download del `README.md`.

## Lingua del sorgente

Sorgente tutto in inglese: commenti, docs (`source/README.md`), nomi file di prova.
Eccezione: le stringhe di interfaccia (`.resw` it-IT/en-US + fallback `x:Uid` in XAML),
che restano localizzate; le stringhe da codice passano da `Str()` + chiavi resw.

## Vincoli unpackaged (niente package identity)

- **Niente MRT/`ResourceLoader` da codice**: il costruttore lancia `FileNotFoundException`. Le stringhe da codice in `MainWindow.xaml.cs` (`Str()`, `LoadResw()`) leggono i `.resw` come XML da `Strings/<lingua>/` accanto all'exe. Gli `x:Uid` in XAML invece funzionano (MRT interno).
- **Niente `ApplicationData.LocalSettings`**: le impostazioni sono un file JSON in `%LocalAppData%\FluentNotepad\settings.json`.
- **WebView2**: `WEBVIEW2_USER_DATA_FOLDER` va impostato su `%LocalAppData%\FluentNotepad\WebView2` nel costruttore **prima** di qualunque inizializzazione, altrimenti in Program Files (non scrivibile) non parte mai.

## settings.json

Schema (indentato): `{theme, x, y, width, height, maximized, hasWindow}`.

- `theme`: `light|dark|system` (default `system` = segue Windows via registro `AppsUseLightTheme`).
- Geometria: salvata su `AppWindow.Changed` (debounce 1s via `DispatcherQueueTimer`) e su `AppWindow.Closing` in tutti i percorsi. Il vecchio file solo-`{"theme"}` resta leggibile.
- Ripristino in `RestoreWindowGeometry()` prima di `Activate()`: `MoveAndResize()` o `Maximize()`; fallback 1000x650 se assente/non valido/fuori da tutti gli schermi (`DisplayArea.FindAll()` vs `WorkArea`).
- Quando massimizzata, `AppWindow.Size/Position` riportano il monitor: i bounds di restore sono tenuti in cache (`_restoreBounds`, aggiornati su `Changed` solo con `OverlappedPresenter.State == Restored`). Lo stato minimizzato non viene mai persistito.

## Gotcha WinUI imparati

- `TextBox.Text` assegnare **dopo** `AcceptsReturn=true`, altrimenti taglia al primo `\n`.
- `TabView` non rimuove la tab da solo: rimozione manuale (vale anche per Annulla).
- `AppWindow.Closing` non ha deferral: pattern conferma con `Cancel=true` + `Close()` dopo i dialoghi (`_closeConfirmed`).
- `Microsoft.UI.Dispatching.DispatcherQueueTimer` va qualificato per esteso: `DispatcherQueueTimer` da solo risolve `Windows.System` e rompe la build (CS0029).
- Se la build C# fallisce, il compilatore XAML riporta errori a cascata fasulli (es. `Unknown type 'CursorGrid'`): guardare prima gli errori CS.
- Presenter di default di `Window` è già `OverlappedPresenter`; verificare `IsMaximizable` prima di `Maximize()`.
- Gli acceleratori da tastiera su un contenitore (es. `Root.KeyboardAccelerators`) mostrano il primo in un tooltip all'hover su tutta l'area: impostare `KeyboardAcceleratorPlacementMode.Hidden` sullo scope, gli shortcut restano attivi.
- Lingua UI: `PrimaryLanguageOverride = "it-IT"` per tag non it/en; en solo se sistema inglese (`MainWindow` ctor + `Str()`).

## Struttura

- `source/MainWindow.xaml(.cs)` — shell, tab, editor, menu, status, temi, geometria finestra.
- `source/MarkdownHtml.cs` — Markdown → HTML (Markdig + CSS chiaro/scuro + immagini data-URI).
- `source/CursorGrid.cs` — cursore busy per i render anteprima.
- `source/App.xaml(.cs)` — avvio + apertura file da riga di comando.
- `source/Strings/it-IT|en-US/Resources.resw` — risorse UI (copiate accanto all'exe via `.csproj`).
- `source/Assets/AppIcon.ico` — icona exe (Explorer) + `AppWindow.SetIcon` (runtime).
- `BENCH.md`, `dati-inventati.md` — file di prova per l'anteprima.
