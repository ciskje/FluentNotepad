@echo off
REM Avvia FluentNotepad WinUI 3 (versione di Debug, exe unpackaged)
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"
)
set "EXE=source\bin\Debug\net8.0-windows10.0.26100.0\win-x64\FluentNotepad.exe"
echo Compilazione...
dotnet build source\FluentNotepad.WinUI.csproj -c Debug -v minimal
if errorlevel 1 (
    echo ERRORE di compilazione.
    pause
    exit /b 1
)
start "" "%EXE%" %*
