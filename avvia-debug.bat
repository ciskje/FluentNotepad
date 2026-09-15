@echo off
REM Avvia FluentNotepad (versione di Debug) - ricompila sempre per evitare build vecchie
cd /d "%~dp0"
set "EXE=FluentNotepad\bin\Debug\net8.0-windows\FluentNotepad.exe"
echo Compilazione...
dotnet build FluentNotepad\FluentNotepad.csproj -c Debug -v minimal
if errorlevel 1 (
    echo ERRORE di compilazione.
    pause
    exit /b 1
)
start "" "%EXE%" %*
