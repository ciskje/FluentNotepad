# FluentNotepad

Clone del Blocco note di Windows 11 in WinUI 3, con anteprima Markdown integrata.

![Anteprima Markdown di BENCH.md](screenshot.png)

## Funzioni

- **Schede** riordinabili col drag, `+` per la nuova scheda, `*` per le modifiche non salvate
- **Anteprima Markdown** (WebView2 + Markdig): tabelle, task list, codice, citazioni, link, immagini locali/remote/data-URI/SVG — i `.md` si aprono direttamente in Anteprima
- **Colori stile Visual Studio** per titoli, link, codice, grassetto/corsivo, citazioni e tabelle (temi chiaro e scuro)
- **Barra di formattazione** (titolo, grassetto, corsivo, barrato, link, citazioni, elenchi, codice)
- **Mica** di sistema, title bar estesa con le schede dentro, toggle Modifica/Anteprima (Ctrl+Maiusc+V)
- Ricerca (Ctrl+F), zoom, a capo automatico, drag & drop dei file
- Status bar come l'originale (riga/colonna, caratteri, tipo file, zoom, CRLF/LF, UTF-8)

## Requisiti

- Windows 10 1809+ (Mica su Windows 11)
- WebView2 evergreen (preinstallato su Windows 11)
- Per compilare: .NET 8 SDK

## Download

Nel repo ci sono solo i sorgenti (build e setup **non** versionati). Due strade:

- **Setup pronto**: pagina [Releases](../../releases) → `FluentNotepad-Setup-2.0.10-win-x64.exe` (installazione per tutti gli utenti in Program Files, associazione `.md` opzionale)
- **Compilalo tu**:

```bash
dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
iscc installer\FluentNotepad.iss
```

L'installer finisce in `installer\dist\` (richiede Inno Setup 6).

## Prova senza installare

```bash
# doppio clic su avvia-debug.bat, oppure:
dotnet build source\FluentNotepad.WinUI.csproj -c Debug
```

## Struttura

- `source/` — codice sorgente (WinUI 3, .NET 8): `MainWindow.*` (shell, schede, editor, temi), `MarkdownHtml.cs` (Markdown → HTML), `CursorGrid.cs`, `Assets/`, `Properties/`
- `installer/` — script Inno Setup 6 (installazione per tutti gli utenti in Program Files, associazione `.md` opzionale)
- `app/` — build pubblicata pronta all'uso (**non versionata**, vedi `.gitignore`)
- `BENCH.md`, `dati-inventati.md` — file di esempio per provare l'anteprima (tabelle comprese)
- `screenshot.png` — l'anteprima di BENCH.md mostrata sopra

Dettagli tecnici in [`source/README.md`](source/README.md).

## Licenza

Da definire.
