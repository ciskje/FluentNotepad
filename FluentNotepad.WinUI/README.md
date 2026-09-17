# FluentNotepad 2.x — clone del Notepad di Windows 11 in WinUI 3

## Versionamento (X.Y.Z)

La versione sta in `<Version>` nel `.csproj` ed è mostrata nel pannello Impostazioni.
Si aggiorna **solo** per modifiche al codice, così:

- **Z** (es. 2.0.0 → 2.0.1) — piccole modifiche: bugfix, ritocchi grafici, testi
- **Y** (es. 2.0.0 → 2.1.0) — modifiche importanti: nuove funzioni, cambi di comportamento
- **X** (es. 2.0.0 → 3.0.0) — stravolgimenti: architettura, riscritture, rotture di compatibilità

Per qualunque altra ragione (docs, refactor interni, build) la versione **non** si tocca.

## Avvio

```bash
# doppio clic su avvia-debug.bat, oppure:
dotnet build FluentNotepad.WinUI/FluentNotepad.WinUI.csproj -c Debug
FluentNotepad.WinUI/bin/Debug/net8.0-windows10.0.26100.0/win-x64/FluentNotepad.exe [file]
```

Exe **unpackaged** (niente MSIX): si avvia standalone, richiede solo il runtime
WebView2 evergreen (preinstallato su Windows 11). Richiede Windows 10 1809+,
Mica su Windows 11.

## Funzioni

- **TabView nativo**: schede riordinabili col drag, `+` dopo l'ultima tab, `*` modifiche
- **Markdown**: apre `.md` con anteprima **WebView2 + Markdig** (tabelle, task list,
  codice, citazioni, link, **immagini locali/remote/data-URI/SVG**), toggle
  Modifica/Anteprima (Ctrl+Maiusc+V), anteprima live, temi chiaro/scuro
- **Barra di formattazione** (H/B/I/S/link/citazione/elenchi/codice) che inserisce sintassi
- **Mica di sistema** (`MicaBackdrop` BaseAlt, zero P/Invoke), title bar estesa con tab dentro
- Menu File/Modifica/Visualizza con shortcut, ricerca (Ctrl+F), zoom, a capo, drag & drop
- Status bar come l'originale (riga/col, caratteri, tipo, zoom, CRLF, UTF-8)
- Icona `Assets/AppIcon.ico` (exe, taskbar, title bar)

## Struttura (FluentNotepad.WinUI/)

- `MainWindow.xaml` / `.xaml.cs` — shell, tab, editor, menu, status, temi, scorciatoie
- `MarkdownHtml.cs` — Markdown → HTML (Markdig + CSS chiaro/scuro + immagini data-URI)
- `App.xaml(.cs)` — avvio + apertura file da riga di comando
- `esempio.md` / `esempio.png` — file di prova

## Note tecniche apprese

- WinUI `TextBox.Text` va assegnato **dopo** `AcceptsReturn=true`, altrimenti taglia al primo `\n`
- Niente `FlowDocument`: le API clipboard (`Cut`/`Copy`/`Paste`) non esistono su `TextBox` WinUI,
  implementate via `Windows.ApplicationModel.DataTransfer`
- `AppWindow.Closing` non ha deferral: pattern conferma-con-`Cancel=true` + `Close()` dopo i dialoghi
