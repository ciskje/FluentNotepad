# FluentNotepad — demo Fluent Design in C# / WPF

## Versionamento (X.Y.Z)

La versione sta in `<Version>` nel `.csproj` ed è mostrata nel pannello Impostazioni.
Si aggiorna **solo** per modifiche al codice, così:

- **Z** (es. 1.0.0 → 1.0.1) — piccole modifiche: bugfix, ritocchi grafici, testi
- **Y** (es. 1.0.0 → 1.1.0) — modifiche importanti: nuove funzioni, cambi di comportamento
- **X** (es. 1.0.0 → 2.0.0) — stravolgimenti: architettura, riscritture, rotture di compatibilità

Per qualunque altra ragione (docs, refactor interni, build) la versione **non** si tocca.

Clone basilare del **Notepad di Windows 11**, scritto in C# con WPF (.NET 8), per mostrare:
finestra con angoli arrotondati + ombra, tab in stile Mica, flyout acrilici, temi chiaro/scuro, status bar.

## Mica reale (Windows 11 22H2+)

La striscia delle tab usa il Mica di sistema via DWM (`DwmBackdrop.cs`):
`DWMWA_SYSTEMBACKDROP_TYPE` + frame esteso a tutta la finestra.
Requisito scoperto sul campo: la finestra deve restare layered
(`AllowsTransparency`), altrimenti DWM disegna nero.
Su Windows 10 / 11 21H2 — o con `FLUENTNOTEPAD_NO_MICA=1` — fallback
automatico ai colori statici campionati dal Notepad originale.
Il tema scuro imposta anche `DWMWA_USE_IMMERSIVE_DARK_MODE`.

## Avvio

```bash
dotnet run --project FluentNotepad/FluentNotepad.csproj
```

Richiede **Windows** + .NET 8 SDK (WPF non gira su Linux/macOS).

## Funzioni
- **Markdown**: apre `.md`/`.markdown` con anteprima renderizzata (titoli, tabelle, codice, liste, checkbox, citazioni, link cliccabili, **immagini**), toggle Modifica/Anteprima (Ctrl+Maiusc+V), anteprima live durante la digitazione, temi chiaro/scuro
- **Immagini in Markdown**: percorsi relativi/assoluti/`file://` (relativi risolti dalla cartella del .md; `?query`/`#frammenti` tipo `?raw=true` ignorati), URL `http(s)` scaricati con cache (gli URL GitHub `.../blob/...` vengono tradotti in `raw.githubusercontent.com`), `data:` URI base64; file mancanti o non supportati (SVG) mostrano un segnaposto con il motivo; barra di formattazione con bottoni H/B/I/S/link/citazione/elenchi/codice
- **Icona personalizzata**: `icona.ico` usata per exe, taskbar e barra dell'app
- Schede multiple (Ctrl+T / Ctrl+W), indicatore `*` modifiche non salvate
- Nuovo / Apri / Salva / Salva con nome (Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Maiusc+S)
- Taglia/Copia/Incolla/Annulla/Ripeti, Trova con Ctrl+F (Invio = avanti, Maiusc+Invio = indietro)
- Zoom Ctrl++/Ctrl+-/Ctrl+0 + controlli in status bar
- A capo automatico, dimensione font, drag & drop di file .txt
- Tema chiaro/scuro automatico (legge `AppsUseLightTheme` di Windows) + toggle manuale
- Pannello Impostazioni a scomparsa, title bar personalizzata (trascina, doppio clic = massimizza)

## Struttura
- `App.xaml` → palette Fluent + stili (IconButton, WinButton, MenuBtn, FluentTabItem)
- `MainWindow.xaml` → layout: TitleBar / TabStrip / Editor / StatusBar + flyout Trova e Impostazioni
- `MainWindow.xaml.cs` → logica tab, file I/O, ricerca, zoom, temi, shortcut

## Idee per estenderlo
- Mica reale con P/Invoke `DwmSetWindowAttribute` + `SetWindowCompositionAttribute`
- Syntax highlighting, conteggio parole, stampa, cronologia recenti
- Porting a WinUI 3 per Mica/Acrylic nativi
