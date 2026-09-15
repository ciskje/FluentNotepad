# FluentNotepad — demo Fluent Design in C# / WPF

Clone basilare del **Notepad di Windows 11**, scritto in C# con WPF (.NET 8), per mostrare:
finestra con angoli arrotondati + ombra, tab in stile Mica, flyout acrilici, temi chiaro/scuro, status bar.

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
