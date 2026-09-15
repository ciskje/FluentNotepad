@echo off
REM Avvia FluentNotepad WinUI 3 (versione di Debug, exe unpackaged)
cd /d "%~dp0"
set "EXE=FluentNotepad.WinUI\bin\Debug\net8.0-windows10.0.26100.0\win-x64\FluentNotepad.exe"
echo Compilazione...
dotnet build FluentNotepad.WinUI\FluentNotepad.WinUI.csproj -c Debug -v minimal
if errorlevel 1 (
    echo ERRORE di compilazione.
    pause
    exit /b 1
)
start "" "%EXE%" %*
